using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Per-frame dynamic content for the GPU renderer, mirroring the CPU draw
/// order per presentation schema: pitched/WaveTable lanes draw note bars,
/// FM3 groups add operator ribbons, sample lanes draw playback voice bands,
/// and Noise/aggregate/rhythm/generic panels draw their activity strips. The
/// GPU path uses only high-level Skia primitives — no per-pixel APIs.
/// </summary>
internal sealed partial class GpuPanelRenderer
{
    private void DrawDynamicPanels(long currentSample)
    {
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PreparedPanel panel = _panels[panelIndex];
            switch (panel.Schema)
            {
                case PanelPresentationSchema.PitchedLane:
                    DrawPitchedNotes(panelIndex, panel, currentSample, windowStart, windowEnd);
                    break;
                case PanelPresentationSchema.WaveTableLane:
                    DrawPitchedNotes(panelIndex, panel, currentSample, windowStart, windowEnd);
                    DrawWaveTableViewport(panelIndex, panel, currentSample);
                    break;
                case PanelPresentationSchema.FmOperatorGroup:
                    DrawPitchedNotes(panelIndex, panel, currentSample, windowStart, windowEnd);
                    DrawFm3OperatorNotes(panelIndex, panel, currentSample, windowStart, windowEnd);
                    break;
                case PanelPresentationSchema.SampleLane:
                    DrawSamplePlaybackLane(panelIndex, panel, currentSample, windowStart, windowEnd);
                    break;
                case PanelPresentationSchema.NoiseLane:
                    DrawNoiseLane(panelIndex, panel, currentSample, windowStart, windowEnd);
                    break;
                case PanelPresentationSchema.AggregateActivity:
                    DrawAggregateCells(panelIndex, panel, currentSample);
                    break;
                case PanelPresentationSchema.PercussionRows:
                    DrawRhythmHits(panelIndex, panel, currentSample, windowStart, windowEnd);
                    break;
                case PanelPresentationSchema.GenericLane:
                    DrawGenericActivity(panelIndex, panel, currentSample, windowStart, windowEnd);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Pitched / WaveTable / FM3 note bars.
    // ------------------------------------------------------------------

    private void DrawPitchedNotes(
        int panelIndex, PreparedPanel panel, long currentSample, long windowStart, long windowEnd)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(
            panelIndex, panel.Schema == PanelPresentationSchema.FmOperatorGroup);
        if (lane.Width <= 0 || lane.Height <= 0)
            return;
        (double minMidi, double maxMidi) = PitchRangeOf(panel);
        int playheadX = _layout.GetPlayheadX(panelIndex);
        PreparedNote[] notes = panel.MainNotes;
        int first = FindFirstVisibleNote(notes, windowStart);
        for (int index = first; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample >= windowEnd)
                break;
            if (note.EndSample <= windowStart)
                continue;
            if (IsSsgNoiseOnly(note.Mode))
            {
                DrawSsgNoiseStrip(panel, note, lane, currentSample);
                continue;
            }
            if (_performance.Enabled)
                _performance.VisibleNotesVisited++;
            DrawNoteBar(panel, note, lane, currentSample, windowStart, windowEnd, minMidi, maxMidi, playheadX);
        }
    }

    /// <summary>
    /// One note bar: body ribbon (start-pitch anchored), onset cap, retrigger
    /// accent bar, release end caps, active pitch marker and envelope token —
    /// the CPU ribbon grammar expressed with filled rects.
    /// </summary>
    private void DrawNoteBar(
        PreparedPanel panel,
        PreparedNote note,
        OverlayRect lane,
        long currentSample,
        long windowStart,
        long windowEnd,
        double minMidi,
        double maxMidi,
        int playheadX)
    {
        if (note.InitialMidiNote < 0)
            return;

        double leftX = _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane);
        double rightX = _layout.SampleToX(note.EndSample, currentSample, _timeline.SampleRate, lane);
        int left = Math.Max(lane.X, (int)Math.Floor(leftX));
        int right = Math.Min(lane.Right, (int)Math.Ceiling(rightX));
        const int minimumWidth = 3;
        if (right - left < minimumWidth)
        {
            int missing = minimumWidth - (right - left);
            int growRight = Math.Min(missing, lane.Right - right);
            right += growRight;
            missing -= growRight;
            left = Math.Max(lane.X, left - missing);
        }
        if (right <= left)
            return;

        bool active = note.StartSample <= currentSample && currentSample < note.EndSample;
        int ribbonHeight = PreferredRibbonHeight(lane, minMidi, maxMidi);
        int half = Math.Max(1, ribbonHeight / 2);
        OverlayColor ribbonFill = active ? note.ActiveFill : note.Fill;
        if (note.Pitch.Length == 0)
        {
            int yCentreTop = MidiToY(NotePitchAt(note, note.StartSample), minMidi, maxMidi, lane);
            int yTop = Math.Clamp(yCentreTop - half, lane.Y, lane.Bottom - 1);
            int yBottom = Math.Clamp(yCentreTop + (ribbonHeight - half), lane.Y, lane.Bottom);
            if (yBottom > yTop)
                FillRect(new OverlayRect(left, yTop, right - left, yBottom - yTop), ribbonFill);
        }
        else
        {
            // Pitch-following ribbon: one batched path containing one rect per
            // ZOH pitch segment (identical pixels to per-segment FillRect, but
            // a single draw call — up to ~200 calls per vibrato note before).
            using SKPath ribbonPath = new SKPath();
            double prevPitch = note.InitialMidiNote;
            double segStartX = leftX;
            int pitchIdx = -1;
            {
                long sp = (long)Math.Round(Math.Clamp(
                    windowStart + (segStartX - lane.X) * (_layout.WindowSeconds * _timeline.SampleRate / lane.Width),
                    note.StartSample, note.EndSample));
                int lo = 0, hi = note.Pitch.Length;
                while (lo < hi) { int mid = lo + (hi - lo) / 2; if (note.Pitch[mid].SamplePosition <= sp) lo = mid + 1; else hi = mid; }
                pitchIdx = lo - 1;
                if (pitchIdx >= 0) prevPitch = note.Pitch[pitchIdx].MidiNote;
            }
            for (int pi = pitchIdx + 1; pi < note.Pitch.Length; pi++)
            {
                long ptSample = note.Pitch[pi].SamplePosition;
                if (ptSample < note.StartSample || ptSample > note.EndSample) continue;
                double xThresh = _layout.SampleToX(ptSample, currentSample, _timeline.SampleRate, lane);
                if (xThresh <= segStartX) continue;
                if (xThresh > rightX) break;
                // Merge sub-pixel segments: floor/ceil inflate a 0.5px step to a
                // 2px rect, so a 200-point LFO at low zoom would bloat the path
                // with overlapping slivers. Hold emission until the accumulated
                // span crosses a pixel boundary; the merged segment uses the
                // latest pitch (visually indistinguishable at that scale).
                if (Math.Ceiling(xThresh) - Math.Floor(segStartX) <= 1.0)
                {
                    prevPitch = note.Pitch[pi].MidiNote;
                    pitchIdx = pi;
                    continue;
                }
                AddRibbonSegment(ribbonPath, segStartX, xThresh, prevPitch, minMidi, maxMidi, lane, half, ribbonHeight);
                segStartX = xThresh;
                prevPitch = note.Pitch[pi].MidiNote;
                pitchIdx = pi;
            }
            AddRibbonSegment(ribbonPath, segStartX, rightX, prevPitch, minMidi, maxMidi, lane, half, ribbonHeight);
            if (!ribbonPath.IsEmpty)
                DrawPathFill(ribbonPath, ribbonFill);
            // Smooth center trace for vibrato: only for the active note at the
            // playhead (1–2 notes per frame) to keep cost bounded. Inactive past
            // notes keep the ZOH stepped ribbon (visible but not smoothed).
            if (active && right - left > 12 && note.Pitch.Length >= 8)
                DrawPitchTrace(note, lane, currentSample, left, right, minMidi, maxMidi, windowStart);
        }

        // Onset cap (§8.5): bright opaque block with an accent border. The
        // enlarged state lasts ~110 ms after the onset. Retriggers draw a thin
        // accent bar before the block.
        bool onsetVisible = note.StartSample >= windowStart;
        int capX = (int)Math.Round(leftX);
        if (onsetVisible && capX >= lane.X && capX < lane.Right)
        {
            int capWidth = Math.Max(3, lane.Width / 100);
            long ageSamples = currentSample - note.StartSample;
            bool enlarged = ageSamples >= 0 && ageSamples < (long)Math.Round(110 * _timeline.SampleRate / 1000.0);
            int capHeight = ribbonHeight + (enlarged ? 4 : 2);
            int capCentreY = MidiToY(NotePitchAt(note, note.StartSample), minMidi, maxMidi, lane);
            int capTop = Math.Clamp(capCentreY - capHeight / 2, lane.Y, lane.Bottom - 1);
            int capBottom = Math.Clamp(capCentreY + (capHeight - capHeight / 2), lane.Y, lane.Bottom);
            if (capBottom > capTop)
            {
                int blockX = capX;
                if (note.IsRetrigger)
                {
                    DrawVerticalLine(capX, capTop, capBottom - 1, panel.Accent);
                    blockX = Math.Min(lane.Right - 1, capX + 1);
                }
                int capFillWidth = capWidth + (enlarged ? 1 : 0);
                int blockLeft = Math.Max(lane.X, blockX);
                int blockRight = Math.Min(lane.Right, blockX + capFillWidth);
                if (blockRight > blockLeft)
                {
                    FillRect(new OverlayRect(blockLeft, capTop, blockRight - blockLeft, capBottom - capTop), note.CapFill);
                    DrawHorizontalLine(blockLeft, blockRight - 1, capTop, panel.Accent);
                    DrawHorizontalLine(blockLeft, blockRight - 1, capBottom - 1, panel.Accent);
                    DrawVerticalLine(blockLeft, capTop, capBottom - 1, panel.Accent);
                    DrawVerticalLine(blockRight - 1, capTop, capBottom - 1, panel.Accent);
                }
            }
        }

        // Release endings (§8.6): hard key-offs get a flat end cap; notes
        // continuing past the window get a clipped end marker at the edge.
        if (note.EndSample < windowEnd)
        {
            if (note.ReleaseStyle == NoteReleaseStyle.HardKeyOff)
            {
                int endX = (int)Math.Round(rightX);
                if (endX >= lane.X && endX < lane.Right)
                {
                    int endCentreY = MidiToY(NotePitchAt(note, note.EndSample), minMidi, maxMidi, lane);
                    int endTop = Math.Clamp(endCentreY - (ribbonHeight + 1) / 2 - 1, lane.Y, lane.Bottom - 1);
                    int endBottom = Math.Clamp(endCentreY + (ribbonHeight + 2) / 2 + 1, lane.Y, lane.Bottom);
                    if (endBottom > endTop)
                        FillRect(
                            new OverlayRect(endX, endTop, Math.Min(2, lane.Right - endX), endBottom - endTop),
                            note.CapFill);
                }
            }
        }
        else
        {
            int markerCentreY = MidiToY(NotePitchAt(note, note.EndSample), minMidi, maxMidi, lane);
            int markerTop = Math.Clamp(markerCentreY - (ribbonHeight + 1) / 2 - 1, lane.Y, lane.Bottom - 1);
            int markerBottom = Math.Clamp(markerCentreY + (ribbonHeight + 2) / 2 + 1, lane.Y, lane.Bottom);
            if (markerBottom > markerTop)
                DrawVerticalLine(lane.Right - 1, markerTop, markerBottom - 1, note.CapFill.WithAlpha(130));
        }

        // Active pitch marker at the playhead (small bright contact circle).
        if (!IsSsgMode(note.Mode) && active)
        {
            double activeMidi = NotePitchAt(note, currentSample);
            if (activeMidi >= 0)
            {
                int markerY = MidiToY(activeMidi, minMidi, maxMidi, lane);
                if (markerY >= lane.Y && markerY <= lane.Bottom && playheadX >= lane.X && playheadX < lane.Right)
                    DrawCircle(playheadX, markerY, 3f, panel.Accent.Lighten(0.5));
            }
        }

        // SSG envelope token: "E" rides the ribbon after the onset cap.
        if (note.Mode is VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeToneNoise)
        {
            int labelX = left + (onsetVisible ? Math.Max(3, lane.Width / 100) + 2 : 2);
            int labelY = Math.Clamp(
                MidiToY(NotePitchAt(note, note.StartSample), minMidi, maxMidi, lane) - ribbonHeight / 2,
                lane.Y,
                lane.Bottom - 1);
            if (labelX < right - 8)
                DrawTextAt("E", labelX, labelY, TextSizeScale1, BrightText);
        }
    }

    /// <summary>
    /// Appends one ZOH ribbon segment (x interval at a fixed pitch, clamped to
    /// the lane) to the batched path. Empty segments are skipped.
    /// </summary>
    private static void AddRibbonSegment(
        SKPath path,
        double segStartX,
        double segEndX,
        double pitch,
        double minMidi,
        double maxMidi,
        OverlayRect lane,
        int half,
        int ribbonHeight)
    {
        int segLeft = Math.Max(lane.X, (int)Math.Floor(segStartX));
        int segRight = Math.Min(lane.Right, (int)Math.Ceiling(segEndX));
        if (segRight <= segLeft)
            return;
        int yC = MidiToY(pitch, minMidi, maxMidi, lane);
        int yT = Math.Clamp(yC - half, lane.Y, lane.Bottom - 1);
        int yB = Math.Clamp(yC + (ribbonHeight - half), lane.Y, lane.Bottom);
        if (yB <= yT)
            return;
        path.AddRect(new SKRect(segLeft, yT, segRight, yB));
    }

    /// <summary>Fills a path with the shared fill paint in one draw call.</summary>
    private void DrawPathFill(SKPath path, OverlayColor color)
    {
        _fillPaint.Color = ToSk(color);
        Canvas.DrawPath(path, _fillPaint);
    }

    private void DrawPitchTrace(
        PreparedNote note,
        OverlayRect lane,
        long currentSample,
        int left,
        int right,
        double minMidi,
        double maxMidi,
        long windowStart)
    {
        if (right - left < 8 || note.Pitch.Length < 4) return;
        double samplesPerPixel = _layout.WindowSeconds * _timeline.SampleRate / lane.Width;
        if (samplesPerPixel <= 0) return;

        // Per-pitch-point path: linear between sampled vibrato points is
        // exactly the interpolated model, but with ~50x fewer vertices than
        // per-pixel sampling (200 vs 500+ per vibrato note).
        using SKPath path = new SKPath();
        bool started = false;

        // Left edge (interpolated).
        {
            double sample = Math.Clamp(windowStart + (left + 0.5 - lane.X) * samplesPerPixel, note.StartSample, note.EndSample);
            double midi = PitchContour.PitchAtSampleInterpolated(note, (long)Math.Round(sample), _samplesPerFrame);
            int y = MidiToY(midi, minMidi, maxMidi, lane);
            if (y >= lane.Y && y < lane.Bottom) { path.MoveTo(left, y); started = true; }
        }

        // Visible pitch points.
        // Find first pitch point with SamplePosition >= windowStart for this lane's left.
        double leftSample = windowStart + (left - lane.X) * samplesPerPixel;
        double rightSample = windowStart + (right - lane.X) * samplesPerPixel;
        int lo = 0, hi = note.Pitch.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (note.Pitch[mid].SamplePosition < leftSample) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < note.Pitch.Length; i++)
        {
            long ptSample = note.Pitch[i].SamplePosition;
            if (ptSample < note.StartSample) continue;
            if (ptSample > note.EndSample) break;
            if (ptSample > rightSample) break;
            double x = _layout.SampleToX(ptSample, currentSample, _timeline.SampleRate, lane);
            if (x < left || x >= right) continue;
            int y = MidiToY(note.Pitch[i].MidiNote, minMidi, maxMidi, lane);
            if (y < lane.Y || y >= lane.Bottom) continue;
            if (!started) { path.MoveTo((float)x, y); started = true; }
            else path.LineTo((float)x, y);
        }

        // Right edge.
        {
            double sample = Math.Clamp(windowStart + (right - 0.5 - lane.X) * samplesPerPixel, note.StartSample, note.EndSample);
            double midi = PitchContour.PitchAtSampleInterpolated(note, (long)Math.Round(sample), _samplesPerFrame);
            int y = MidiToY(midi, minMidi, maxMidi, lane);
            if (y >= lane.Y && y < lane.Bottom)
            {
                if (!started) { path.MoveTo(right - 1, y); started = true; }
                else path.LineTo(right - 1, y);
            }
        }

        if (!started) return;

        // Stroke with a high-contrast hairline: white at 75% + 1px black under-stroke for legibility.
        SKColor prevColor = _fillPaint.Color;
        SKPaintStyle prevStyle = _fillPaint.Style;
        float prevWidth = _fillPaint.StrokeWidth;
        bool prevAA = _fillPaint.IsAntialias;

        _fillPaint.Style = SKPaintStyle.Stroke;
        _fillPaint.IsAntialias = true;

        // Under-stroke for contrast (1px wider, dark).
        _fillPaint.Color = new SKColor(0, 0, 0, 110);
        _fillPaint.StrokeWidth = 2.8f;
        Canvas.DrawPath(path, _fillPaint);

        // Main trace.
        _fillPaint.Color = new SKColor(255, 255, 255, 200);
        _fillPaint.StrokeWidth = 1.4f;
        Canvas.DrawPath(path, _fillPaint);

        _fillPaint.Color = prevColor;
        _fillPaint.Style = prevStyle;
        _fillPaint.StrokeWidth = prevWidth;
        _fillPaint.IsAntialias = prevAA;
    }

    /// <summary>Noise-only SSG notes render as a fixed accent strip (GPU approximation of the CPU texture).</summary>
    private void DrawSsgNoiseStrip(PreparedPanel panel, PreparedNote note, OverlayRect lane, long currentSample)
    {
        double left = Math.Max(lane.X, _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane));
        double right = Math.Min(lane.Right, _layout.SampleToX(note.EndSample, currentSample, _timeline.SampleRate, lane));
        if (right <= left)
            return;
        int y = lane.Y + Math.Max(2, lane.Height / 2 - 3);
        FillRect(
            new OverlayRect((int)Math.Floor(left), y, Math.Max(1, (int)Math.Ceiling(right - left)), Math.Max(2, 6)),
            panel.Accent.WithAlpha(190));
    }

    /// <summary>Relative-pitch operator ribbons (§13.4): anchor ±2 semitones at ×0.6 opacity.</summary>
    private void DrawFm3OperatorNotes(
        int panelIndex, PreparedPanel panel, long currentSample, long windowStart, long windowEnd)
    {
        OverlayRect ribbons = _layout.GetFm3OperatorRect(panelIndex);
        if (ribbons.Height <= 0)
            return;
        int rowHeight = Math.Max(1, ribbons.Height / 4);
        int operatorCount = Math.Min(4, panel.OperatorNotes.Length);
        for (int op = 0; op < operatorCount; op++)
        {
            var row = new OverlayRect(
                ribbons.X + 20, ribbons.Y + op * rowHeight, Math.Max(1, ribbons.Width - 20), rowHeight);
            PreparedNote[] notes = panel.OperatorNotes[op];
            int first = FindFirstVisibleNote(notes, windowStart);
            for (int index = first; index < notes.Length; index++)
            {
                PreparedNote note = notes[index];
                if (note.StartSample >= windowEnd)
                    break;
                if (note.EndSample <= windowStart)
                    continue;
                double left = Math.Max(row.X, _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, row));
                double right = Math.Min(row.Right, _layout.SampleToX(note.EndSample, currentSample, _timeline.SampleRate, row));
                if (right <= left)
                    continue;
                double opAnchor = Math.Round(note.InitialMidiNote);
                int yCentre = MidiToY(note.InitialMidiNote, opAnchor - 2, opAnchor + 2, row);
                int height = Math.Max(2, (int)Math.Round(row.Height * 0.24));
                int yTop = Math.Clamp(yCentre - height / 2, row.Y, row.Bottom - 1);
                int yBottom = Math.Clamp(yCentre + (height - height / 2), row.Y, row.Bottom);
                if (yBottom <= yTop)
                    continue;
                bool active = note.StartSample <= currentSample && currentSample < note.EndSample;
                OverlayColor fill = (active ? note.ActiveFill : note.Fill).WithAlpha(153);
                FillRect(
                    new OverlayRect((int)Math.Floor(left), yTop, Math.Max(1, (int)Math.Ceiling(right - left)), yBottom - yTop),
                    fill);
            }
        }
    }

    /// <summary>Active wavetable identity viewport: frame, centre line and label.</summary>
    private void DrawWaveTableViewport(int panelIndex, PreparedPanel panel, long currentSample)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        var viewport = new OverlayRect(
            timeline.X + _layout.PitchLabelWidth + 4,
            timeline.Y + 3,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth - 8),
            Math.Max(12, timeline.Height / 3));
        if (viewport.Width < 8 || viewport.Height < 4)
            return;

        WaveformChangeEvent[] changes = panel.WaveformChanges;
        int currentIndex = UpperBoundWaveform(changes, currentSample) - 1;
        WaveformDefinition current = null;
        if (currentIndex >= 0)
        {
            if (panel.WaveformsById.TryGetValue(changes[currentIndex].WaveformId, out WaveformDefinition resolved))
                current = resolved;
        }
        else if (panel.Waveforms.Length > 0)
        {
            current = panel.Waveforms[0];
        }

        if (current == null)
        {
            DrawTextAt(
                "TABLE UNAVAILABLE",
                viewport.X + 4,
                viewport.Y + Math.Max(1, viewport.Height / 2 - 4),
                TextSizeScale1,
                MutedText);
            return;
        }

        OverlayColor identity = IdentityColor(current.Id, panel.Accent);
        long changeAge = currentIndex >= 0 ? currentSample - changes[currentIndex].SamplePosition : long.MaxValue;
        bool emphasized = changeAge >= 0 && changeAge < (long)Math.Round(0.150 * _timeline.SampleRate);
        OverlayColor frame = identity.WithAlpha(emphasized ? (byte)255 : (byte)150);
        DrawHorizontalLine(viewport.X, viewport.Right - 1, viewport.Y + viewport.Height / 2, identity.WithAlpha(90));
        DrawVerticalLine(viewport.X, viewport.Y, viewport.Bottom - 1, frame);
        DrawVerticalLine(viewport.Right - 1, viewport.Y, viewport.Bottom - 1, frame);
        DrawHorizontalLine(viewport.X, viewport.Right - 1, viewport.Y, frame);
        DrawHorizontalLine(viewport.X, viewport.Right - 1, viewport.Bottom - 1, frame);
        if (viewport.Bottom + 2 < timeline.Bottom)
        {
            DrawTextAt(
                current.DisplayName ?? current.Id,
                viewport.X + 4,
                viewport.Bottom + 2,
                TextSizeScale1,
                BrightText.WithAlpha(210));
        }
    }

    // ------------------------------------------------------------------
    // Sample lanes: one band per distinct sample identity.
    // ------------------------------------------------------------------

    /// <summary>
    /// Precomputes, per panel, the row index into the first-appearance sample
    /// label list for every playback event (parallel to
    /// <see cref="PreparedPanel.SamplePlayback"/>), plus the label count that
    /// sizes the lane bands.
    /// </summary>
    private int[][] BuildSampleRowByPlayback()
    {
        var rowsByPanel = new int[_panels.Length][];
        var rowCounts = new int[_panels.Length];
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PreparedPanel panel = _panels[panelIndex];
            SamplePlaybackEvent[] playback = panel.SamplePlayback;
            var rows = new int[playback.Length];
            var labels = new List<string>();
            for (int i = 0; i < playback.Length; i++)
            {
                if (string.IsNullOrEmpty(playback[i].SampleId))
                    continue;
                string label = SampleRowLabel(panel, playback[i].SampleId);
                int row = labels.IndexOf(label);
                if (row < 0)
                {
                    labels.Add(label);
                    row = labels.Count - 1;
                }
                rows[i] = row;
            }
            rowsByPanel[panelIndex] = rows;
            rowCounts[panelIndex] = labels.Count;
        }
        _sampleLaneRowCounts = rowCounts;
        return rowsByPanel;
    }

    private void DrawSamplePlaybackLane(
        int panelIndex, PreparedPanel panel, long currentSample, long windowStart, long windowEnd)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        var lane = new OverlayRect(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth),
            timeline.Height);
        SamplePlaybackEvent[] events = panel.SamplePlayback;
        int[] rows = _sampleRowByPlayback != null && panelIndex < _sampleRowByPlayback.Length
            ? _sampleRowByPlayback[panelIndex]
            : null;
        int rowCount = _sampleLaneRowCounts != null && panelIndex < _sampleLaneRowCounts.Length
            ? _sampleLaneRowCounts[panelIndex]
            : 1;
        rowCount = Math.Max(1, rowCount);
        int first = LowerBoundPlaybackGpu(events, windowStart);
        for (int index = first; index < events.Length; index++)
        {
            SamplePlaybackEvent value = events[index];
            if (value.StartSample >= windowEnd)
                break;
            if (value.EndSample <= windowStart)
                continue;
            int row = rows != null && index < rows.Length ? rows[index] : 0;
            DrawSamplePlaybackRow(panel, value, lane, currentSample, row, rowCount);
        }
    }

    private void DrawSamplePlaybackRow(
        PreparedPanel panel,
        SamplePlaybackEvent value,
        OverlayRect lane,
        long currentSample,
        int rowIndex,
        int rowCount)
    {
        double left = Math.Max(lane.X, _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        double right = Math.Min(lane.Right, _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
        if (right <= left)
            return;
        int rowHeight = Math.Max(10, lane.Height / Math.Max(1, rowCount));
        int height = Math.Max(8, rowHeight - 2);
        int y = lane.Y + Math.Min(rowCount - 1, Math.Max(0, rowIndex)) * rowHeight + (rowHeight - height) / 2;

        OverlayColor identity = IdentityColor(value.SampleId, panel.Accent);
        int leftI = (int)Math.Round(left);
        int widthI = Math.Max(1, (int)Math.Round(right - left));
        FillRect(new OverlayRect(leftI, y, widthI, height), identity.WithAlpha(70));
        StrokeRectOutline(leftI, y, leftI + widthI - 1, y + height - 1, identity.WithAlpha(190));

        int startX = (int)Math.Round(_layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        if (startX >= lane.X && startX < lane.Right)
            DrawVerticalLine(startX, y - 1, Math.Min(lane.Bottom - 1, y + height), panel.Accent.Lighten(0.45));

        string label = ShortAssetLabel(
            panel.SamplesById.TryGetValue(value.SampleId, out SampleDefinition sample) ? sample.DisplayName : null,
            value.SampleId);
        if (widthI >= 28 && y + 1 < lane.Bottom - 1)
            DrawTextWithLimit(label, leftI + 3, y + 1, TextSizeScale1, BrightText.WithAlpha(210), Math.Min(lane.Right - 2, leftI + widthI - 2));
    }

    // ------------------------------------------------------------------
    // Noise lanes.
    // ------------------------------------------------------------------

    private void DrawNoiseLane(
        int panelIndex, PreparedPanel panel, long currentSample, long windowStart, long windowEnd)
    {
        OverlayRect lane = _layout.GetTimelineRect(panelIndex);
        var band = new OverlayRect(lane.X + 4, lane.Y + 5, Math.Max(1, lane.Width - 8), Math.Max(12, lane.Height - 24));
        if (band.Width <= 2 || band.Height <= 2)
            return;
        DrawHorizontalLine(band.X, band.Right - 1, band.Y + band.Height / 2, new OverlayColor(90, 96, 118, 120));

        NoiseStateEvent[] events = panel.Noise;
        int first = LowerBoundNoiseGpu(events, windowStart);
        if (first > 0)
            first--;
        bool active = false;
        int activeIndex = -1;
        for (int index = first; index < events.Length; index++)
        {
            NoiseStateEvent value = events[index];
            if (value.EndSample <= windowStart)
                continue;
            if (value.StartSample >= windowEnd)
                break;
            long age = currentSample - value.StartSample;
            if (age < 0)
                continue;
            double visualLevel = Math.Clamp(value.Level, 0, 1);
            double attack = Math.Clamp(age / Math.Max(1.0, _timeline.SampleRate * 0.030), 0, 1);
            long remaining = value.EndSample - currentSample;
            double release = Math.Clamp(remaining / Math.Max(1.0, _timeline.SampleRate * 0.080), 0, 1);
            visualLevel *= Math.Min(attack, release);
            if (visualLevel > 0)
                DrawNoiseBarsGpu(panel, value, band, currentSample, visualLevel);
            if (value.StartSample <= currentSample && currentSample < value.EndSample)
            {
                active = true;
                activeIndex = index;
            }
        }

        string state = active && activeIndex >= 0 && activeIndex < panel.NoiseLabels.Length
            ? panel.NoiseLabels[activeIndex]
            : panel.HasTrackEvents ? "NOISE" : "SILENT";
        DrawTextAt(state, lane.X + 6, lane.Bottom - 14, TextSizeScale1, active ? BrightText : MutedText);
    }

    /// <summary>
    /// Noise texture as per-column vertical bars (the CPU's per-pixel strip
    /// rendered as high-level lines); frequency shaping biases the column
    /// height like the CPU band-position rule.
    /// </summary>
    private void DrawNoiseBarsGpu(
        PreparedPanel panel, NoiseStateEvent value, OverlayRect band, long currentSample, double level)
    {
        uint seed = StableSeedNoiseGpu(panel.Id, value.StartSample, currentSample);
        int centre = band.Y + band.Height / 2;
        byte alpha = (byte)Math.Clamp(40 + level * 180, 0, 220);
        var color = new OverlayColor(120, 156, 230, alpha);
        double bandPosition = 0.5;
        if (value.CentreFrequencyHz is > 0 and double hz)
            bandPosition = Math.Clamp(Math.Log10(Math.Max(20, hz) / 20.0) / 3.0, 0, 1);
        for (int x = band.X; x < band.Right; x += 2)
        {
            seed = seed * 1664525u + 1013904223u;
            double n = ((seed >> 8) & 0xFFFF) / 65535.0;
            double distance = Math.Abs(n - bandPosition);
            double weighted = Math.Max(n, 1 - distance * 2);
            int height = Math.Max(1, (int)Math.Round((0.25 + weighted * 0.75) * band.Height * level));
            int yTop = Math.Max(band.Y, centre - height / 2);
            int yBottom = Math.Min(band.Bottom, centre + height / 2);
            if (yBottom > yTop)
                DrawVerticalLine(x, yTop, yBottom - 1, color);
        }
    }

    private static uint StableSeedNoiseGpu(string voiceId, long startSample, long currentSample)
    {
        uint hash = 2166136261;
        for (int index = 0; index < voiceId.Length; index++)
            hash = (hash ^ voiceId[index]) * 16777619;
        hash ^= unchecked((uint)startSample);
        hash *= 16777619;
        hash ^= unchecked((uint)currentSample);
        return hash;
    }

    // ------------------------------------------------------------------
    // Aggregate activity cells.
    // ------------------------------------------------------------------

    private void DrawAggregateCells(int panelIndex, PreparedPanel panel, long currentSample)
    {
        OverlayRect lane = _layout.GetTimelineRect(panelIndex);
        string[] subVoices = panel.AggregateSubVoices;
        if (subVoices.Length == 0)
        {
            DrawTextAt(
                panel.HasTrackEvents ? "AGGREGATE" : "SILENT",
                lane.X + 8,
                lane.Y + Math.Max(2, lane.Height / 2 - 4),
                TextSizeScale1,
                MutedText);
            return;
        }

        int columns = 4;
        int rows = (subVoices.Length + columns - 1) / columns;
        int cellWidth = Math.Max(1, lane.Width / columns);
        int cellHeight = Math.Max(1, lane.Height / rows);
        long decaySamples = Math.Max(1, (long)Math.Round(0.180 * _timeline.SampleRate));
        for (int slot = 0; slot < subVoices.Length && slot < 16; slot++)
        {
            int column = slot % columns;
            int row = slot / columns;
            var cell = new OverlayRect(
                lane.X + column * cellWidth,
                lane.Y + row * cellHeight,
                column == columns - 1 ? lane.Right - lane.X - column * cellWidth : cellWidth,
                row == rows - 1 ? lane.Bottom - lane.Y - row * cellHeight : cellHeight);
            AggregateHitEvent hit = FindRecentHitGpu(panel.AggregateHits, subVoices[slot], currentSample);
            double brightness = hit != null
                ? Math.Clamp(1 - (currentSample - hit.SamplePosition) / (double)decaySamples, 0, 1)
                    * Math.Clamp(hit.Strength, 0, 1)
                : 0;
            OverlayColor fill = panel.Accent.Lighten(0.10 + brightness * 0.45)
                .WithAlpha((byte)Math.Clamp(65 + brightness * 160, 0, 255));

            // 1px accent border via outer fill + inset fill.
            FillRect(cell, panel.Accent.WithAlpha((byte)Math.Clamp(110 + brightness * 145, 0, 255)));
            if (cell.Width > 2 && cell.Height > 2)
                FillRect(new OverlayRect(cell.X + 1, cell.Y + 1, cell.Width - 2, cell.Height - 2), fill);

            string label = panel.AggregateLabels.TryGetValue(subVoices[slot], out string preparedLabel)
                ? preparedLabel
                : subVoices[slot];
            DrawTextWithLimit(
                label,
                cell.X + 3,
                cell.Y + Math.Max(1, cell.Height / 2 - 4),
                TextSizeScale1,
                BrightText.WithAlpha((byte)Math.Clamp(130 + brightness * 125, 0, 255)),
                cell.Right - 3);
            if (hit != null)
            {
                int panX = cell.X + cell.Width / 2
                    + (int)Math.Round(Math.Clamp(hit.Pan, -1, 1) * Math.Max(1, cell.Width / 2 - 5));
                DrawVerticalLine(panX, cell.Y + 2, cell.Bottom - 3, BrightText.WithAlpha(180));
            }
            DrawAggregateHitTrail(cell, panel.AggregateHits, subVoices[slot], currentSample);
        }
    }

    private void DrawAggregateHitTrail(
        OverlayRect cell, AggregateHitEvent[] hits, string subVoiceId, long currentSample)
    {
        long historySamples = Math.Max(1, (long)Math.Round(1.5 * _timeline.SampleRate));
        long historyStart = currentSample - historySamples;
        int upper = UpperBoundAggregateHits(hits, currentSample);
        int markers = 0;
        for (int index = upper - 1; index >= 0 && markers < 8; index--)
        {
            AggregateHitEvent hit = hits[index];
            if (hit.SamplePosition < historyStart)
                break;
            if (!string.Equals(hit.SubVoiceId, subVoiceId, StringComparison.Ordinal))
                continue;
            double age = (currentSample - hit.SamplePosition) / (double)historySamples;
            int x = cell.X + 3 + (int)Math.Round((1 - Math.Clamp(age, 0, 1)) * Math.Max(1, cell.Width - 7));
            byte alpha = (byte)Math.Clamp(70 + Math.Clamp(hit.Strength, 0, 1) * 150, 0, 255);
            DrawVerticalLine(x, Math.Max(cell.Y + 2, cell.Bottom - 5), cell.Bottom - 2, BrightText.WithAlpha(alpha));
            markers++;
        }
    }

    private static AggregateHitEvent FindRecentHitGpu(
        AggregateHitEvent[] hits, string subVoiceId, long currentSample)
    {
        int upper = UpperBoundAggregateHits(hits, currentSample) - 1;
        for (int index = upper; index >= 0; index--)
        {
            AggregateHitEvent hit = hits[index];
            if (string.Equals(hit.SubVoiceId, subVoiceId, StringComparison.Ordinal))
                return hit;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Percussion rhythm hits.
    // ------------------------------------------------------------------

    private void DrawRhythmHits(
        int panelIndex, PreparedPanel panel, long currentSample, long windowStart, long windowEnd)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        var lane = new OverlayRect(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth),
            timeline.Height);
        PreparedRhythmEvent[] rhythm = panel.Rhythm;
        PanelRowDefinition[] rows = panel.Rows;
        if (rhythm.Length == 0 || rows.Length == 0)
            return;
        int rowHeight = Math.Max(1, lane.Height / rows.Length);
        OverlayColor accent = panel.Accent;
        long strongSamples = (long)Math.Round(90 * _timeline.SampleRate / 1000.0);
        long totalSamples = (long)Math.Round(300 * _timeline.SampleRate / 1000.0);
        int first = LowerBoundRhythm(rhythm, windowStart);
        for (int index = first; index < rhythm.Length; index++)
        {
            PreparedRhythmEvent evt = rhythm[index];
            if (evt.SamplePosition >= windowEnd)
                break;
            int voice = evt.RowIndex;
            if (voice < 0 || voice >= rows.Length)
                voice = rows.Length - 1;
            double xFrac = _layout.SampleToX(evt.SamplePosition, currentSample, _timeline.SampleRate, lane);
            int x = (int)Math.Round(xFrac);
            if (x < lane.X || x >= lane.Right)
                continue;
            float strength = Math.Clamp(evt.Strength, 0, 1);
            int pulseWidth = 4 + (int)Math.Round(strength * 12);
            int pulseHeight = Math.Max(3, rowHeight - 3);
            int y = lane.Y + voice * rowHeight + Math.Max(1, (rowHeight - pulseHeight) / 2);
            double pulseLeft = Math.Max(lane.X, xFrac - pulseWidth / 2.0);
            double pulseRight = Math.Min(lane.Right, xFrac + pulseWidth / 2.0);

            // Decaying horizontal trail (§15.2): full brightness through the
            // strong phase, fading to zero by the total decay.
            long age = currentSample - evt.SamplePosition;
            if (age >= 0 && age < totalSamples)
            {
                double decayFraction = age < strongSamples
                    ? 1.0
                    : 1.0 - (age - strongSamples) / (double)Math.Max(1, totalSamples - strongSamples);
                int trailAlpha = (int)Math.Round(110 * strength * decayFraction);
                if (trailAlpha > 0)
                {
                    double trailEndX = _layout.SampleToX(
                        evt.SamplePosition + totalSamples, currentSample, _timeline.SampleRate, lane);
                    double trailLeft = Math.Max(lane.X, xFrac + pulseWidth / 2.0);
                    double trailRight = Math.Min(lane.Right, trailEndX);
                    int trailHeight = Math.Max(2, pulseHeight / 2);
                    int trailY = y + (pulseHeight - trailHeight) / 2;
                    if (trailRight > trailLeft)
                    {
                        FillRect(
                            new OverlayRect(
                                (int)Math.Floor(trailLeft),
                                trailY,
                                Math.Max(1, (int)Math.Ceiling(trailRight - trailLeft)),
                                trailHeight),
                            accent.WithAlpha((byte)Math.Clamp(trailAlpha, 0, 255)));
                    }
                }
            }

            // Onset impact block, drawn above the trail.
            OverlayColor impact = accent.Lighten(0.12)
                .WithAlpha((byte)Math.Clamp(Math.Round(150 + strength * 105), 0, 255));
            if (pulseRight > pulseLeft)
            {
                FillRect(
                    new OverlayRect(
                        (int)Math.Floor(pulseLeft),
                        y,
                        Math.Max(1, (int)Math.Ceiling(pulseRight - pulseLeft)),
                        pulseHeight),
                    impact);
            }

            // Pan tick (§15.3): short horizontal mark offset from the row centre.
            int rowCentreY = lane.Y + voice * rowHeight + rowHeight / 2;
            int tickX = x + (int)Math.Round(evt.Pan * 6);
            DrawHorizontalLine(tickX - 2, tickX + 2, rowCentreY, BrightText.WithAlpha(200));
        }
    }

    // ------------------------------------------------------------------
    // Generic activity lane (mixed events as stacked marks).
    // ------------------------------------------------------------------

    private void DrawGenericActivity(
        int panelIndex, PreparedPanel panel, long currentSample, long windowStart, long windowEnd)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        var lane = new OverlayRect(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth),
            timeline.Height);
        int barHeight = Math.Max(4, lane.Height / 3);
        int barY = lane.Y + Math.Max(0, (lane.Height - barHeight) / 2);

        foreach (PreparedNote note in panel.MainNotes)
        {
            if (note.EndSample <= windowStart || note.StartSample >= windowEnd)
                continue;
            double left = Math.Max(lane.X, _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane));
            double right = Math.Min(lane.Right, _layout.SampleToX(note.EndSample, currentSample, _timeline.SampleRate, lane));
            if (right > left)
                FillRect(new OverlayRect((int)Math.Floor(left), barY, Math.Max(1, (int)Math.Ceiling(right - left)), barHeight), note.Fill);
        }

        foreach (SamplePlaybackEvent value in panel.SamplePlayback)
        {
            if (value.EndSample <= windowStart || value.StartSample >= windowEnd)
                continue;
            DrawSamplePlaybackRow(panel, value, lane, currentSample, 0, 1);
        }

        foreach (NoiseStateEvent value in panel.Noise)
        {
            if (value.EndSample <= windowStart || value.StartSample >= windowEnd)
                continue;
            double left = Math.Max(lane.X, _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
            double right = Math.Min(lane.Right, _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
            if (right > left)
                FillRect(new OverlayRect((int)Math.Floor(left), barY, Math.Max(1, (int)Math.Ceiling(right - left)), barHeight), panel.Accent.WithAlpha(170));
        }

        foreach (PreparedRhythmEvent value in panel.Rhythm)
        {
            if (value.SamplePosition < windowStart || value.SamplePosition >= windowEnd)
                continue;
            int x = (int)Math.Round(_layout.SampleToX(value.SamplePosition, currentSample, _timeline.SampleRate, lane));
            if (x >= lane.X && x < lane.Right)
                DrawVerticalLine(x, lane.Y, lane.Bottom - 1, panel.Accent);
        }

        foreach (AggregateHitEvent value in panel.AggregateHits)
        {
            if (value.SamplePosition < windowStart || value.SamplePosition >= windowEnd)
                continue;
            int x = (int)Math.Round(_layout.SampleToX(value.SamplePosition, currentSample, _timeline.SampleRate, lane));
            if (x >= lane.X && x < lane.Right)
                DrawVerticalLine(x, lane.Y, lane.Bottom - 1, panel.Accent);
        }
    }

    // ------------------------------------------------------------------
    // Shared helpers.
    // ------------------------------------------------------------------

    private static bool IsSsgMode(VisualizationNoteMode mode)
        => mode is VisualizationNoteMode.SsgTone
            or VisualizationNoteMode.SsgToneNoise
            or VisualizationNoteMode.SsgNoise
            or VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeToneNoise
            or VisualizationNoteMode.SsgEnvelopeNoise;

    private static bool IsSsgNoiseOnly(VisualizationNoteMode mode)
        => mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise;

    private static (double Min, double Max) PitchRangeOf(PreparedPanel panel)
    {
        double min = panel.MinMidi;
        double max = panel.MaxMidi;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min)
        {
            min = 48;
            max = 72;
        }
        return (min, max);
    }

    /// <summary>Ribbon height mirroring the CPU preferred size: ~95% of a semitone, clamped to 5.5–12% of the lane.</summary>
    private static int PreferredRibbonHeight(OverlayRect lane, double minMidi, double maxMidi)
    {
        double pixelsPerSemitone = lane.Height / (maxMidi - minMidi);
        int minimum = Math.Max(5, (int)Math.Round(lane.Height * 0.055));
        int maximum = Math.Max(minimum, (int)Math.Round(lane.Height * 0.12));
        return Math.Clamp((int)Math.Floor(pixelsPerSemitone * 0.95), minimum, maximum);
    }

    private double NotePitchAt(PreparedNote note, long sample)
        => PitchContour.PitchAtSample(note, sample, _samplesPerFrame);

    private static string ShortAssetLabel(string displayName, string id)
        => string.IsNullOrWhiteSpace(displayName) ? id : displayName;

    private static string SampleRowLabel(PreparedPanel panel, string sampleId)
        => ShortAssetLabel(
            panel.SamplesById.TryGetValue(sampleId, out SampleDefinition sample) ? sample.DisplayName : null,
            sampleId);

    private static OverlayColor IdentityColor(string id, OverlayColor fallback)
    {
        if (string.IsNullOrEmpty(id))
            return fallback;
        uint hash = 2166136261;
        for (int index = 0; index < id.Length; index++)
            hash = (hash ^ id[index]) * 16777619;
        byte r = (byte)(90 + hash % 100);
        byte g = (byte)(90 + (hash >> 8) % 100);
        byte b = (byte)(110 + (hash >> 16) % 100);
        return new OverlayColor(r, g, b, fallback.A);
    }

    /// <summary>First playback event whose end exceeds <paramref name="sample"/>.</summary>
    private static int LowerBoundPlaybackGpu(SamplePlaybackEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].EndSample <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int LowerBoundNoiseGpu(NoiseStateEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].StartSample < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int UpperBoundWaveform(WaveformChangeEvent[] changes, long sample)
    {
        int low = 0;
        int high = changes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (changes[middle].SamplePosition <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int UpperBoundAggregateHits(AggregateHitEvent[] hits, long sample)
    {
        int low = 0;
        int high = hits.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (hits[middle].SamplePosition <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>1px outline of the rect (left..right, top..bottom) in one color.</summary>
    private void StrokeRectOutline(int left, int top, int right, int bottom, OverlayColor color)
    {
        DrawHorizontalLine(left, right, top, color);
        DrawHorizontalLine(left, right, bottom, color);
        DrawVerticalLine(left, top, bottom, color);
        DrawVerticalLine(right, top, bottom, color);
    }

    private void DrawHorizontalLine(int xLeft, int xRight, int y, OverlayColor color)
    {
        if (xRight < xLeft)
            return;
        _fillPaint.Color = ToSk(color);
        Canvas.DrawRect(new SKRect(xLeft, y, xRight + 1, y + 1), _fillPaint);
    }

    private void DrawCircle(int cx, int cy, float radius, OverlayColor color)
    {
        if (radius <= 0)
            return;
        _fillPaint.Color = ToSk(color);
        Canvas.DrawCircle(cx, cy, radius, _fillPaint);
    }
}