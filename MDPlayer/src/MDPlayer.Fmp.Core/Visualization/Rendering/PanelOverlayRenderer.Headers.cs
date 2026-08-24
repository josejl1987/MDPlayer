namespace Fmp.Core.Visualization.Rendering;

// Header and metadata drawing seam. Static text is still rasterized once.
internal sealed partial class PanelOverlayRenderer
{
    private void DrawStaticFm3Ribbons(Span<byte> frame, int panelIndex)
    {
        OverlayRect ribbons = _layout.GetFm3OperatorRect(panelIndex);
        FillRect(frame, ribbons, new OverlayColor(12, 14, 21));
        int rowHeight = Math.Max(1, ribbons.Height / 4);
        for (int op = 0; op < 4; op++)
        {
            int y = ribbons.Y + op * rowHeight;
            DrawHorizontalLine(frame, ribbons.X, ribbons.Right - 1, y, GridLine);
            DrawText(frame, ribbons.X + 2, y + 1, $"OP{op + 1}", TertiaryText, 1, ribbons.X + 20);
        }
    }

    private void DrawStaticRhythmRows(Span<byte> frame, int panelIndex)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        int labelWidth = _layout.PitchLabelWidth;
        FillRect(frame, new OverlayRect(timeline.X, timeline.Y, labelWidth, timeline.Height), HeaderBackground);
        PanelRowDefinition[] rows = _panels[panelIndex].Prepared.Rows;
        if (rows.Length == 0)
            return;
        int rowHeight = Math.Max(1, timeline.Height / rows.Length);
        for (int row = 0; row < rows.Length; row++)
        {
            int y = timeline.Y + row * rowHeight;
            DrawHorizontalLine(frame, timeline.X + labelWidth, timeline.Right - 1, y, GridLine);
            DrawGutterLabelRightAligned(
                frame,
                timeline,
                rows[row].Label,
                y + Math.Max(1, (rowHeight - 7) / 2));
        }
    }

    /// <summary>
    /// Draws a gutter label (rhythm row / PCM lane) right-aligned within the
    /// pitch gutter, mirroring the pitch-label inset.
    /// </summary>
    private void DrawGutterLabelRightAligned(Span<byte> frame, OverlayRect timeline, string label, int y)
    {
        int gutter = _layout.PitchLabelWidth;
        int minX = timeline.X + _layout.PitchLabelInsetLeft;
        int maxX = timeline.X + gutter - _layout.PitchLabelInsetRight;
        int labelWidth = BitmapFont.MeasureText(label, 1);
        int x = maxX - labelWidth;
        if (x < minX)
            x = minX;
        DrawText(frame, x, y, label, TertiaryText, 1, maxX);
    }

    private void DrawClock(Span<byte> frame, long currentSample)
    {
        OverlayRect bar = _layout.TopBarRect;
        string clock = FormatClock(currentSample);
        int clockWidth = BitmapFont.MeasureText(clock, 2);
        int clockX = bar.Right - _layout.SafeHorizontalMargin - clockWidth;
        int clockY = bar.Y + Math.Max(2, (bar.Height - 14) / 2);
        DrawText(frame, clockX, clockY, clock, PrimaryText, 2, bar.Right - _layout.SafeHorizontalMargin);
    }

    private void DrawLoopLabel(Span<byte> frame, long frameIndex)
    {
        if ((ulong)frameIndex >= (ulong)_loopLabelByFrame.Length)
            return;
        string label = _loopLabelByFrame[frameIndex];
        if (string.IsNullOrEmpty(label))
            return;

        OverlayRect bar = _layout.TopBarRect;
        string clock = FormatClock(OverlayLayout.FrameToSample(
            frameIndex, _timeline.SampleRate, FpsNumerator, FpsDenominator));
        int clockX = bar.Right - _layout.SafeHorizontalMargin - BitmapFont.MeasureText(clock, 2);
        int labelWidth = BitmapFont.MeasureText(label, 1);
        DrawText(frame, clockX - labelWidth - 8,
            bar.Y + Math.Max(2, (bar.Height - 7) / 2), label, MutedText, 1, clockX - 4);
    }

    private void DrawProgress(Span<byte> frame, long currentSample)
    {
        OverlayRect bar = _layout.BottomBarRect;

        // Progress track: a 4px strip at the top edge of the bottom bar.
        int progressHeight = 4;
        FillRect(frame, new OverlayRect(bar.X, bar.Y, bar.Width, progressHeight), new OverlayColor(33, 36, 49));
        double progress = _timeline.EndSample <= _timeline.StartSample
            ? 0
            : (currentSample - _timeline.StartSample) / (double)(_timeline.EndSample - _timeline.StartSample);
        progress = Math.Clamp(progress, 0, 1);
        double progressRight = bar.X + progress * bar.Width;
        FillRectFractionalX(frame, bar.X, progressRight, bar.Y, progressHeight, new OverlayColor(122, 164, 255));
    }

    /// <summary>
    /// Returns the cached "MM:SS / MM:SS" clock string for the whole second
    /// containing <paramref name="currentSample"/>. The clock only changes once
    /// per second, so caching avoids per-frame string formatting.
    /// </summary>
    private string FormatClock(long currentSample)
    {
        long second = Math.Clamp(
            (currentSample - _timeline.StartSample) / _timeline.SampleRate,
            0,
            _clockBySecond.Length - 1);
        return _clockBySecond[second];
    }

    /// <summary>
    /// Truncates text to fit within <paramref name="maxWidth"/> pixels at the
    /// given scale, appending "..." when truncation is necessary. Uses the
    /// bitmap font's monospace advance (6px per glyph) for measurement.
    /// </summary>
    private static string Ellipsize(string text, int scale, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return "";

        int fullWidth = BitmapFont.MeasureText(text, scale);
        if (fullWidth <= maxWidth)
            return text;

        int ellipsisWidth = BitmapFont.MeasureText("...", scale);
        int available = maxWidth - ellipsisWidth;
        if (available <= 0)
            return "";

        // MeasureText uses (Length * 6 * scale - scale) because the last glyph
        // has no trailing advance. So we can't simply divide available by a
        // per-char constant — verify with MeasureText and shrink until it fits.
        int maxChars = Math.Min(text.Length, available / (6 * scale) + 1);
        while (maxChars > 0 && BitmapFont.MeasureText(text[..maxChars], scale) > available)
            maxChars--;
        if (maxChars <= 0)
            return "";
        if (maxChars >= text.Length)
            return text;
        return text[..maxChars] + "...";
    }

    private void DrawDynamicPanelHeader(Span<byte> frame, PanelData panel, long currentSample)
    {
        if (_layout.Variant == VisualizationLayoutVariant.PerformanceLanes)
        {
            DrawPerformanceLaneStatus(frame, panel, currentSample);
            return;
        }

        OverlayRect header = _layout.GetHeaderRect(panel.Index);
        if (header.Height <= 0)
            return;
        PreparedNote active = FindActive(panel.Prepared.MainNotes, panel.MainNoteStreamId, currentSample);
        if (active == null && panel.TrackKind == VisualizationTrackKind.FmOperatorGroup)
        {
            int operatorCount = Math.Min(
                panel.Prepared.OperatorNotes.Length, panel.OperatorNoteStreamIds.Length);
            for (int operatorIndex = 0; operatorIndex < operatorCount; operatorIndex++)
            {
                active = FindActive(
                    panel.Prepared.OperatorNotes[operatorIndex],
                    panel.OperatorNoteStreamIds[operatorIndex],
                    currentSample);
                if (active != null)
                    break;
            }
        }

        string modeToken = active != null && IsSsgMode(active.Mode)
            && currentSample >= active.StartSample
            && currentSample - active.StartSample <= (long)Math.Round(0.600 * _timeline.SampleRate)
            ? SsgModeToken(active.Mode)
            : "";
        string state = !string.IsNullOrEmpty(modeToken)
            ? modeToken
            : active == null
                ? ""
                : active.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise
                    ? "NOISE"
                    : FormatPitchWithCents(PitchContour.PitchAtSample(active, currentSample, _samplesPerFrame));

        // §13.2: instrument-change overlay. When an instrument change occurred
        // within the last 800 ms, replace the normal patch token with
        // ALG/FB/AMS/PMS text + four compact operator bars (§13.3).
        PreparedNote changeNote = FindRecentInstrumentChange(panel, currentSample);
        bool showOverlay = changeNote != null;

        string patch = showOverlay
            ? ""
            : active?.Text.ShortLabel ?? "";
        string badges = showOverlay
            ? changeNote.Text.ChangeLabel
            : active?.Text.BadgeLabel ?? "";

        if (!showOverlay
            && ChipPanelHeaderBuilder.TryBuild(
                panel.Prepared, currentSample, _chipHeaderCursors[panel.Index], out PanelHeaderData chipHeader))
        {
            patch = chipHeader.Label;
            badges = "";
        }

        // The underline is the compact activity hierarchy: active panels are
        // full strength, recent audio is slightly dimmer, used channels keep
        // their identity, and empty panels retain only a quiet accent. It is
        // drawn inside the existing dynamic header restore rectangle.
        float energy = GetEnergy(panel.Index, currentSample);
        bool contactActivity = active != null
            || HasPanelContactActivity(panel, currentSample)
            || (HasEnergyEnvelope(panel.Index) && energy > 0.02f);
        bool recentActivity = !contactActivity && HasRecentActivity(panel.Index, currentSample);
        double headerWeight = contactActivity
            ? 1.0
            : recentActivity
                ? 0.80
                : panel.Prepared.HasTrackEvents
                    ? 0.60
                    : 0.40;
        OverlayColor headerAccent = panel.Prepared.Accent
            .Lighten((headerWeight - 0.40) * 0.30)
            .WithAlpha((byte)Math.Clamp(Math.Round(80 + headerWeight * 90), 0, 255));
        // The underline spans the usable header width, left of the patch slot,
        // so it never crosses the three header text rectangles.
        PanelHeaderLayout slots = _layout.HeaderSlots(panel.Index);
        int underlineLeft = Math.Min(slots.Name.X, slots.State.X);
        DrawHorizontalLine(frame, underlineLeft, slots.Patch.Right - 1, header.Bottom - 1, headerAccent);

        // Lead the state (higher contrast) in its own slot; it must never be
        // dropped when space is tight.
        int primaryScale = Height >= 720 ? 2 : 1;
        if (!string.IsNullOrEmpty(state))
        {
            string primary = CachedHeaderText(
                _headerStateInputs,
                _headerStateOutputs,
                _headerStateWidths,
                panel.Index,
                state,
                primaryScale,
                Math.Max(0, slots.State.Width));
            if (!string.IsNullOrEmpty(primary))
                DrawText(frame, slots.State.X, TextCenterY(slots.State, primaryScale), primary, SecondaryText, primaryScale, slots.State.Right);
        }

        // Patch/algorithm + optional badges share the right 55% slot. Badges
        // are a trailing detail: if operator/algorithm text overflows, the
        // badges are dropped first, then the patch identifier is trimmed.
        if (slots.Patch.Width > 0)
        {
            int contentRight = slots.Patch.Right;

            // Patch text is left-aligned within the patch slot; badges trail
            // after it (or lead the slot when the patch is empty, e.g. during
            // an instrument-change overlay).
            int badgeWidth = badges.Length > 0 ? BitmapFont.MeasureText(badges, 1) : 0;
            const int badgeGap = 8;
            string compactPatch = CachedHeaderText(
                _headerPatchInputs,
                _headerPatchOutputs,
                _headerPatchWidths,
                panel.Index,
                patch,
                primaryScale,
                Math.Max(0, contentRight - slots.Patch.X - (badgeWidth > 0 ? badgeWidth + badgeGap : 0)));

            if (!string.IsNullOrEmpty(compactPatch))
            {
                DrawText(frame, slots.Patch.X, TextCenterY(slots.Patch, primaryScale),
                    compactPatch, SecondaryText, primaryScale, contentRight);
            }

            if (badgeWidth > 0)
            {
                int badgeX = slots.Patch.X
                    + BitmapFont.MeasureText(compactPatch, primaryScale)
                    + (compactPatch.Length > 0 ? badgeGap : 0);
                if (badgeX + badgeWidth <= contentRight)
                {
                    DrawText(frame, badgeX,
                        header.Y + Math.Max(2, (header.Height - 7) / 2),
                        badges, SecondaryText, 1, contentRight);
                }
            }
        }

        // §13.3: four compact operator bars during the instrument-change overlay.
        if (showOverlay && _instrumentById.TryGetValue(changeNote.InstrumentId, out var def) && def.Operators.Count > 0)
        {
            DrawOperatorBars(frame, header, def, slots.Patch.Right, panel.Index);
        }
    }

    private void DrawPerformanceLaneStatus(Span<byte> frame, PanelData panel, long currentSample)
    {
        PanelHeaderLayout slots = _layout.HeaderSlots(panel.Index);
        if (slots.State.Width <= 0)
            return;

        PreparedNote active = FindActive(panel.Prepared.MainNotes, panel.MainNoteStreamId, currentSample);
        if (active == null && panel.TrackKind == VisualizationTrackKind.FmOperatorGroup)
        {
            int operatorCount = Math.Min(
                panel.Prepared.OperatorNotes.Length, panel.OperatorNoteStreamIds.Length);
            for (int operatorIndex = 0; operatorIndex < operatorCount; operatorIndex++)
            {
                active = FindActive(
                    panel.Prepared.OperatorNotes[operatorIndex],
                    panel.OperatorNoteStreamIds[operatorIndex],
                    currentSample);
                if (active != null)
                    break;
            }
        }

        string state = active is not null
            ? FormatPitchWithCents(PitchContour.PitchAtSample(active, currentSample, _samplesPerFrame))
            : panel.TrackKind == VisualizationTrackKind.Noise
                ? CurrentPerformanceNoiseLabel(panel, currentSample)
                : panel.TrackKind == VisualizationTrackKind.Sample
                    ? CurrentPerformanceSampleLabel(panel, currentSample)
                    : "";
        if (!string.IsNullOrEmpty(state))
        {
            DrawText(
                frame,
                slots.State.X,
                slots.State.Y,
                Ellipsize(state, 1, slots.State.Width),
                active is not null ? BrightText : SecondaryText,
                1,
                slots.State.Right);
        }

        string patch = active?.Text.ShortLabel ?? "";
        if (!string.IsNullOrEmpty(patch))
        {
            DrawText(
                frame,
                slots.Patch.X,
                slots.Patch.Y,
                Ellipsize(patch, 1, slots.Patch.Width),
                TertiaryText,
                1,
                slots.Patch.Right);
        }
    }

    private static string CurrentPerformanceSampleLabel(PanelData panel, long currentSample)
    {
        foreach (DacHitEvent hit in panel.Prepared.DacHits)
        {
            if (hit.StartSample <= currentSample && currentSample < hit.EndSample)
            {
                string sample = panel.Prepared.SamplesById.TryGetValue(hit.SampleId, out SampleDefinition definition)
                    ? ShortAssetLabel(definition.DisplayName, hit.SampleId)
                    : hit.SampleId;
                return $"~{DacHitLabel(hit.Classification)} {sample}";
            }
        }
        foreach (SamplePlaybackEvent value in panel.Prepared.SamplePlayback)
        {
            if (value.StartSample <= currentSample && currentSample < value.EndSample)
            {
                return ShortAssetLabel(
                    panel.Prepared.SamplesById.TryGetValue(value.SampleId, out SampleDefinition sample)
                        ? sample.DisplayName
                        : null,
                    value.SampleId);
            }
        }
        return panel.Prepared.HasTrackEvents ? "DAC" : "SILENT";
    }

    private static string DacHitLabel(DacHitClass classification)
        => classification switch
        {
            DacHitClass.Kick => "KICK",
            DacHitClass.Snare => "SNARE",
            DacHitClass.Tom => "TOM",
            _ => "HIT",
        };

    private static string CurrentPerformanceNoiseLabel(PanelData panel, long currentSample)
    {
        NoiseStateEvent[] events = panel.Prepared.Noise;
        for (int index = 0; index < events.Length; index++)
        {
            NoiseStateEvent value = events[index];
            if (value.StartSample <= currentSample && currentSample < value.EndSample)
                return index < panel.Prepared.NoiseLabels.Length
                    ? panel.Prepared.NoiseLabels[index]
                    : "NOISE";
        }
        return panel.Prepared.HasTrackEvents ? "NOISE" : "SILENT";
    }

    /// <summary>Centres mono text of a given scale within a header rect.</summary>
    private int TextCenterY(OverlayRect rect, int scale)
        => rect.Y + Math.Max(0, (rect.Height - 7 * scale) / 2);

    private static string CachedHeaderText(
        string[] inputs,
        string[] outputs,
        int[] widths,
        int panelIndex,
        string value,
        int scale,
        int maxWidth)
    {
        if (widths[panelIndex] == maxWidth
            && string.Equals(inputs[panelIndex], value, StringComparison.Ordinal)
            && outputs[panelIndex] is string cached)
        {
            return cached;
        }

        string result = Ellipsize(value, scale, maxWidth);
        inputs[panelIndex] = value;
        outputs[panelIndex] = result;
        widths[panelIndex] = maxWidth;
        return result;
    }

    private static string SsgModeToken(VisualizationNoteMode mode) => mode switch
    {
        VisualizationNoteMode.SsgTone => "T",
        VisualizationNoteMode.SsgToneNoise => "T+N",
        VisualizationNoteMode.SsgNoise => "N",
        VisualizationNoteMode.SsgEnvelopeTone => "ENV-T",
        VisualizationNoteMode.SsgEnvelopeToneNoise => "ENV-T+N",
        VisualizationNoteMode.SsgEnvelopeNoise => "ENV-N",
        _ => "",
    };

}
