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
            DrawText(frame, ribbons.X + 2, y + 1, $"OP{op + 1}", MutedText, 1, ribbons.X + 20);
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
            DrawText(frame, timeline.X + 2, y + Math.Max(1, (rowHeight - 7) / 2), rows[row].Label, MutedText, 1, timeline.X + labelWidth - 2);
        }
    }

    private void DrawClock(Span<byte> frame, long currentSample)
    {
        OverlayRect bar = _layout.TopBarRect;
        string clock = FormatClock(currentSample);
        int clockWidth = BitmapFont.MeasureText(clock, 2);
        int clockX = bar.Right - _layout.SafeHorizontalMargin - clockWidth;
        int clockY = bar.Y + Math.Max(2, (bar.Height - 14) / 2);
        DrawText(frame, clockX, clockY, clock, BrightText, 2, bar.Right - _layout.SafeHorizontalMargin);
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
        OverlayRect header = _layout.GetHeaderRect(panel.Index);
        PreparedNote active = FindActive(panel.Prepared.MainNotes, currentSample);
        if (active == null && panel.TrackKind == VisualizationTrackKind.FmOperatorGroup)
        {
            foreach (PreparedNote[] operatorNotes in panel.Prepared.OperatorNotes)
            {
                active = FindActive(operatorNotes, currentSample);
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
                panel.Prepared, currentSample, out PanelHeaderData chipHeader))
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
        DrawHorizontalLine(frame, header.X + 78, header.Right - 5, header.Bottom - 1, headerAccent);

        // Panel headers contain only channel-local information; the global
        // clock now lives in the dedicated top bar.
        // Keep a quiet right gutter so the compact panel hierarchy cannot be
        // mistaken for the global clock area.
        int rightLimit = header.Right - 64;
        int valueLeft = header.X + 82;
        int primaryScale = Height >= 720 ? 2 : 1;
        int primaryHeight = 7 * primaryScale;
        int textY = header.Y + Math.Max(2, (header.Height - primaryHeight) / 2);
        int patchX = rightLimit;
        if (!string.IsNullOrEmpty(patch))
        {
            string compactPatch = Ellipsize(patch, primaryScale, Math.Max(0, header.Width / 3));
            int width = BitmapFont.MeasureText(compactPatch, primaryScale);
            patchX = rightLimit - width;
            if (patchX > valueLeft)
                DrawText(frame, patchX, textY, compactPatch, BrightText, primaryScale, rightLimit);
            else
                patchX = rightLimit;
        }

        int badgeX = patchX;
        if (!string.IsNullOrEmpty(badges))
        {
            int badgeWidth = BitmapFont.MeasureText(badges, 1);
            badgeX = patchX - badgeWidth - 8;
            if (badgeX > valueLeft)
                DrawText(frame, badgeX, header.Y + Math.Max(2, (header.Height - 7) / 2), badges, MutedText, 1, patchX - 4);
            else
                badgeX = patchX;
        }

        if (!string.IsNullOrEmpty(state))
        {
            int stateRight = Math.Max(valueLeft, badgeX - 8);
            string primary = Ellipsize(state, primaryScale, Math.Max(0, stateRight - valueLeft));
            if (!string.IsNullOrEmpty(primary))
                DrawText(frame, valueLeft, textY, primary, BrightText, primaryScale, stateRight);
        }

        // §13.3: four compact operator bars during the instrument-change overlay.
        if (showOverlay && _instrumentById.TryGetValue(changeNote.InstrumentId, out var def) && def.Operators.Count > 0)
            DrawOperatorBars(frame, header, def, rightLimit, panel.Index);
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
