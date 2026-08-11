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

    private void DrawPitchedPanel(Span<byte> frame, PanelData panel, long currentSample, bool reserveFm3OperatorRibbons)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, reserveFm3OperatorRibbons);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        double windowSamples = _layout.WindowSeconds * _timeline.SampleRate;
        (double minMidi, double maxMidi) = GetPitchRange(panel, currentSample);
        int preferredRibbonHeight = NormalRibbonHeight(lane, minMidi, maxMidi);
        DrawVisibleNotes(
            frame, panel, panel.Prepared.MainNotes, lane, currentSample, false,
            windowStart, windowEnd, windowSamples, minMidi, maxMidi, _layout.GetPlayheadX(panel.Index),
            preferredRibbonHeight);
    }

    private void DrawSsgPanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, false);
        PreparedNote[] notes = panel.Prepared.MainNotes;
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        double windowSamples = _layout.WindowSeconds * _timeline.SampleRate;
        (double minMidi, double maxMidi) = GetPitchRange(panel, currentSample);
        int playheadX = _layout.GetPlayheadX(panel.Index);
        int preferredRibbonHeight = NormalRibbonHeight(lane, minMidi, maxMidi);
        int first = FindFirstVisibleIndex(notes, windowStart);

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
                DrawNoiseStrip(frame, panel, note, lane, currentSample);
                continue;
            }

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
        int preferredRibbonHeight)
    {
        int first = FindFirstVisibleIndex(notes, windowStart);

        for (int index = first; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample >= windowEnd)
                break;
            if (note.EndSample <= windowStart)
                continue;
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
        DrawPitchRibbon(frame, panel, note, lane, currentSample, leftX, rightX, fill, minMidi, maxMidi, ribbonHeight, opOpacity, flashAmount, active, energy);

        // Onset cap (§8.5): a bright, opaque, accent-bordered bar at the note
        // start, enlarged for the first ~110 ms. Retriggers get a double cap
        // (│▌) so repeated attacks on the same pitch remain visible. Caps are
        // drawn only for onsets that occur inside the visible window — an
        // onset that already happened off-screen must not fake a cap at the
        // lane edge.
        int capWidth = Math.Max(3, lane.Width / CapWidthDivisor);
        if (onsetVisible)
        {
            double ageMs = (currentSample - note.StartSample) * 1000.0 / _timeline.SampleRate;
            bool enlarged = ageMs >= 0 && ageMs < OnsetCapEnlargedMs;
            int capX = (int)Math.Round(
                _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane));
            int capCentreY = MidiToY(
                PitchContour.PitchAtSample(note, note.StartSample, _samplesPerFrame),
                minMidi, maxMidi, lane);
            int capHeight = ribbonHeight + (enlarged ? 4 : 2);
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
                    DrawOnsetRipple(frame, panel, note, lane, currentSample, minMidi, maxMidi, rippleAgeMs);
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
                    int endCentreY = MidiToY(
                        PitchContour.PitchAtSample(note, note.EndSample, _samplesPerFrame),
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
            int markerCentreY = MidiToY(
                PitchContour.PitchAtSample(note, note.EndSample, _samplesPerFrame),
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
            if (active)
            {
                double actualMidi = PitchContour.PitchAtSample(note, currentSample, _samplesPerFrame);
                if (actualMidi >= 0)
                {
                    int markerY = MidiToY(actualMidi, minMidi, maxMidi, lane);
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
                double actualMidi = PitchContour.PitchAtSample(note, currentSample, _samplesPerFrame);
                double lo = panelMinMidi;
                double hi = panelMaxMidi;
                if (actualMidi < lo)
                    DrawChevron(frame, playheadX, lane.Y, true, accent.Lighten(0.5), lane);
                else if (actualMidi >= hi)
                    DrawChevron(frame, playheadX, lane.Bottom - 1, false, accent.Lighten(0.5), lane);
            }
        }
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
        long currentSample,
        double minMidi,
        double maxMidi,
        double ageMs)
    {
        int playheadX = _layout.GetPlayheadX(panel.Index);
        int centreY = MidiToY(
            PitchContour.PitchAtSample(note, note.StartSample, _samplesPerFrame),
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
        float energy = 0)
    {
        int firstX = Math.Max(lane.X, (int)Math.Ceiling(leftX - 1e-9));
        int lastXExclusive = Math.Min(lane.Right, (int)Math.Floor(rightX + 1e-9));
        if (lastXExclusive <= firstX)
            return;

        double leftCoverage = Math.Clamp(firstX - leftX, 0, 1);
        double rightCoverage = Math.Clamp(rightX - lastXExclusive, 0, 1);

        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        double samplesPerPixel = _layout.WindowSeconds * _timeline.SampleRate / lane.Width;
        double half = ribbonHeight / 2.0;

        bool stipple = note.Mode is VisualizationNoteMode.SsgToneNoise
            or VisualizationNoteMode.SsgEnvelopeToneNoise;
        bool stripe = note.Mode is VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeNoise
            or VisualizationNoteMode.SsgEnvelopeToneNoise;
        OverlayColor accent = _panelAccents[panel.Index];

        int firstColumn = Math.Max(lane.X, firstX - 1);
        int lastColumn = Math.Min(lane.Right - 1, lastXExclusive);
        int playheadX = _layout.GetPlayheadX(panel.Index);
        int pitchSegment = -1;

        // Normal releases taper the final 40–80 ms of the ribbon (§8.6);
        // hard key-offs keep full opacity up to their flat end cap. Notes
        // shorter than the taper window are left un-tapered so they stay
        // visible.
        bool taper = note.ReleaseStyle == NoteReleaseStyle.Normal
            && note.EndSample - note.StartSample > _taperSamples;

        // Most publishing notes are flat, inactive ribbons. Their geometry is
        // constant in Y, so rasterize those pixels as contiguous opaque bands.
        // This preserves the temporal opacity and release taper as deterministic
        // quantized runs while avoiding a pitch lookup and strided writes for
        // every pixel. Active/ornamented ribbons continue through the full path.
        if (note.Pitch.Length == 0
            && !stipple
            && !stripe
            && !active
            && opacityFactor == 1.0
            && flashAmount == 0)
        {
            DrawFlatPitchRibbonFast(
                frame,
                note,
                lane,
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

            long samplePosition = (long)Math.Round(sample);
            double centreY = MidiToY(
                PitchContour.PitchAtSampleMonotonic(note, samplePosition, _samplesPerFrame, ref pitchSegment),
                minMidi, maxMidi, lane);
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

        // Quantize only the opacity of a flat body into small, deterministic
        // runs. Opaque row fills keep the raster hot path contiguous; the
        // visible temporal/release ramp remains a sequence of short bands.
        int bodyFirst = Math.Max(lane.X, firstX);
        int bodyLastExclusive = Math.Min(lane.Right, lastXExclusive);
        int runStart = bodyFirst;
        int previousBucket = -1;
        for (int x = bodyFirst; x <= bodyLastExclusive; x++)
        {
            double alphaFactor = 1.0;
            if (x < bodyLastExclusive)
            {
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

                alphaFactor = NormalRibbonOpacity * temporalOpacity;
                if (taper)
                    alphaFactor *= Math.Clamp(
                        (note.EndSample - sample) / (double)_taperSamples,
                        0,
                        1);
            }

            int bucket = Math.Clamp((int)Math.Round(alphaFactor * 32), 0, 32);
            if (previousBucket < 0)
                previousBucket = bucket;
            else if (bucket != previousBucket)
            {
                FillFlatOpaqueBand(
                    frame,
                    fill,
                    runStart,
                    x,
                    firstFull,
                    lastFullExclusive,
                    previousBucket / 32.0);
                runStart = x;
                previousBucket = bucket;
            }
        }

        if (runStart < bodyLastExclusive && previousBucket >= 0)
        {
            FillFlatOpaqueBand(
                frame,
                fill,
                runStart,
                bodyLastExclusive,
                firstFull,
                lastFullExclusive,
                previousBucket / 32.0);
        }

        // Keep the two fractional horizontal edge columns antialiased. They
        // are at most two narrow columns per note and do not affect the bulk
        // rasterization budget.
        if (firstX > lane.X && leftCoverage > 1e-3)
            DrawFlatEdgeColumn(frame, fill, firstX - 1, firstFull, lastFullExclusive, leftCoverage);
        if (lastXExclusive < lane.Right && rightCoverage > 1e-3)
            DrawFlatEdgeColumn(frame, fill, lastXExclusive, firstFull, lastFullExclusive, rightCoverage);
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
        OverlayColor opaque = new(
            (byte)((fill.R * alpha + 15 * (255 - alpha) + 127) / 255),
            (byte)((fill.G * alpha + 17 * (255 - alpha) + 127) / 255),
            (byte)((fill.B * alpha + 25 * (255 - alpha) + 127) / 255));
        FillRectOpaque(frame, left, right, top, bottom, opaque);
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
        for (int y = top; y < bottom; y++)
            BlendFlatPixel(frame, x, y, fill, alpha);
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
            BlendScaled(frame, x, firstFull - 1, color, alphaFactor * topCoverage);

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
            }
            else
            {
                byte alpha = (byte)Math.Clamp(Math.Round(color.A * alphaFactor), 0, 255);
                OverlayColor rowColor = color.WithAlpha(alpha);
                for (int y = firstFull; y < lastFullExclusive; y++)
                    BlendPixel(frame, x, y, rowColor);
            }
        }

        // Bottom fractional edge: coverage of the row at lastFullExclusive.
        double bottomCoverage = bottom - lastFullExclusive;
        if (bottomCoverage > 1e-3 && lastFullExclusive < clipBottom)
            BlendScaled(frame, x, lastFullExclusive, color, alphaFactor * bottomCoverage);
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
        long currentSample)
    {
        if (!TryClipTimeSpanFractional(note.StartSample, note.EndSample, currentSample, lane,
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
