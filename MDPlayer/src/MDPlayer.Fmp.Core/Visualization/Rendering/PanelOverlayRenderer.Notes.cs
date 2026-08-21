using System.Diagnostics;

namespace Fmp.Core.Visualization.Rendering;

// Note/ribbon methods remain adjacent to their primitives until the next
// mechanical extraction; no alternate note renderer is introduced.
internal sealed partial class PanelOverlayRenderer
{
    private const double NormalRibbonOpacity = 0.72;
    private static bool IsSsgMode(VisualizationNoteMode mode)
        => mode is VisualizationNoteMode.SsgTone
            or VisualizationNoteMode.SsgToneNoise
            or VisualizationNoteMode.SsgNoise
            or VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeToneNoise
            or VisualizationNoteMode.SsgEnvelopeNoise;

    private void DrawPitchedPanel(
        Span<byte> frame,
        PanelData panel,
        long currentSample,
        bool reserveFm3OperatorRibbons,
        (double Min, double Max)? sharedRange = null)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, reserveFm3OperatorRibbons);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        double windowSamples = _layout.WindowSeconds * _timeline.SampleRate;
        (double minMidi, double maxMidi) = sharedRange ?? GetPitchRange(panel, currentSample);
        int preferredRibbonHeight = NormalRibbonHeight(lane, minMidi, maxMidi);
        DrawVisibleNotes(
            frame, panel, panel.Prepared.MainNotes, lane, currentSample, false,
            windowStart, windowEnd, windowSamples, minMidi, maxMidi, _layout.GetPlayheadX(panel.Index),
            preferredRibbonHeight, panel.MainNoteStreamId);
    }

    private void DrawSsgPanel(
        Span<byte> frame,
        PanelData panel,
        long currentSample,
        (double Min, double Max)? sharedRange = null)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, false);
        PreparedNote[] notes = panel.Prepared.MainNotes;
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        double windowSamples = _layout.WindowSeconds * _timeline.SampleRate;
        (double minMidi, double maxMidi) = sharedRange ?? GetPitchRange(panel, currentSample);
        int playheadX = _layout.GetPlayheadX(panel.Index);
        int preferredRibbonHeight = NormalRibbonHeight(lane, minMidi, maxMidi);
        int first = FindFirstVisibleIndex(notes, panel.MainNoteStreamId, windowStart);

        for (int index = first; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample >= windowEnd)
                break;
            if (note.EndSample <= windowStart)
                continue;

            bool noiseOnly = note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise;
            if (noiseOnly)
            {
                DrawNoiseStrip(
                    frame, panel, note, lane, currentSample,
                    windowStart, windowEnd, windowSamples);
                continue;
            }

            if (_performance.Enabled)
                _performance.VisibleNotesVisited++;
            DrawNote(
                frame, panel, note, lane, currentSample, false,
                windowStart, windowEnd, windowSamples, minMidi, maxMidi, playheadX,
                preferredRibbonHeight);
        }
    }

    private void DrawVisibleNotes(
        Span<byte> frame,
        PanelData panel,
        PreparedNote[] notes,
        OverlayRect lane,
        long currentSample,
        bool operatorRibbon,
        long windowStart,
        long windowEnd,
        double windowSamples,
        double panelMinMidi,
        double panelMaxMidi,
        int playheadX,
        int preferredRibbonHeight,
        int streamId = -1)
    {
        int first = FindFirstVisibleIndex(notes, streamId, windowStart);

        for (int index = first; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample >= windowEnd)
                break;
            if (note.EndSample <= windowStart)
                continue;
            if (_performance.Enabled)
                _performance.VisibleNotesVisited++;
            DrawNote(
                frame, panel, note, lane, currentSample, operatorRibbon,
                windowStart, windowEnd, windowSamples, panelMinMidi, panelMaxMidi, playheadX,
                preferredRibbonHeight);
        }
    }

    private (double Min, double Max) GetPitchRange(PanelData panel, long currentSample)
    {
        if (_cameras[panel.Index] is { } camera)
            return camera.GetPreciseRange(currentSample);
        return (panel.Prepared.MinMidi, panel.Prepared.MaxMidi);
    }

    private static int NormalRibbonHeight(OverlayRect lane, double minMidi, double maxMidi)
    {
        double pixelsPerSemitone = lane.Height / (maxMidi - minMidi);
        int minimum = Math.Max(5, (int)Math.Round(lane.Height * 0.055));
        int maximum = Math.Max(minimum, (int)Math.Round(lane.Height * 0.12));
        return Math.Clamp((int)Math.Floor(pixelsPerSemitone * 0.95), minimum, maximum);
    }

    private void DrawNote(
        Span<byte> frame,
        PanelData panel,
        PreparedNote note,
        OverlayRect lane,
        long currentSample,
        bool operatorRibbon,
        long windowStart,
        long windowEnd,
        double windowSamples,
        double panelMinMidi,
        double panelMaxMidi,
        int playheadX,
        int preferredRibbonHeight)
    {
        if (lane.Width <= 0 || lane.Height <= 0)
            return;
        if (note.InitialMidiNote < 0 && !operatorRibbon)
            return;

        if (!TryClipTimeSpanFractional(
                note.StartSample,
                note.EndSample,
                windowStart,
                windowEnd,
                windowSamples,
                lane,
                out double leftX,
                out double rightX))
            return;

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
            leftX = left;
            rightX = right;
        }
        if (right <= left)
            return;

        // Pre-resolved at preparation time; no per-frame color lookup.
        OverlayColor fill = note.Fill;
        OverlayColor accent = _panelAccents[panel.Index];
        bool active = note.StartSample <= currentSample && currentSample < note.EndSample;

        // Active-note flash (§9.1): a 120 ms transient white-mix + size
        // enlargement layered on top of the steady active lightening. It uses
        // absolute note age so it is seek-independent and self-gates to zero
        // for onsets already off-screen (the 0.75 s past window dwarfs 120 ms).
        // Dense passages (§9.3) keep the flash color but suppress the size
        // enlargement so notes do not visually bloom into each other.
        double flashAmount = 0;
        double flashScale = 1.0;
        long ageSamples = currentSample - note.StartSample;
        bool onsetVisible = note.StartSample >= windowStart;
        if (active && ageSamples >= 0 && onsetVisible
            && _effects is EffectsMode.Cinematic or EffectsMode.All)
        {
            double ageMs = ageSamples * 1000.0 / _timeline.SampleRate;
            if (ageMs < ActiveFlashMs)
            {
                double t = ageMs / ActiveFlashMs; // 0 → 1
                // Cubic decay: the attack is brightest at age zero.
                double decay = Math.Pow(1.0 - t, 3);
                flashAmount = ActiveFlashWhiteMix * decay;
                if (!note.IsDenseOnset)
                    flashScale = 1.0 + (ActiveFlashMaxScale - 1.0) * decay;
            }
        }

        double minMidi, maxMidi;
        if (operatorRibbon)
        {
            // FM3 operator ribbons use relative pitch movement: the centre
            // represents the operator's anchor pitch, and approximately ±2
            // semitones are mapped across the ribbon height. Larger deviations
            // are clipped and shown at the edge.
            double opAnchor = Math.Round(note.InitialMidiNote);
            minMidi = opAnchor - 2;
            maxMidi = opAnchor + 2;
        }
        else
        {
            minMidi = panelMinMidi;
            maxMidi = panelMaxMidi;
        }

        bool energyEffects = _effects != EffectsMode.None;
        float energy = active && energyEffects ? GetEnergy(panel.Index, currentSample) : 0.5f;
        int ribbonHeight;
        if (!operatorRibbon && !active && flashAmount == 0 && preferredRibbonHeight > 0)
        {
            ribbonHeight = preferredRibbonHeight;
        }
        else
        {
            double pps = lane.Height / (maxMidi - minMidi);
            int energyPixels = active && energyEffects ? Math.Min(2, (int)Math.Round(energy * 2)) : 0;
            // Size ordinary ribbons from the lane itself, not only from the
            // semitone scale. This keeps them legible after 720p delivery and
            // still lets the pitch camera provide the vertical detail.
            int minimumRibbonHeight = operatorRibbon
                ? Math.Max(2, (int)Math.Round(lane.Height * 0.12))
                : Math.Max(5, (int)Math.Round(lane.Height * 0.055));
            int maximumRibbonHeight = operatorRibbon
                ? Math.Max(minimumRibbonHeight, (int)Math.Round(lane.Height * 0.24))
                : Math.Max(minimumRibbonHeight, (int)Math.Round(lane.Height * 0.12));
            ribbonHeight = Math.Clamp(
                (int)Math.Floor(pps * 0.95 * flashScale) + energyPixels,
                minimumRibbonHeight,
                maximumRibbonHeight);
        }

        // Continuous ribbon: the note body itself follows the interpolated
        // pitch (bends, vibrato, portamento). There is no detached pitch line.
        // §13.4: FM3 operator ribbons render at ×0.6 opacity.
        double opOpacity = operatorRibbon ? 0.6 : 1.0;
        DrawPitchRibbon(
            frame, panel, note, lane, currentSample, leftX, rightX, fill,
            minMidi, maxMidi, ribbonHeight, opOpacity, flashAmount, active, energy,
            playheadX, windowStart, windowSamples / lane.Width);

        long decoStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        // Onset cap (§8.5): a bright, opaque, accent-bordered bar at the note
        // start, enlarged for the first ~110 ms. Retriggers get a double cap
        // (│▌) so repeated attacks on the same pitch remain visible. Caps are
        // drawn only for onsets that occur inside the visible window — an
        // onset that already happened off-screen must not fake a cap at the
        // lane edge.
        int capWidth = Math.Max(3, lane.Width / CapWidthDivisor);
        if (onsetVisible)
        {
            double startMidi = note.StartMidiNote;
            if (double.IsNaN(startMidi))
            {
                startMidi = PitchContour.PitchAtSample(
                    note, note.StartSample, _samplesPerFrame);
            }
            double ageMs = (currentSample - note.StartSample) * 1000.0 / _timeline.SampleRate;
            bool enlarged = ageMs >= 0 && ageMs < OnsetCapEnlargedMs;
            int capX = (int)Math.Round(
                _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane));
            int capHeight = ribbonHeight + (enlarged ? 4 : 2);
            int capCentreY = MidiToY(startMidi, minMidi, maxMidi, lane);
            int capTop = Math.Clamp(capCentreY - capHeight / 2, lane.Y, lane.Bottom - 1);
            int capBottom = Math.Clamp(capCentreY + (capHeight - capHeight / 2), lane.Y, lane.Bottom);
            if (capBottom > capTop && capX >= lane.X && capX < lane.Right)
            {
                int blockX = capX;
                if (note.IsRetrigger)
                {
                    // Thin accent bar (the "│"), then the bordered block.
                    DrawVerticalLine(frame, capX, capTop, capBottom - 1, accent);
                    blockX = Math.Min(lane.Right - 1, capX + 1);
                }
                int capFillWidth = capWidth + (enlarged ? 1 : 0);
                int blockLeft = Math.Max(lane.X, blockX);
                int blockRight = Math.Min(lane.Right, blockX + capFillWidth);
                if (blockRight > blockLeft)
                {
                    FillRectOpaque(frame, blockLeft, blockRight, capTop, capBottom, note.CapFill);
                    DrawHorizontalLine(frame, blockLeft, blockRight - 1, capTop, accent);
                    DrawHorizontalLine(frame, blockLeft, blockRight - 1, capBottom - 1, accent);
                    DrawVerticalLine(frame, blockLeft, capTop, capBottom - 1, accent);
                    DrawVerticalLine(frame, blockRight - 1, capTop, capBottom - 1, accent);
                }
            }

            // Onset contact ripple (§9.2): two expanding rings centered at the
            // playhead × onset-pitch intersection, lasting 280 ms. Dense
            // passages (§9.3) halve the alpha so overlapping ripples do not
            // wash out the notes. Suppressed entirely by --effects none. The
            // ripple uses absolute onset age, so it is seek-independent.
            if (_effects is EffectsMode.Cinematic or EffectsMode.All)
            {
                double rippleAgeMs = (currentSample - note.StartSample) * 1000.0 / _timeline.SampleRate;
                if (rippleAgeMs >= 0 && rippleAgeMs < RippleMs)
                    DrawOnsetRipple(
                        frame, panel, note, lane, startMidi, minMidi, maxMidi, rippleAgeMs);
            }
        }

        // Release endings (§8.6). The renderer never implies a release beyond
        // the actual EndSample.
        if (note.EndSample < windowEnd)
        {
            // The note's real end is inside the window. Hard key-offs get a
            // flat high-contrast end cap; normal releases rely on the ribbon
            // taper already drawn in DrawPitchRibbon.
            if (note.ReleaseStyle == NoteReleaseStyle.HardKeyOff)
            {
                int endX = (int)Math.Round(rightX);
                if (endX >= lane.X && endX < lane.Right)
                {
                    double endMidi = note.EndMidiNote;
                    if (double.IsNaN(endMidi))
                    {
                        endMidi = PitchContour.PitchAtSample(
                            note, note.EndSample, _samplesPerFrame);
                    }
                    int endCentreY = MidiToY(
                        endMidi,
                        minMidi, maxMidi, lane);
                    int endTop = Math.Clamp(endCentreY - (ribbonHeight + 1) / 2 - 1, lane.Y, lane.Bottom - 1);
                    int endBottom = Math.Clamp(endCentreY + (ribbonHeight + 2) / 2 + 1, lane.Y, lane.Bottom);
                    if (endBottom > endTop)
                        FillRectOpaque(frame, endX, Math.Min(lane.Right, endX + 2), endTop, endBottom, note.CapFill);
                }
            }
        }
        else
        {
            // The note continues past the visible window: a half-visible end
            // marker at the panel edge instead of a release cap.
            double endMidi = note.EndMidiNote;
            if (double.IsNaN(endMidi))
            {
                endMidi = PitchContour.PitchAtSample(
                    note, note.EndSample, _samplesPerFrame);
            }
            int markerCentreY = MidiToY(
                endMidi,
                minMidi, maxMidi, lane);
            int markerTop = Math.Clamp(markerCentreY - (ribbonHeight + 1) / 2 - 1, lane.Y, lane.Bottom - 1);
            int markerBottom = Math.Clamp(markerCentreY + (ribbonHeight + 2) / 2 + 1, lane.Y, lane.Bottom);
            if (markerBottom > markerTop)
                DrawVerticalLine(frame, lane.Right - 1, markerTop, markerBottom - 1, note.CapFill.WithAlpha(ClippedEndMarkerAlpha));
        }

        // Edge indicator for a deliberately clipped ornament (§11.3). The
        // camera owns the clip decision; the renderer draws a marker only when
        // the camera flagged an intentional exclusion, rather than drawing
        // triangles for any out-of-range note.
        if (!operatorRibbon
            && note.Pitch.Length > 0
            && _cameras[panel.Index] is { } cam
            && cam.IsClipped(currentSample))
        {
            int edgeX = lane.Right - 2;
            DrawVerticalLine(frame, edgeX, lane.Y + 1, lane.Bottom - 2, accent);
            DrawUpTriangle(frame, edgeX, lane.Y + 2, accent);
            DrawDownTriangle(frame, edgeX, lane.Bottom - 3, accent);
        }

        if (note.Mode is VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeToneNoise)
        {
            double samplesPerPixel = _layout.WindowSeconds * _timeline.SampleRate / lane.Width;
            double sampleAtLeft = Math.Clamp(
                windowStart + (left + 0.5 - lane.X) * samplesPerPixel, note.StartSample, note.EndSample);
            double leftMidi = PitchContour.PitchAtSample(note, (long)Math.Round(sampleAtLeft), _samplesPerFrame);
            int labelY = Math.Clamp(MidiToY(leftMidi, minMidi, maxMidi, lane) - ribbonHeight / 2, lane.Y, lane.Bottom - 1);
            // The envelope token follows the onset cap so it is never covered.
            int labelX = left + (onsetVisible ? capWidth + 2 : 2);
            DrawText(frame, labelX, labelY, "E", BrightText, 1, right - 1);
        }

        if (!operatorRibbon && !IsSsgMode(note.Mode))
        {
            // Active pitch marker: a small bright marker centered on the pitch
            // contour at the playhead, making vibrato and bends easier to follow.
            double activeMidi = active
                ? PitchContour.PitchAtSample(note, currentSample, _samplesPerFrame)
                : double.NaN;
            if (active)
            {
                if (activeMidi >= 0)
                {
                    int markerY = MidiToY(activeMidi, minMidi, maxMidi, lane);
                    if (lane.Contains(playheadX, markerY))
                    {
                        DrawContactFlare(frame, playheadX, markerY, accent, lane);
                        DrawPitchMarker(frame, playheadX, markerY, accent.Lighten(0.5), lane);
                        // Keep a crisp accent-colored contact edge inside the
                        // broader flare so the musical intersection remains
                        // readable after scaling and compression.
                        DrawHorizontalLine(
                            frame,
                            Math.Max(lane.X, playheadX + 3),
                            Math.Min(lane.Right - 1, playheadX + 6),
                            markerY,
                            accent);
                    }
                }
            }

            // Edge chevrons for out-of-range bends: if the actual pitch is
            // outside the viewport, draw a chevron at the edge.
            if (active && _cameras[panel.Index] != null)
            {
                double lo = panelMinMidi;
                double hi = panelMaxMidi;
                if (activeMidi < lo)
                    DrawChevron(frame, playheadX, lane.Y, true, accent.Lighten(0.5), lane);
                else if (activeMidi >= hi)
                    DrawChevron(frame, playheadX, lane.Bottom - 1, false, accent.Lighten(0.5), lane);
            }
        }
        if (_performance.Enabled)
            _performance.RibbonDecorationTicks += Stopwatch.GetTimestamp() - decoStart;
    }

    /// <summary>Draws the active pitch anchor and its compact contact flare.</summary>
    private void DrawPitchMarker(Span<byte> frame, int cx, int cy, OverlayColor color, OverlayRect lane)
    {
        OverlayColor halo = color.WithAlpha(90);
        for (int dx = -4; dx <= 4; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                if (lane.Contains(cx + dx, cy + dy))
                    SetPixel(frame, cx + dx, cy + dy, halo);
            }
        }

        for (int dx = -3; dx <= 3; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (lane.Contains(cx + dx, cy + dy))
                    SetPixel(frame, cx + dx, cy + dy, color);
            }
        }
    }

    private void DrawContactFlare(
        Span<byte> frame,
        int playheadX,
        int markerY,
        OverlayColor accent,
        OverlayRect lane)
    {
        OverlayColor flare = accent.Lighten(0.65).WithAlpha(190);
        DrawHorizontalLine(
            frame,
            Math.Max(lane.X, playheadX - 10),
            Math.Min(lane.Right - 1, playheadX + 10),
            markerY,
            flare);
        DrawHorizontalLine(
            frame,
            Math.Max(lane.X, playheadX - 5),
            Math.Min(lane.Right - 1, playheadX + 5),
            markerY - 1,
            flare.WithAlpha(80));
        DrawHorizontalLine(
            frame,
            Math.Max(lane.X, playheadX - 5),
            Math.Min(lane.Right - 1, playheadX + 5),
            markerY + 1,
            flare.WithAlpha(80));
        DrawVerticalLine(
            frame,
            playheadX,
            Math.Max(lane.Y, markerY - 4),
            Math.Min(lane.Bottom - 1, markerY + 4),
            flare.WithAlpha(95));
    }

    /// <summary>
    /// Onset contact ripple (§9.2): two expanding rings centered at the
    /// playhead × onset-pitch intersection. The radius grows from 2 px to a
    /// panel-height-scaled maximum (15 px at 720p reference) over 280 ms;
    /// alpha decays from 205 to 0. FM panels draw compact rectangles, SSG
    /// panels draw narrow diamonds. Dense onsets (§9.3) halve the alpha. All
    /// clipping is to the lane so ripples never bleed into the scope.
    /// </summary>
    private void DrawOnsetRipple(
        Span<byte> frame,
        PanelData panel,
        PreparedNote note,
        OverlayRect lane,
        double onsetMidi,
        double minMidi,
        double maxMidi,
        double ageMs)
    {
        int playheadX = _layout.GetPlayheadX(panel.Index);
        int centreY = MidiToY(
            onsetMidi,
            minMidi, maxMidi, lane);
        if (!lane.Contains(playheadX, centreY))
            return;

        double t = ageMs / RippleMs; // 0 → 1
        // Linear radius expansion; alpha uses cubic ease-out for a softer fade.
        double finalRadius = Math.Clamp(15.0 * Height / 720.0, 12, 24);
        double radius = RippleInitialRadius + (finalRadius - RippleInitialRadius) * t;
        double alphaDecay = Math.Pow(1.0 - t, 3);
        int alpha = (int)Math.Round(RippleInitialAlpha * alphaDecay);
        if (note.IsDenseOnset)
            alpha /= 2;
        if (alpha <= 0)
            return;

        OverlayColor accent = _panelAccents[panel.Index].Lighten(0.5);
        OverlayColor ringColor = accent.WithAlpha((byte)Math.Clamp(alpha, 0, 255));

        // Two rings: one at full radius, one lagging at 65% for a "double ring"
        // effect without a particle system.
        int r1 = (int)Math.Round(radius);
        int r2 = Math.Max(2, (int)Math.Round(radius * 0.65));
        byte ring2Alpha = (byte)Math.Clamp(alpha * 2 / 3, 0, 255);
        OverlayColor ring2Color = accent.WithAlpha(ring2Alpha);

        bool ssg = IsSsgMode(note.Mode);
        DrawRippleRing(frame, playheadX, centreY, r1, ringColor, ssg, lane);
        // Cinematic mode gets one restrained secondary transient. `All` is
        // retained as the legacy opt-in for the original double-ring effect.
        if (_effects == EffectsMode.All)
            DrawRippleRing(frame, playheadX, centreY, r2, ring2Color, ssg, lane);
    }

    /// <summary>
    /// Draws a single ripple ring: a rectangle (FM) or diamond (SSG) outline
    /// clipped to <paramref name="lane"/>. Uses SetPixelClipped so the ring
    /// never bleeds into the scope or an adjacent panel.
    /// </summary>
    private void DrawRippleRing(
        Span<byte> frame,
        int cx,
        int cy,
        int radius,
        OverlayColor color,
        bool diamond,
        OverlayRect lane)
    {
        if (radius <= 0 || color.A == 0)
            return;
        if (diamond)
        {
            // Diamond: |x-cx| + |y-cy| == radius.
            for (int i = 0; i <= radius; i++)
            {
                int j = radius - i;
                SetPixelClipped(frame, cx + i, cy + j, color, lane);
                SetPixelClipped(frame, cx + i, cy - j, color, lane);
                SetPixelClipped(frame, cx - i, cy + j, color, lane);
                SetPixelClipped(frame, cx - i, cy - j, color, lane);
            }
        }
        else
        {
            // Compact rectangle outline.
            int left = cx - radius;
            int right = cx + radius;
            int top = cy - radius;
            int bottom = cy + radius;
            for (int x = left; x <= right; x++)
            {
                SetPixelClipped(frame, x, top, color, lane);
                SetPixelClipped(frame, x, bottom, color, lane);
            }
            for (int y = top; y <= bottom; y++)
            {
                SetPixelClipped(frame, left, y, color, lane);
                SetPixelClipped(frame, right, y, color, lane);
            }
        }
    }

    /// <summary>
    /// Draws a small chevron at the lane edge to indicate an out-of-range bend.
    /// aboveRange=true draws "/\" (pointing up), false draws "\/" (pointing down).
    /// All pixels are clipped to <paramref name="lane"/> so the chevron never
    /// bleeds into the header or an adjacent panel.
    /// </summary>
    private void DrawChevron(Span<byte> frame, int cx, int edgeY, bool aboveRange, OverlayColor color, OverlayRect lane)
    {
        if (!lane.Contains(cx, edgeY))
            return;

        if (aboveRange)
        {
            SetPixelClipped(frame, cx - 1, edgeY + 1, color, lane);
            SetPixelClipped(frame, cx, edgeY, color, lane);
            SetPixelClipped(frame, cx + 1, edgeY + 1, color, lane);
        }
        else
        {
            SetPixelClipped(frame, cx - 1, edgeY - 1, color, lane);
            SetPixelClipped(frame, cx, edgeY, color, lane);
            SetPixelClipped(frame, cx + 1, edgeY - 1, color, lane);
        }
    }

    private void SetPixelClipped(Span<byte> frame, int x, int y, OverlayColor color, OverlayRect clip)
    {
        if (clip.Contains(x, y))
            SetPixel(frame, x, y, color);
    }

    /// <summary>
    /// Renders a note as a continuous ribbon whose centreline follows the
    /// linearly interpolated pitch (Visualization 2.0 §8.2). Each output
    /// column is filled around the centreline with fractional vertical edge
    /// coverage, so bends and vibrato move smoothly; the note's time edges
    /// keep fractional horizontal coverage so starts/ends do not stair-step
    /// as the window scrolls. No per-frame allocations.
    /// </summary>
    private void DrawPitchRibbon(
        Span<byte> frame,
        PanelData panel,
        PreparedNote note,
        OverlayRect lane,
        long currentSample,
        double leftX,
        double rightX,
        OverlayColor fill,
        double minMidi,
        double maxMidi,
        int ribbonHeight,
        double opacityFactor = 1.0,
        double flashAmount = 0,
        bool active = false,
        float energy = 0,
        int playheadX = 0,
        long windowStart = 0,
        double samplesPerPixel = 0)
    {
        int firstX = Math.Max(lane.X, (int)Math.Ceiling(leftX - 1e-9));
        int lastXExclusive = Math.Min(lane.Right, (int)Math.Floor(rightX + 1e-9));
        if (lastXExclusive <= firstX)
            return;

        double leftCoverage = Math.Clamp(firstX - leftX, 0, 1);
        double rightCoverage = Math.Clamp(rightX - lastXExclusive, 0, 1);

        if (samplesPerPixel <= 0)
        {
            windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
            samplesPerPixel = _layout.WindowSeconds * _timeline.SampleRate / lane.Width;
        }
        double half = ribbonHeight / 2.0;

        bool stipple = note.Mode is VisualizationNoteMode.SsgToneNoise
            or VisualizationNoteMode.SsgEnvelopeToneNoise;
        bool stripe = note.Mode is VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeNoise
            or VisualizationNoteMode.SsgEnvelopeToneNoise;
        OverlayColor accent = _panelAccents[panel.Index];

        int firstColumn = Math.Max(lane.X, firstX - 1);
        int lastColumn = Math.Min(lane.Right - 1, lastXExclusive);
        if (playheadX == 0)
            playheadX = _layout.GetPlayheadX(panel.Index);
        int pitchSegment = -1;

        // Normal releases taper the final 40–80 ms of the ribbon (§8.6);
        // hard key-offs keep full opacity up to their flat end cap. Notes
        // shorter than the taper window are left un-tapered so they stay
        // visible.
        bool taper = note.ReleaseStyle == NoteReleaseStyle.Normal
            && note.EndSample - note.StartSample > _taperSamples;

        // Inactive, non-decorated ribbons have constant Y per ZOH pitch
        // segment, so rasterize each segment as exact alpha-batched runs.
        // This avoids a pitch lookup per column and keeps blending row-major
        // and exact (grid visible through translucent). Active/ornamented
        // ribbons continue through the full per-column path. TestDisableZohRuns
        // forces the per-column reference for pixel-equivalence tests.
        if (!TestDisableZohRuns
            && !stipple
            && !stripe
            && !active
            && opacityFactor == 1.0
            && flashAmount == 0)
        {
            DrawZohPitchRibbonRuns(
                frame,
                note,
                lane,
                panel.Index,
                currentSample,
                fill,
                firstX,
                lastXExclusive,
                leftCoverage,
                rightCoverage,
                windowStart,
                samplesPerPixel,
                half,
                taper,
                minMidi,
                maxMidi,
                playheadX);
            return;
        }

        for (int x = firstColumn; x <= lastColumn; x++)
        {
            if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
            double alphaFactor = 1.0;
            if (x == firstX - 1)
                alphaFactor = leftCoverage;
            else if (x == lastXExclusive)
                alphaFactor = rightCoverage;

            double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
            if (sample < note.StartSample)
                sample = note.StartSample;
            else if (sample > note.EndSample)
                sample = note.EndSample;

            bool contact = Math.Abs(x - playheadX) <= 2;
            double temporalOpacity;
            if (contact)
            {
                temporalOpacity = 1.0;
            }
            else if (sample > currentSample)
            {
                temporalOpacity = 0.35;
            }
            else
            {
                double ageSeconds = Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate);
                temporalOpacity = 0.70 - 0.35 * Math.Clamp(ageSeconds / 0.25, 0, 1);
            }
            alphaFactor *= NormalRibbonOpacity * opacityFactor * temporalOpacity;
            if (alphaFactor <= 0)
                continue;

            if (taper)
                alphaFactor *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
            if (alphaFactor <= 0)
                continue;

            int prevSegment = pitchSegment;
            long samplePosition = (long)Math.Round(sample);
            double centreY = MidiToY(
                PitchContour.PitchAtSampleMonotonic(note, samplePosition, _samplesPerFrame, ref pitchSegment),
                minMidi, maxMidi, lane);
            if (_performance.Enabled && pitchSegment != prevSegment) _performance.PitchSegmentsVisited++;
            OverlayColor columnFill = fill;
            if (active)
            {
            double lightening = contact ? 0.30 + flashAmount : 0.14 + flashAmount;
            columnFill = AdjustForEnergy(fill.Lighten(lightening), energy);
            }
            FillColumnFractional(frame, x, centreY - half, centreY + half, columnFill, alphaFactor, lane.Y, lane.Bottom);

            if (stripe)
            {
                int stripeTop = Math.Max(lane.Y, (int)Math.Ceiling(centreY - half));
                int stripeBottom = Math.Min(lane.Bottom, (int)Math.Floor(centreY + half));
                for (int y = stripeTop; y < stripeBottom; y++)
                {
                    // Panel-relative coordinates keep the diagonal texture
                    // fixed while the time window scrolls.
                    if (((x - lane.X) + (y - lane.Y)) % 7 == 0)
                        SetPixel(frame, x, y, accent.WithAlpha(150));
                }
            }

            // SSG tone+noise: stipple the ribbon's top edge, aligned to panel
            // coordinates so the pattern does not shimmer as the window moves.
            if (stipple && ((x - lane.X) & 3) == 0)
            {
                int topRow = (int)Math.Ceiling(centreY - half - 1e-9);
                if (topRow >= lane.Y && topRow < lane.Bottom)
                    SetPixel(frame, x, topRow, accent.WithAlpha((byte)Math.Round(accent.A * temporalOpacity)));
            }
        }
    }

    private void DrawFlatPitchRibbonFast(
        Span<byte> frame,
        PreparedNote note,
        OverlayRect lane,
        long currentSample,
        OverlayColor fill,
        int firstX,
        int lastXExclusive,
        double leftCoverage,
        double rightCoverage,
        long windowStart,
        double samplesPerPixel,
        double half,
        bool taper,
        double minMidi,
        double maxMidi,
        int playheadX)
    {
        int firstColumn = Math.Max(lane.X, firstX - 1);
        int lastColumn = Math.Min(lane.Right - 1, lastXExclusive);
        if (lastColumn < firstColumn)
            return;

        double centreY = MidiToY(note.InitialMidiNote, minMidi, maxMidi, lane);
        double top = centreY - half;
        double bottom = centreY + half;
        int firstFull = Math.Max(lane.Y, (int)Math.Ceiling(top - 1e-9));
        int lastFullExclusive = Math.Min(lane.Bottom, (int)Math.Floor(bottom + 1e-9));
        if (lastFullExclusive <= firstFull)
            return;

        // Exact run-based rasterization for flat ribbons: Y is constant, so
        // the whole body is a set of horizontal runs where the blended alpha
        // byte is constant. This preserves grid lines and black-key bands
        // through translucent ribbons and matches the per-column reference
        // exactly, including vertical fractional edges.
        int bodyFirst = Math.Max(lane.X, firstX);
        int bodyLastExclusive = Math.Min(lane.Right, lastXExclusive);
        if (_performance.Enabled && bodyFirst < bodyLastExclusive) _performance.PitchSegmentsVisited++;
        // Precompute per-column alphaFactor for the body to allow exact
        // batching by the final alpha byte (256 levels, not 32).
        if (bodyFirst < bodyLastExclusive)
        {
            int width = bodyLastExclusive - bodyFirst;
            Span<double> alphaFactors = width <= 1024 ? stackalloc double[width] : new double[width];
            Span<byte> alphaBytes = width <= 1024 ? stackalloc byte[width] : new byte[width];
            for (int i = 0; i < width; i++)
            {
                int x = bodyFirst + i;
                double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
                if (sample < note.StartSample)
                    sample = note.StartSample;
                else if (sample > note.EndSample)
                    sample = note.EndSample;
                double temporalOpacity;
                if (Math.Abs(x - playheadX) <= 2)
                    temporalOpacity = 1.0;
                else if (sample > currentSample)
                    temporalOpacity = 0.35;
                else
                {
                    double ageSeconds = Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate);
                    temporalOpacity = 0.70 - 0.35 * Math.Clamp(ageSeconds / 0.25, 0, 1);
                }
                double af = NormalRibbonOpacity * temporalOpacity;
                if (taper)
                    af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
                alphaFactors[i] = af;
                alphaBytes[i] = (byte)Math.Clamp(Math.Round(fill.A * af), 0, 255);
                if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
            }
            // Batch by exact alpha byte for interior rows; fractional top/bottom
            // rows are handled per-column with coverage to stay pixel-identical.
            double topCoverage = firstFull - top;
            double bottomCoverage = bottom - lastFullExclusive;
            bool hasTopFrac = topCoverage > 1e-3 && firstFull > lane.Y;
            bool hasBottomFrac = bottomCoverage > 1e-3 && lastFullExclusive < lane.Bottom;
            int runStart = 0;
            byte prevAlpha = alphaBytes[0];
            double prevAf = alphaFactors[0];
            for (int i = 1; i <= width; i++)
            {
                bool flush = i == width || alphaBytes[i] != prevAlpha;
                if (flush)
                {
                    int runLeft = bodyFirst + runStart;
                    int runRight = bodyFirst + i;
                    if (prevAlpha != 0)
                    {
                        // Interior rows: constant alpha for the whole run (row-major).
                        if (lastFullExclusive > firstFull)
                        {
                            OverlayColor src = fill.WithAlpha(prevAlpha);
                            for (int y = firstFull; y < lastFullExclusive; y++)
                            {
                                for (int x = runLeft; x < runRight; x++)
                                    BlendPixel(frame, x, y, src);
                            }
                            if (_performance.Enabled)
                                _performance.RibbonPixelsBlended += (long)(runRight - runLeft) * (lastFullExclusive - firstFull);
                        }
                        // Top fractional row: per-column exact with coverage.
                        if (hasTopFrac)
                        {
                            int y = firstFull - 1;
                            for (int x = runLeft; x < runRight; x++)
                            {
                                double af = alphaFactors[x - bodyFirst];
                                byte a = (byte)Math.Clamp(Math.Round(fill.A * af * topCoverage), 0, 255);
                                if (a != 0)
                                {
                                    BlendPixel(frame, x, y, fill.WithAlpha(a));
                                    if (_performance.Enabled) _performance.RibbonPixelsBlended++;
                                }
                            }
                        }
                        // Bottom fractional row.
                        if (hasBottomFrac)
                        {
                            int y = lastFullExclusive;
                            for (int x = runLeft; x < runRight; x++)
                            {
                                double af = alphaFactors[x - bodyFirst];
                                byte a = (byte)Math.Clamp(Math.Round(fill.A * af * bottomCoverage), 0, 255);
                                if (a != 0)
                                {
                                    BlendPixel(frame, x, y, fill.WithAlpha(a));
                                    if (_performance.Enabled) _performance.RibbonPixelsBlended++;
                                }
                            }
                        }
                    }
                    if (i < width)
                    {
                        runStart = i;
                        prevAlpha = alphaBytes[i];
                        prevAf = alphaFactors[i];
                    }
                }
            }
        }

        // Fractional horizontal edge columns: antialiased and with exact
        // temporal/taper opacity, so a note starting/ending mid-pixel
        // blends correctly through the grid.
        if (firstX > lane.X && leftCoverage > 1e-3)
        {
            int x = firstX - 1;
            double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
            if (sample < note.StartSample) sample = note.StartSample;
            else if (sample > note.EndSample) sample = note.EndSample;
            double temporalOpacity;
            if (Math.Abs(x - playheadX) <= 2) temporalOpacity = 1.0;
            else if (sample > currentSample) temporalOpacity = 0.35;
            else
            {
                double age = Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate);
                temporalOpacity = 0.70 - 0.35 * Math.Clamp(age / 0.25, 0, 1);
            }
            double af = leftCoverage * NormalRibbonOpacity * temporalOpacity;
            if (taper) af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
            if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
            FillColumnFractional(frame, x, top, bottom, fill, af, lane.Y, lane.Bottom);
        }
        if (lastXExclusive < lane.Right && rightCoverage > 1e-3)
        {
            int x = lastXExclusive;
            double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
            if (sample < note.StartSample) sample = note.StartSample;
            else if (sample > note.EndSample) sample = note.EndSample;
            double temporalOpacity;
            if (Math.Abs(x - playheadX) <= 2) temporalOpacity = 1.0;
            else if (sample > currentSample) temporalOpacity = 0.35;
            else
            {
                double age = Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate);
                temporalOpacity = 0.70 - 0.35 * Math.Clamp(age / 0.25, 0, 1);
            }
            double af = rightCoverage * NormalRibbonOpacity * temporalOpacity;
            if (taper) af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
            if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
            FillColumnFractional(frame, x, top, bottom, fill, af, lane.Y, lane.Bottom);
        }
    }

    private void DrawZohPitchRibbonRuns(
        Span<byte> frame,
        PreparedNote note,
        OverlayRect lane,
        int panelIndex,
        long currentSample,
        OverlayColor fill,
        int firstX,
        int lastXExclusive,
        double leftCoverage,
        double rightCoverage,
        long windowStart,
        double samplesPerPixel,
        double half,
        bool taper,
        double minMidi,
        double maxMidi,
        int playheadX)
    {
        int bodyFirst = Math.Max(lane.X, firstX);
        int bodyLastExclusive = Math.Min(lane.Right, lastXExclusive);
        if (bodyLastExclusive <= bodyFirst)
        {
            // Only edge columns may be visible.
            if (firstX > lane.X && leftCoverage > 1e-3)
            {
                int x = firstX - 1;
                double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
                if (sample < note.StartSample) sample = note.StartSample;
                else if (sample > note.EndSample) sample = note.EndSample;
                double temporal = Math.Abs(x - playheadX) <= 2 ? 1.0 : sample > currentSample ? 0.35 : 0.70 - 0.35 * Math.Clamp(Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate) / 0.25, 0, 1);
                double af = leftCoverage * NormalRibbonOpacity * temporal;
                if (taper) af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
                long sp = (long)Math.Round(sample);
                double pitch = PitchContour.PitchAtSample(note, sp, _samplesPerFrame);
                double cy = MidiToY(pitch, minMidi, maxMidi, lane);
                FillColumnFractional(frame, x, cy - half, cy + half, fill, af, lane.Y, lane.Bottom);
                if (_performance.Enabled) { _performance.RibbonColumnsEvaluated++; _performance.PitchSegmentsVisited++; }
            }
            if (lastXExclusive < lane.Right && rightCoverage > 1e-3)
            {
                int x = lastXExclusive;
                double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
                if (sample < note.StartSample) sample = note.StartSample;
                else if (sample > note.EndSample) sample = note.EndSample;
                double temporal = Math.Abs(x - playheadX) <= 2 ? 1.0 : sample > currentSample ? 0.35 : 0.70 - 0.35 * Math.Clamp(Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate) / 0.25, 0, 1);
                double af = rightCoverage * NormalRibbonOpacity * temporal;
                if (taper) af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
                long sp = (long)Math.Round(sample);
                double pitch = PitchContour.PitchAtSample(note, sp, _samplesPerFrame);
                double cy = MidiToY(pitch, minMidi, maxMidi, lane);
                FillColumnFractional(frame, x, cy - half, cy + half, fill, af, lane.Y, lane.Bottom);
                if (_performance.Enabled) { _performance.RibbonColumnsEvaluated++; _performance.PitchSegmentsVisited++; }
            }
            return;
        }

        // Build ZOH pitch segments for the visible body interval.
        // Each segment is constant pitch between pitch points.
        PreparedPitchPoint[] pitchPoints = note.Pitch;
        // Segments as (xStart, xEnd, pitch)
        var segments = new List<(int x0, int x1, double pitch)>(Math.Max(1, pitchPoints.Length + 1));
        int segStartX = bodyFirst;
        double segPitch = note.InitialMidiNote;
        int pitchIdx = -1;
        // Find initial pitchIdx for segStartX's sample
        {
            double sampleAtSegStart = windowStart + (segStartX + 0.5 - lane.X) * samplesPerPixel;
            long sp = (long)Math.Round(Math.Clamp(sampleAtSegStart, note.StartSample, note.EndSample));
            // binary search for pitch point <= sp
            int lo = 0, hi = pitchPoints.Length;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (pitchPoints[mid].SamplePosition <= sp) lo = mid + 1; else hi = mid; }
            pitchIdx = lo - 1;
            segPitch = pitchIdx < 0 ? note.InitialMidiNote : pitchPoints[pitchIdx].MidiNote;
        }
        // Collect pitch change X thresholds within body
        for (int i = pitchIdx + 1; i < pitchPoints.Length; i++)
        {
            long ptSample = pitchPoints[i].SamplePosition;
            if (ptSample < note.StartSample || ptSample > note.EndSample) continue;
            // First x where round(sampleCenter) >= ptSample
            int xThresh = FindFirstXForSample(ptSample, windowStart, samplesPerPixel, lane.X, bodyFirst, bodyLastExclusive);
            if (xThresh <= segStartX) continue;
            if (xThresh > bodyLastExclusive) break;
            segments.Add((segStartX, xThresh, segPitch));
            segStartX = xThresh;
            segPitch = pitchPoints[i].MidiNote;
            pitchIdx = i;
        }
        segments.Add((segStartX, bodyLastExclusive, segPitch));
        if (_performance.Enabled) _performance.PitchSegmentsVisited += segments.Count;

        double[] baseAlphas = GetBaseAlphasForLane(panelIndex, lane, windowStart, samplesPerPixel, currentSample, playheadX);

        // Taper-tail X threshold: columns at or beyond this need per-X taper
        // evaluation; everything before it has a constant final alpha per
        // baseAf run.
        int taperTailX = int.MaxValue;
        if (taper)
        {
            double tailStartSample = note.EndSample - _taperSamples;
            taperTailX = FindFirstXForSample(
                (long)Math.Ceiling(tailStartSample), windowStart, samplesPerPixel, lane.X, bodyFirst, bodyLastExclusive);
        }

        // For each pitch segment, render its X interval as alpha-batched runs.
        foreach (var (segX0, segX1, pitch) in segments)
        {
            if (segX1 <= segX0) continue;
            double centreY = MidiToY(pitch, minMidi, maxMidi, lane);
            double top = centreY - half;
            double bottom = centreY + half;
            int firstFull = Math.Max(lane.Y, (int)Math.Ceiling(top - 1e-9));
            int lastFullExclusive = Math.Min(lane.Bottom, (int)Math.Floor(bottom + 1e-9));
            if (lastFullExclusive <= firstFull && !(top < lane.Bottom && bottom > lane.Y)) continue;
            double topCoverage = firstFull - top;
            double bottomCoverage = bottom - lastFullExclusive;
            bool hasTopFrac = topCoverage > 1e-3 && firstFull > lane.Y;
            bool hasBottomFrac = bottomCoverage > 1e-3 && lastFullExclusive < lane.Bottom;

            // Split the segment into the constant-alpha head (before the
            // taper tail; batched directly from the baseAf runs) and the
            // per-column tail (at most ~4 px at 60 fps for a 60 ms window).
            int headEnd = Math.Min(segX1, Math.Max(segX0, taperTailX));

            // ---- Head: batch by baseAf runs (each is one alpha byte). ----
            int x = segX0;
            while (x < headEnd)
            {
                double baseAf = baseAlphas[x - lane.X];
                int runEnd = x + 1;
                while (runEnd < headEnd && baseAlphas[runEnd - lane.X] == baseAf)
                    runEnd++;
                byte alphaByte = (byte)Math.Clamp(Math.Round(fill.A * baseAf), 0, 255);
                if (_performance.Enabled) _performance.RibbonColumnsEvaluated += runEnd - x;
                if (alphaByte != 0)
                {
                    OverlayColor src = fill.WithAlpha(alphaByte);
                    if (lastFullExclusive > firstFull)
                    {
                        for (int y = firstFull; y < lastFullExclusive; y++)
                            for (int px = x; px < runEnd; px++)
                                BlendPixel(frame, px, y, src);
                        if (_performance.Enabled)
                            _performance.RibbonPixelsBlended += (long)(runEnd - x) * (lastFullExclusive - firstFull);
                    }
                    if (hasTopFrac)
                    {
                        byte aTop = (byte)Math.Clamp(Math.Round(fill.A * baseAf * topCoverage), 0, 255);
                        if (aTop != 0)
                        {
                            OverlayColor srcTop = fill.WithAlpha(aTop);
                            int yTopRow = firstFull - 1;
                            for (int px = x; px < runEnd; px++)
                            {
                                BlendPixel(frame, px, yTopRow, srcTop);
                                if (_performance.Enabled) _performance.RibbonPixelsBlended++;
                            }
                        }
                    }
                    if (hasBottomFrac)
                    {
                        byte aBottom = (byte)Math.Clamp(Math.Round(fill.A * baseAf * bottomCoverage), 0, 255);
                        if (aBottom != 0)
                        {
                            OverlayColor srcBottom = fill.WithAlpha(aBottom);
                            int yBottomRow = lastFullExclusive;
                            for (int px = x; px < runEnd; px++)
                            {
                                BlendPixel(frame, px, yBottomRow, srcBottom);
                                if (_performance.Enabled) _performance.RibbonPixelsBlended++;
                            }
                        }
                    }
                }
                x = runEnd;
            }

            // ---- Tail: per-column with exact taper. ----
            for (; x < segX1; x++)
            {
                double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
                if (sample < note.StartSample) sample = note.StartSample;
                else if (sample > note.EndSample) sample = note.EndSample;
                double af = baseAlphas[x - lane.X]
                    * (taper ? Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1) : 1.0);
                byte alphaByte = (byte)Math.Clamp(Math.Round(fill.A * af), 0, 255);
                if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
                if (alphaByte == 0) continue;
                if (lastFullExclusive > firstFull)
                {
                    OverlayColor src = fill.WithAlpha(alphaByte);
                    for (int y = firstFull; y < lastFullExclusive; y++)
                        BlendPixel(frame, x, y, src);
                    if (_performance.Enabled) _performance.RibbonPixelsBlended += lastFullExclusive - firstFull;
                }
                if (hasTopFrac)
                {
                    byte aTop = (byte)Math.Clamp(Math.Round(fill.A * af * topCoverage), 0, 255);
                    if (aTop != 0)
                    {
                        BlendPixel(frame, x, firstFull - 1, fill.WithAlpha(aTop));
                        if (_performance.Enabled) _performance.RibbonPixelsBlended++;
                    }
                }
                if (hasBottomFrac)
                {
                    byte aBottom = (byte)Math.Clamp(Math.Round(fill.A * af * bottomCoverage), 0, 255);
                    if (aBottom != 0)
                    {
                        BlendPixel(frame, x, lastFullExclusive, fill.WithAlpha(aBottom));
                        if (_performance.Enabled) _performance.RibbonPixelsBlended++;
                    }
                }
            }
        }

        // Edge columns with fractional horizontal coverage, now with exact temporal/taper and correct pitch per edge.
        if (firstX > lane.X && leftCoverage > 1e-3)
        {
            int x = firstX - 1;
            double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
            if (sample < note.StartSample) sample = note.StartSample;
            else if (sample > note.EndSample) sample = note.EndSample;
            double temporal = Math.Abs(x - playheadX) <= 2 ? 1.0 : sample > currentSample ? 0.35 : 0.70 - 0.35 * Math.Clamp(Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate) / 0.25, 0, 1);
            double af = leftCoverage * NormalRibbonOpacity * temporal;
            if (taper) af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
            long sp = (long)Math.Round(sample);
            double pitch = PitchContour.PitchAtSample(note, sp, _samplesPerFrame);
            double cy = MidiToY(pitch, minMidi, maxMidi, lane);
            FillColumnFractional(frame, x, cy - half, cy + half, fill, af, lane.Y, lane.Bottom);
            if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
        }
        if (lastXExclusive < lane.Right && rightCoverage > 1e-3)
        {
            int x = lastXExclusive;
            double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
            if (sample < note.StartSample) sample = note.StartSample;
            else if (sample > note.EndSample) sample = note.EndSample;
            double temporal = Math.Abs(x - playheadX) <= 2 ? 1.0 : sample > currentSample ? 0.35 : 0.70 - 0.35 * Math.Clamp(Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate) / 0.25, 0, 1);
            double af = rightCoverage * NormalRibbonOpacity * temporal;
            if (taper) af *= Math.Clamp((note.EndSample - sample) / (double)_taperSamples, 0, 1);
            long sp = (long)Math.Round(sample);
            double pitch = PitchContour.PitchAtSample(note, sp, _samplesPerFrame);
            double cy = MidiToY(pitch, minMidi, maxMidi, lane);
            FillColumnFractional(frame, x, cy - half, cy + half, fill, af, lane.Y, lane.Bottom);
            if (_performance.Enabled) _performance.RibbonColumnsEvaluated++;
        }
    }

    private int FindFirstXForSample(long targetSample, long windowStart, double samplesPerPixel, int laneX, int low, int high)
    {
        int lo = low, hi = high;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            double sampleCenter = windowStart + (mid + 0.5 - laneX) * samplesPerPixel;
            long sp = (long)Math.Round(sampleCenter);
            if (sp < targetSample) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private void FillFlatOpaqueBand(
        Span<byte> frame,
        OverlayColor fill,
        int left,
        int right,
        int top,
        int bottom,
        double alphaFactor)
    {
        if (right <= left || bottom <= top || alphaFactor <= 0)
            return;
        byte alpha = (byte)Math.Clamp(Math.Round(fill.A * alphaFactor), 0, 255);
        if (alpha == 0)
            return;
        // Exact: blend constant-alpha run over the actual destination pixels
        // so grid lines / black-key bands / waveform remain visible through
        // translucent ribbons. This is still a contiguous row-major run.
        OverlayColor src = fill.WithAlpha(alpha);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
                BlendPixel(frame, x, y, src);
        }
        if (_performance.Enabled)
            _performance.RibbonPixelsBlended += (long)(right - left) * (bottom - top);
    }

    private void DrawFlatEdgeColumn(
        Span<byte> frame,
        OverlayColor fill,
        int x,
        int top,
        int bottom,
        double coverage)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(fill.A * NormalRibbonOpacity * coverage), 0, 255);
        if (alpha == 0) return;
        for (int y = top; y < bottom; y++)
            BlendFlatPixel(frame, x, y, fill, alpha);
        if (_performance.Enabled)
            _performance.RibbonPixelsBlended += bottom - top;
    }

    private void BlendFlatPixel(
        Span<byte> frame,
        int x,
        int y,
        OverlayColor color,
        byte alpha)
    {
        if (alpha == 0)
            return;
        int offset = (y * Width + x) * 4;
        if (alpha == 255)
        {
            frame[offset] = color.R;
            frame[offset + 1] = color.G;
            frame[offset + 2] = color.B;
            frame[offset + 3] = 255;
            return;
        }

        if (frame[offset + 3] == 255)
        {
            int inverse = 255 - alpha;
            frame[offset] = (byte)((color.R * alpha + frame[offset] * inverse + 127) / 255);
            frame[offset + 1] = (byte)((color.G * alpha + frame[offset + 1] * inverse + 127) / 255);
            frame[offset + 2] = (byte)((color.B * alpha + frame[offset + 2] * inverse + 127) / 255);
            return;
        }

        BlendPixel(frame, x, y, color.WithAlpha(alpha));
    }

    /// <summary>
    /// Fills one pixel column with fractional vertical edge coverage for
    /// subpixel pitch motion. Rows fully inside [top, bottom) are written at
    /// alpha * <paramref name="alphaFactor"/>; the first and last fractional
    /// rows are blended with the product of the two coverages.
    /// </summary>
    private void FillColumnFractional(
        Span<byte> frame,
        int x,
        double top,
        double bottom,
        OverlayColor color,
        double alphaFactor,
        int clipTop,
        int clipBottom)
    {
        if (x < 0 || x >= Width || bottom <= top || alphaFactor <= 0)
            return;

        int firstFull = Math.Max(clipTop, (int)Math.Ceiling(top - 1e-9));
        int lastFullExclusive = Math.Min(clipBottom, (int)Math.Floor(bottom + 1e-9));

        // Top fractional edge: coverage of the row just before firstFull.
        double topCoverage = firstFull - top;
        if (topCoverage > 1e-3 && firstFull > clipTop)
        {
            BlendScaled(frame, x, firstFull - 1, color, alphaFactor * topCoverage);
            if (_performance.Enabled) _performance.RibbonPixelsBlended++;
        }

        // Fully covered interior rows.
        if (lastFullExclusive > firstFull)
        {
            if (color.A == 255 && alphaFactor >= 1.0 - 1e-9)
            {
                int offset = (firstFull * Width + x) * 4;
                int stride = Width * 4;
                for (int y = firstFull; y < lastFullExclusive; y++, offset += stride)
                {
                    frame[offset] = color.R;
                    frame[offset + 1] = color.G;
                    frame[offset + 2] = color.B;
                    frame[offset + 3] = 255;
                }
                if (_performance.Enabled) _performance.RibbonPixelsBlended += lastFullExclusive - firstFull;
            }
            else
            {
                byte alpha = (byte)Math.Clamp(Math.Round(color.A * alphaFactor), 0, 255);
                OverlayColor rowColor = color.WithAlpha(alpha);
                for (int y = firstFull; y < lastFullExclusive; y++)
                    BlendPixel(frame, x, y, rowColor);
                if (_performance.Enabled && alpha != 0) _performance.RibbonPixelsBlended += lastFullExclusive - firstFull;
            }
        }

        // Bottom fractional edge: coverage of the row at lastFullExclusive.
        double bottomCoverage = bottom - lastFullExclusive;
        if (bottomCoverage > 1e-3 && lastFullExclusive < clipBottom)
        {
            BlendScaled(frame, x, lastFullExclusive, color, alphaFactor * bottomCoverage);
            if (_performance.Enabled) _performance.RibbonPixelsBlended++;
        }
    }

    private void BlendScaled(Span<byte> frame, int x, int y, OverlayColor color, double alphaFactor)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height || alphaFactor <= 0)
            return;
        byte alpha = (byte)Math.Clamp(Math.Round(color.A * alphaFactor), 0, 255);
        if (alpha == 0)
            return;
        BlendPixel(frame, x, y, color.WithAlpha(alpha));
    }

    private void DrawUpTriangle(Span<byte> frame, int cx, int cy, OverlayColor color)
    {
        if (cy < 0 || cy >= Height) return;
        SetPixel(frame, cx, cy, color);
        if (cy > 0)
            SetPixel(frame, cx, cy - 1, color);
    }

    private void DrawDownTriangle(Span<byte> frame, int cx, int cy, OverlayColor color)
    {
        if (cy < 0 || cy >= Height) return;
        SetPixel(frame, cx, cy, color);
        if (cy < Height - 1)
            SetPixel(frame, cx, cy + 1, color);
    }

    private void DrawNoiseStrip(
        Span<byte> frame,
        PanelData panel,
        PreparedNote note,
        OverlayRect lane,
        long currentSample,
        long windowStart,
        long windowEnd,
        double windowSamples)
    {
        if (!TryClipTimeSpanFractional(
                note.StartSample,
                note.EndSample,
                windowStart,
                windowEnd,
                windowSamples,
                lane,
            out double leftX, out double rightX))
            return;

        int left = Math.Max(lane.X, (int)Math.Floor(leftX));
        int right = Math.Min(lane.Right, (int)Math.Ceiling(rightX));
        if (right <= left)
            return;

        OverlayColor fill = note.Fill;
        OverlayColor accent = _panelAccents[panel.Index];
        bool active = note.StartSample <= currentSample && currentSample < note.EndSample;
        if (active)
            fill = AdjustForEnergy(fill.Lighten(0.14),
                _effects == EffectsMode.None ? 0.5f : GetEnergy(panel.Index, currentSample));

        int height = Math.Clamp(lane.Height / 8, 5, 9);
        var rect = new OverlayRect(left, lane.Bottom - height - 1, right - left, height);
        FillRectFractionalX(frame, leftX, rightX, rect.Y, height, fill);
        StrokeRect(frame, rect, accent, active ? 2 : 1);
        if (note.Mode == VisualizationNoteMode.SsgEnvelopeNoise)
        {
            for (int x = rect.X; x < rect.Right; x++)
            for (int y = rect.Y; y < rect.Bottom; y++)
                if (((x - lane.X) + (y - lane.Y)) % 7 == 0)
                    SetPixel(frame, x, y, accent.WithAlpha(150));
        }
        for (int x = rect.X + 2; x < rect.Right - 1; x += 4)
            SetPixel(frame, x, rect.Y + rect.Height / 2, BrightText.WithAlpha(190));
    }
}
