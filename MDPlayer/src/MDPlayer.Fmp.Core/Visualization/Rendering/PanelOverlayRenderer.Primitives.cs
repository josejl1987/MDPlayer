using System.Runtime.InteropServices;

using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering;

// CPU pixel primitives remain a single implementation shared by all panels.
internal sealed partial class PanelOverlayRenderer
{
    private void DrawFm3OperatorRibbons(
        Span<byte> frame,
        PanelData panel,
        long currentSample,
        (double Min, double Max)? sharedRange = null)
    {
        OverlayRect ribbons = _layout.GetFm3OperatorRect(panel.Index);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        double windowSamples = _layout.WindowSeconds * _timeline.SampleRate;
        (double minMidi, double maxMidi) = sharedRange ?? GetPitchRange(panel, currentSample);
        int playheadX = _layout.GetPlayheadX(panel.Index);
        int rowHeight = Math.Max(1, ribbons.Height / 4);
        int operatorCount = Math.Min(
            4,
            Math.Min(panel.Prepared.OperatorNotes.Length, panel.OperatorNoteStreamIds.Length));
        for (int op = 0; op < operatorCount; op++)
        {
            var row = new OverlayRect(ribbons.X + 20, ribbons.Y + op * rowHeight, Math.Max(1, ribbons.Width - 20), rowHeight);
            DrawVisibleNotes(
                frame, panel, panel.Prepared.OperatorNotes[op], row, currentSample, true,
                windowStart, windowEnd, windowSamples, minMidi, maxMidi, playheadX, 0,
                panel.OperatorNoteStreamIds[op]);
        }
    }


    private bool HasAudioEnergy(int panelIndex)
        => panelIndex >= 0
            && panelIndex < _hasAudioEnergyByPanel.Length
            && _hasAudioEnergyByPanel[panelIndex];

    private bool HasEnergyEnvelope(int panelIndex)
        => panelIndex < _energyByPanel.Length && _energyByPanel[panelIndex] != null;

    private void DrawPlayhead(Span<byte> frame, int panelIndex)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        int x = _layout.GetPlayheadX(panelIndex);
        DrawVerticalLine(frame, x, timeline.Y, timeline.Bottom - 1, Playhead);
        if (x + 1 < timeline.Right)
            DrawVerticalLine(frame, x + 1, timeline.Y, timeline.Bottom - 1, Playhead.WithAlpha(55));
    }

    /// <summary>
    /// Returns normalized [0,1] RMS energy for a panel at a given sample (§6.4).
    /// Direct array lookup — no allocation on the hot path.
    /// </summary>
    private float GetEnergy(int panelIndex, long currentSample)
    {
        var env = _energyByPanel != null && panelIndex < _energyByPanel.Length
            ? _energyByPanel[panelIndex] : null;
        float[] activity = env?.FrameActivity is { Length: > 0 }
            ? env.FrameActivity
            : env?.FrameRms;
        if (activity == null || activity.Length == 0)
            // No analyzed envelope means no modulation, not a silent signal.
            // 0.5 is the neutral point for the ±15% perceptual mapping.
            return 0.5f;
        int idx = EnergyFrameIndex(currentSample, activity.Length);
        return activity[idx];
    }

    private int EnergyFrameIndex(long currentSample, int frameCount)
    {
        double frameIndex = (currentSample - _timeline.StartSample) / _samplesPerFrame;
        return (int)Math.Clamp((long)frameIndex, 0, frameCount - 1);
    }

    private bool HasRecentActivity(int panelIndex, long currentSample)
    {
        if (_energyByPanel != null && panelIndex < _energyByPanel.Length)
        {
            ChannelEnergyEnvelope env = _energyByPanel[panelIndex];
            float[] activity = env?.FrameActivity is { Length: > 0 }
                ? env.FrameActivity
                : env?.FrameRms;
            if (activity is { Length: > 0 })
            {
                int current = EnergyFrameIndex(currentSample, activity.Length);
                int lookback = Math.Max(1, (int)Math.Ceiling(0.500 * FpsNumerator / FpsDenominator));
                int first = Math.Max(0, current - lookback);
                for (int index = first; index <= current; index++)
                {
                    if (activity[index] > 0.02f)
                        return true;
                }
            }
        }

        long recentStart = currentSample - (long)Math.Round(0.500 * _timeline.SampleRate);
        if (panelIndex < _panels.Length)
        {
            PanelData panel = _panels[panelIndex];
            PreparedPanel prepared = panel.Prepared;
            if (ContainsRecentNote(
                    prepared.MainNotes, panel.MainNoteStreamId, recentStart, currentSample))
                return true;
            if (panel.TrackKind == VisualizationTrackKind.FmOperatorGroup)
            {
                int operatorCount = Math.Min(
                    prepared.OperatorNotes.Length, panel.OperatorNoteStreamIds.Length);
                for (int operatorIndex = 0; operatorIndex < operatorCount; operatorIndex++)
                {
                    if (ContainsRecentNote(
                            prepared.OperatorNotes[operatorIndex],
                            panel.OperatorNoteStreamIds[operatorIndex],
                            recentStart,
                            currentSample))
                        return true;
                }
            }
            if (panel.TrackKind == VisualizationTrackKind.Percussion)
            {
                PreparedRhythmEvent[] events = prepared.Rhythm;
                int first;
                if (!(_activeSequentialState?.TryGetRhythmFirst(
                        panel.RhythmStreamId, events, recentStart, out first) ?? false))
                    first = LowerBoundRhythm(events, recentStart);
                if (first < events.Length && events[first].SamplePosition <= currentSample)
                    return true;
            }
            else if (panel.TrackKind == VisualizationTrackKind.Sample)
            {
                if (ContainsRecentSample(
                        prepared.SamplePlayback, panel.PlaybackStreamId, recentStart, currentSample))
                    return true;
            }
            else if (panel.TrackKind == VisualizationTrackKind.Noise
                && ContainsRecentNoise(
                    prepared.Noise, panel.NoiseStreamId, recentStart, currentSample))
                return true;
            else if (panel.TrackKind == VisualizationTrackKind.AggregateActivity
                && ContainsRecentAggregate(
                    prepared.AggregateHits, panel.AggregateStreamId, recentStart, currentSample))
                return true;
        }
        return false;
    }

    private bool ContainsRecentNote(
        PreparedNote[] notes, int streamId, long recentStart, long currentSample)
    {
        int first = FindFirstVisibleIndex(notes, streamId, recentStart);
        for (int index = first; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample > currentSample)
                break;
            if (note.EndSample > recentStart)
                return true;
        }
        return false;
    }

    private bool ContainsRecentSample(
        SamplePlaybackEvent[] events, int streamId, long recentStart, long currentSample)
    {
        int first;
        if (!(_activeSequentialState?.TryGetPlaybackFirst(
                streamId, events, recentStart, out first) ?? false))
            first = LowerBoundPlayback(events, recentStart);
        for (int index = first; index < events.Length; index++)
        {
            SamplePlaybackEvent value = events[index];
            if (value.StartSample > currentSample)
                break;
            if (value.EndSample > recentStart)
                return true;
        }
        return false;
    }

    private bool ContainsRecentNoise(
        NoiseStateEvent[] events, int streamId, long recentStart, long currentSample)
    {
        int first;
        if (!(_activeSequentialState?.TryGetNoiseFirst(
                streamId, events, recentStart, out first) ?? false))
            first = LowerBoundNoiseByStart(events, recentStart);
        if (first > 0)
            first--;
        for (int index = first; index < events.Length; index++)
        {
            NoiseStateEvent value = events[index];
            if (value.StartSample > currentSample)
                break;
            if (value.EndSample > recentStart)
                return true;
        }
        return false;
    }

    private static int LowerBoundNoiseByStart(NoiseStateEvent[] events, long sample)
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

    private bool ContainsRecentAggregate(
        AggregateHitEvent[] events, int streamId, long recentStart, long currentSample)
    {
        int first;
        if (!(_activeSequentialState?.TryGetAggregateFirst(
                streamId, events, recentStart, out first) ?? false))
            first = LowerBoundAggregate(events, recentStart);
        for (int index = first; index < events.Length; index++)
        {
            AggregateHitEvent value = events[index];
            if (value.SamplePosition > currentSample)
                break;
            if (value.SamplePosition >= recentStart)
                return true;
        }
        return false;
    }

    private static int LowerBoundAggregate(AggregateHitEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].SamplePosition < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private bool HasPanelContactActivity(PanelData panel, long currentSample)
    {
        long tolerance = Math.Max(1, (long)Math.Ceiling(_samplesPerFrame * 2));
        if (panel.TrackKind == VisualizationTrackKind.Percussion)
        {
            PreparedRhythmEvent[] events = panel.Prepared.Rhythm;
            int first;
            if (!(_activeSequentialState?.TryGetRhythmFirst(
                    panel.RhythmStreamId, events, currentSample - tolerance, out first) ?? false))
                first = LowerBoundRhythm(events, currentSample - tolerance);
            for (int index = first; index < events.Length; index++)
            {
                long sample = events[index].SamplePosition;
                if (sample > currentSample + tolerance)
                    break;
                if (Math.Abs(sample - currentSample) <= tolerance)
                    return true;
            }
        }
        else if (panel.TrackKind == VisualizationTrackKind.Sample)
        {
            int first;
            if (!(_activeSequentialState?.TryGetPlaybackFirst(
                    panel.PlaybackStreamId, panel.Prepared.SamplePlayback, currentSample, out first) ?? false))
                first = LowerBoundPlayback(panel.Prepared.SamplePlayback, currentSample);
            for (int index = first; index < panel.Prepared.SamplePlayback.Length; index++)
            {
                SamplePlaybackEvent value = panel.Prepared.SamplePlayback[index];
                if (value.StartSample > currentSample)
                    break;
                if (value.StartSample <= currentSample && currentSample < value.EndSample)
                    return true;
            }
        }
        else if (panel.TrackKind == VisualizationTrackKind.Noise)
        {
            int first;
            if (!(_activeSequentialState?.TryGetNoiseFirst(
                    panel.NoiseStreamId, panel.Prepared.Noise, currentSample - tolerance, out first) ?? false))
                first = LowerBoundNoiseByStart(panel.Prepared.Noise, currentSample - tolerance);
            if (first > 0)
                first--;
            for (int index = first; index < panel.Prepared.Noise.Length; index++)
            {
                NoiseStateEvent value = panel.Prepared.Noise[index];
                if (value.StartSample > currentSample + tolerance)
                    break;
                if (value.StartSample <= currentSample + tolerance
                    && currentSample - tolerance < value.EndSample)
                    return true;
            }
        }
        else if (panel.TrackKind == VisualizationTrackKind.AggregateActivity)
        {
            int first;
            if (!(_activeSequentialState?.TryGetAggregateFirst(
                    panel.AggregateStreamId, panel.Prepared.AggregateHits,
                    currentSample - tolerance, out first) ?? false))
                first = LowerBoundAggregate(panel.Prepared.AggregateHits, currentSample - tolerance);
            for (int index = first; index < panel.Prepared.AggregateHits.Length; index++)
            {
                long hitSample = panel.Prepared.AggregateHits[index].SamplePosition;
                if (hitSample > currentSample + tolerance)
                    break;
                if (Math.Abs(hitSample - currentSample) <= tolerance)
                    return true;
            }
        }
        return false;
    }

    private static OverlayColor AdjustForEnergy(OverlayColor color, float energy)
    {
        double delta = (Math.Clamp(energy, 0, 1) - 0.5) * 0.30;
        if (delta >= 0)
            return color.Lighten(delta);

        double factor = 1.0 + delta;
        return new OverlayColor(
            (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255),
            color.A);
    }

    /// <summary>
    /// Draws an energy-reactive scope border overlay (§17.6). The static
    /// border (alpha 180) remains as a floor; this overlays a brighter border
    /// when channel energy is high.
    /// </summary>
    private void DrawEnergyScopeBorder(Span<byte> frame, int panelIndex, long currentSample)
    {
        if (_energyByPanel == null || panelIndex >= _energyByPanel.Length)
            return;
        var env = _energyByPanel[panelIndex];
        if (env == null)
            return;
        float energy = GetEnergy(panelIndex, currentSample);
        if (energy <= 0.01f)
            return;
        OverlayRect scope = _layout.GetScopeRect(panelIndex);
        byte alpha = (byte)Math.Clamp(180 + (int)(energy * 75), 180, 255);
        StrokeRect(frame, scope, Border.WithAlpha(alpha), 1);
    }

    /// <summary>
    /// Computes the fractional [leftX, rightX) boundaries of a note's visible
    /// span, clipped to the lane. The subpixel position of each edge is
    /// preserved so the caller can blend fractional edge coverage for smooth
    /// horizontal motion.
    /// </summary>
    private bool TryClipTimeSpanFractional(
        long startSample,
        long endSample,
        long currentSample,
        OverlayRect lane,
        out double leftX,
        out double rightX)
    {
        leftX = rightX = 0;
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        return TryClipTimeSpanFractional(
            startSample,
            endSample,
            windowStart,
            windowEnd,
            _layout.WindowSeconds * _timeline.SampleRate,
            lane,
            out leftX,
            out rightX);
    }

    private bool TryClipTimeSpanFractional(
        long startSample,
        long endSample,
        long windowStart,
        long windowEnd,
        double windowSamples,
        OverlayRect lane,
        out double leftX,
        out double rightX)
    {
        leftX = rightX = 0;
        if (startSample >= windowEnd || endSample <= windowStart || endSample <= startSample)
            return false;

        double rawLeft = lane.X + (startSample - windowStart) * lane.Width / windowSamples;
        double rawRight = lane.X + (endSample - windowStart) * lane.Width / windowSamples;
        leftX = Math.Max(lane.X, rawLeft);
        rightX = Math.Min(lane.Right, rawRight);
        return rightX > leftX;
    }

    private int FindFirstVisibleIndex(PreparedNote[] notes, int streamId, long windowStart)
    {
        if (_activeSequentialState?.TryGetFirst(
                streamId, notes, windowStart, out int sequentialFirst) == true)
            return sequentialFirst;

        int low = 0;
        int high = notes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].StartSample < windowStart)
                low = middle + 1;
            else
                high = middle;
        }
        return Math.Max(0, low - 1);
    }

    private static int LowerBoundRhythm(PreparedRhythmEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].SamplePosition < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private PreparedNote FindActive(PreparedNote[] notes, int streamId, long sample)
    {
        if (_activeSequentialState?.TryGetActive(
                streamId, notes, sample, out PreparedNote active) == true)
            return active;
        return FindActiveBinary(notes, sample);
    }

    private static PreparedNote FindActiveBinary(PreparedNote[] notes, long sample)
    {
        int low = 0;
        int high = notes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].StartSample <= sample)
                low = middle + 1;
            else
                high = middle;
        }

        for (int index = low - 1; index >= 0 && index >= low - 2; index--)
        {
            PreparedNote note = notes[index];
            if (note.StartSample <= sample && sample < note.EndSample)
                return note;
        }
        return null;
    }

    private static int MidiToY(double midi, double minimum, double maximum, OverlayRect lane)
    {
        if (!double.IsFinite(midi) || maximum <= minimum)
            return lane.Bottom - 1;
        double fraction = (midi - minimum) / (maximum - minimum);
        fraction = Math.Clamp(fraction, 0, 1);
        return lane.Bottom - 1 - (int)Math.Round(fraction * Math.Max(0, lane.Height - 1));
    }

    private string[] BuildClockStrings(string totalClock)
    {
        long durationSeconds = Math.Max(0, _timeline.EndSample - _timeline.StartSample)
            / _timeline.SampleRate;
        int count = checked((int)Math.Min(int.MaxValue - 1L, durationSeconds + 1));
        var clocks = new string[count];
        for (int second = 0; second < clocks.Length; second++)
            clocks[second] = FormatTime(second) + " / " + totalClock;
        return clocks;
    }

    private string[] BuildLoopLabels()
    {
        int frameCount = checked((int)Math.Max(1, Math.Min(int.MaxValue - 1L, TotalFrames)));
        var labels = new string[frameCount];
        foreach (LoopMarker marker in _timeline.LoopMarkers)
        {
            long relative = Math.Max(0, marker.SamplePosition - _timeline.StartSample);
            int first = Math.Clamp(
                (int)Math.Round(relative * FpsNumerator / (double)(_timeline.SampleRate * FpsDenominator)),
                0,
                labels.Length - 1);
            int last = Math.Min(
                labels.Length,
                first + Math.Max(1, (int)Math.Round(0.600 * FpsNumerator / FpsDenominator)));
            string label = marker.Kind == LoopMarkerKind.Start
                ? "LOOP0"
                : "LOOP" + marker.Iteration;
            for (int frame = first; frame < last; frame++)
                labels[frame] = label;
        }
        return labels;
    }

    /// <summary>
    /// Returns a prepared pitch label for the nearest cent. The table is
    /// finite because MIDI pitch labels are only used for the 0–127 range;
    /// all string construction happens during type initialization.
    /// </summary>
    private static string FormatPitchWithCents(double actualMidi)
        => PitchLabelCache.Format(actualMidi);

    private static string FormatTime(double seconds)
    {
        seconds = Math.Max(0, seconds);
        int total = (int)Math.Floor(seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>
    /// Finds the most recent note on this panel whose instrument change is
    /// within the 800 ms overlay window (§13.2). Uses a backward scan from
    /// the most recent note — instrument changes are rare.
    /// </summary>
    private PreparedNote FindRecentInstrumentChange(PanelData panel, long currentSample)
    {
        long overlaySamples = (long)Math.Round(InstrumentChangeOverlayMs / 1000.0 * _timeline.SampleRate);
        long threshold = currentSample - overlaySamples;
        PreparedNote[] notes = panel.Prepared.InstrumentChanges;

        int low = 0, high = notes.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (notes[mid].StartSample <= currentSample)
                low = mid + 1;
            else
                high = mid;
        }

        for (int index = low - 1; index >= 0; index--)
        {
            PreparedNote note = notes[index];
            if (note.StartSample < threshold)
                break;
            if (note.HasInstrumentChange)
                return note;
        }
        return null;
    }

    /// <summary>
    /// Formats the instrument-change overlay text (§13.2):
    /// "ALG 4  FB 6  AMS 1  PMS 5". PMS is the spec's name for FMS.
    /// </summary>
    /// <summary>
    /// Draws four compact operator bars in the header during the
    /// instrument-change overlay (§13.3). Each bar encodes:
    /// brightness=inverse TotalLevel, height=AttackRate,
    /// tail mark=ReleaseRate, dot=AmplitudeModulation.
    /// </summary>
    private void DrawOperatorBars(
        Span<byte> frame,
        OverlayRect header,
        InstrumentDefinition def,
        int rightLimit,
        int panelIndex)
    {
        var operators = def.Operators;
        int n = Math.Min(4, operators.Count);
        int totalWidth = n * OperatorBarWidth + (n - 1) * OperatorBarGap;
        int startX = rightLimit - totalWidth;
        if (startX < header.X + 82)
            return;

        for (int op = 0; op < n; op++)
        {
            int x = startX + op * (OperatorBarWidth + OperatorBarGap);
            var opDef = operators[op];

            // Brightness: inverse TotalLevel (0=full, 127=silent).
            byte brightness = (byte)Math.Clamp(255 - opDef.TotalLevel * 2, 60, 255);
            var barColor = new OverlayColor(brightness, brightness, brightness);

            // Height: AttackRate (0=slowest, 31=fastest). Taller = faster.
            int h = Math.Max(2, header.Height * (opDef.AttackRate + 1) / 32);
            int barTop = header.Bottom - h;
            int barBottom = header.Bottom - 1;

            for (int bx = x; bx < x + OperatorBarWidth && bx < header.Right; bx++)
                for (int by = barTop; by <= barBottom; by++)
                    if (header.Contains(bx, by))
                        SetPixel(frame, bx, by, barColor);

            // Tail mark: ReleaseRate > 0 → bright pixel at the bar's bottom.
            if (opDef.ReleaseRate > 0 && header.Contains(x, barBottom))
                SetPixel(frame, x, barBottom, BrightText);

            // Dot: AmplitudeModulation enabled → accent pixel at the bar's top.
            if (opDef.AmplitudeModulation && header.Contains(x, barTop))
                SetPixel(frame, x, barTop, _panelAccents[panelIndex]);
        }
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private void Fill(Span<byte> frame, OverlayColor color)
        => Canvas.DrawColor(ToSk(color), SKBlendMode.Src);

    /// <summary>Skia-backed translucent/opaque rect fill. Exact legacy fast paths removed — Skia's rasterizer supersedes them.</summary>
    private void FillRect(Span<byte> frame, OverlayRect rect, OverlayColor color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        _fillPaint.Color = ToSk(color);
        Canvas.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, _fillPaint);
    }

    private void FillRectOpaque(
        Span<byte> frame,
        int left,
        int right,
        int top,
        int bottom,
        OverlayColor color)
    {
        if (right <= left || bottom <= top)
            return;
        _fillPaint.Color = new SKColor(color.R, color.G, color.B, 255);
        Canvas.DrawRect(left, top, right - left, bottom - top, _fillPaint);
    }

    private void BlendRunRect(
        Span<byte> frame,
        int left,
        int right,
        int top,
        int bottom,
        OverlayColor color,
        int alpha)
    {
        if (right <= left || bottom <= top || alpha <= 0)
            return;
        byte a = alpha >= 255 ? (byte)255 : (byte)alpha;
        _fillPaint.Color = new SKColor(color.R, color.G, color.B, a);
        Canvas.DrawRect(left, top, right - left, bottom - top, _fillPaint);
    }

    private void FillRectFractionalX(
        Span<byte> frame,
        double leftX,
        double rightX,
        int top,
        int height,
        OverlayColor color)
    {
        if (!double.IsFinite(leftX) || !double.IsFinite(rightX) || rightX <= leftX || height <= 0 || color.A == 0)
            return;
        int topClamped = Math.Clamp(top, 0, Height);
        int bottomClamped = Math.Clamp(top + height, 0, Height);
        if (bottomClamped <= topClamped)
            return;
        _fillPaintAA.Color = ToSk(color);
        Canvas.DrawRect((float)leftX, topClamped, (float)(rightX - leftX), bottomClamped - topClamped, _fillPaintAA);
    }

    private void ClearRect(Span<byte> frame, OverlayRect rect)
    {
        // Src blend (not SrcOver): the scope holes must become fully
        // transparent regardless of what is underneath.
        _clearPaint.Color = new SKColor(0, 0, 0, 0);
        Canvas.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, _clearPaint);
    }

    private void StrokeRect(Span<byte> frame, OverlayRect rect, OverlayColor color, int thickness)
    {
        DrawHorizontalLine(frame, rect.X, rect.Right - 1, rect.Y, color);
        DrawHorizontalLine(frame, rect.X, rect.Right - 1, rect.Bottom - 1, color);
        DrawVerticalLine(frame, rect.X, rect.Y, rect.Bottom - 1, color);
        DrawVerticalLine(frame, rect.Right - 1, rect.Y, rect.Bottom - 1, color);
    }

    private void DrawHorizontalLine(Span<byte> frame, int x1, int x2, int y, OverlayColor color)
        => FillRect(frame, new OverlayRect(Math.Min(x1, x2), y, Math.Abs(x2 - x1) + 1, 1), color);

    private void DrawVerticalLine(Span<byte> frame, int x, int y1, int y2, OverlayColor color)
        => FillRect(frame, new OverlayRect(x, Math.Min(y1, y2), 1, Math.Abs(y2 - y1) + 1), color);

    private void SetPixel(Span<byte> frame, int x, int y, OverlayColor color)
        => FillRect(frame, new OverlayRect(x, y, 1, 1), color);

    private void BlendPixel(Span<byte> frame, int x, int y, OverlayColor source)
        => SetPixel(frame, x, y, source);

    private void DrawText(
        Span<byte> frame,
        int x,
        int y,
        string text,
        OverlayColor color,
        int scale,
        int maxX)
    {
        if (string.IsNullOrEmpty(text) || scale <= 0)
            return;
        int cursor = x;
        foreach (char raw in text)
        {
            char character = char.ToUpperInvariant(raw);
            if (cursor + 5 * scale > maxX)
                break;
            if (BitmapFont.TryGetGlyph(character, out byte[] rows))
            {
                for (int row = 0; row < 7; row++)
                {
                    for (int column = 0; column < 5; column++)
                    {
                        if ((rows[row] & (1 << (4 - column))) == 0)
                            continue;
                        FillRect(frame, new OverlayRect(
                            cursor + column * scale,
                            y + row * scale,
                            scale,
                            scale), color);
                    }
                }
            }
            cursor += 6 * scale;
        }
    }
}
