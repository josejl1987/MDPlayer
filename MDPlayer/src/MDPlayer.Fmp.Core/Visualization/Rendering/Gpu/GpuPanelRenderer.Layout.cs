using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Static-per-frame chrome for the GPU renderer: metadata bars, panel bodies,
/// headers, pitch/rhythm grids and operator ribbons. The GPU path redraws all
/// of it every frame (no static-image caching), so "chrome" here means
/// time-invariant content — the same pixels the CPU cached as its static layer.
/// </summary>
internal sealed partial class GpuPanelRenderer
{
    private void DrawChrome()
    {
        DrawMetadataBars();
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
            DrawPanelChrome(panelIndex);
        DrawPanelSeparators();
    }

    private void DrawMetadataBars()
    {
        OverlayRect topBar = _layout.TopBarRect;
        OverlayRect bottomBar = _layout.BottomBarRect;
        FillRect(topBar, HeaderBackground);
        FillRect(bottomBar, HeaderBackground);

        // Title / subtitle ride the top bar; credits ride the bottom bar
        // (static text, drawn every frame like the rest of the chrome).
        int safeX = topBar.X + _layout.SafeHorizontalMargin;
        int titleMaxX = topBar.Right - _layout.SafeHorizontalMargin - (int)ClockReservedWidth() - 40;
        if (titleMaxX > safeX)
        {
            if (!string.IsNullOrEmpty(_presentation.Title))
            {
                int titleCenterY = topBar.Y + Math.Max(2, (topBar.Height - 21) / 2);
                DrawTextWithLimit(
                    _presentation.Title,
                    safeX,
                    titleCenterY,
                    TextSizeScale3,
                    PrimaryTextColor,
                    titleMaxX);
            }

            if (!string.IsNullOrEmpty(_presentation.Subtitle))
            {
                int subtitleCenterY = topBar.Y + Math.Max(2, (topBar.Height - 21) / 2) + 24;
                DrawTextWithLimit(
                    _presentation.Subtitle,
                    safeX,
                    subtitleCenterY,
                    TextSizeScale2,
                    SecondaryTextColor,
                    titleMaxX);
            }
        }

        if (!string.IsNullOrEmpty(_presentation.Credits))
        {
            const int progressHeight = 4;
            int creditsY = bottomBar.Y + progressHeight
                + Math.Max(2, (bottomBar.Height - progressHeight - 14) / 2);
            DrawTextWithLimit(
                _presentation.Credits,
                bottomBar.X + _layout.SafeHorizontalMargin,
                creditsY,
                TextSizeScale2,
                MutedText,
                bottomBar.Right - _layout.SafeHorizontalMargin);
        }
    }

    private void DrawPanelChrome(int panelIndex)
    {
        PreparedPanel panel = _panels[panelIndex];
        OverlayRect panelRect = _layout.GetPanelRect(panelIndex);
        FillRect(panelRect, TimelineBackground);

        // Panel border.
        if (panelRect.Width > 2 && panelRect.Height > 2)
        {
            DrawLine(panelRect.X, panelRect.Y, panelRect.Right - 1, panelRect.Y, Border);
            DrawLine(panelRect.X, panelRect.Bottom - 1, panelRect.Right - 1, panelRect.Bottom - 1, Border);
            DrawLine(panelRect.X, panelRect.Y, panelRect.X, panelRect.Bottom - 1, Border);
            DrawLine(panelRect.Right - 1, panelRect.Y, panelRect.Right - 1, panelRect.Bottom - 1, Border);
        }

        // Header chrome: fill, accent bar, channel name.
        OverlayRect header = _layout.GetHeaderRect(panelIndex);
        if (header.Height > 0)
        {
            FillRect(header, HeaderBackground);
            _fillPaint.Color = ToSk(panel.Accent);
            Canvas.DrawRect(new SKRect(header.X, header.Y, header.X + 4, header.Bottom), _fillPaint);
            if (header.Width > 16)
            {
                DrawTextWithLimit(
                    panel.Label,
                    header.X + 10,
                    header.Y + Math.Max(2, (header.Height - 7) / 2),
                    TextSizeScale1,
                    PrimaryTextColor,
                    header.Right - 10);
            }
        }

        // Per-kind chrome inside the panel body.
        switch (panel.Schema)
        {
            case PanelPresentationSchema.PitchedLane:
            case PanelPresentationSchema.WaveTableLane:
                DrawPitchGrid(panelIndex);
                break;
            case PanelPresentationSchema.FmOperatorGroup:
                DrawPitchGrid(panelIndex);
                DrawFm3RibbonChrome(panelIndex);
                break;
            case PanelPresentationSchema.PercussionRows:
                DrawRhythmRowsChrome(panelIndex);
                break;
            case PanelPresentationSchema.SampleLane:
                DrawPcmLanesChrome(panelIndex);
                break;
            default:
                DrawEmptyLaneChrome(panelIndex);
                break;
        }

        DrawLaneIdentity(panelIndex);
    }

    /// <summary>
    /// Pitch gutter + black-key bands + per-midi row lines + C-octave labels,
    /// mirroring the CPU pitch grid grammar (approximated with Skia rects).
    /// </summary>
    private void DrawPitchGrid(int panelIndex)
    {
        PreparedPanel panel = _panels[panelIndex];
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        OverlayRect lane = _layout.GetPitchedLaneRect(
            panelIndex, panel.Schema == PanelPresentationSchema.FmOperatorGroup);
        if (lane.Width <= 0 || lane.Height <= 0)
            return;

        double minMidi = panel.MinMidi;
        double maxMidi = panel.MaxMidi;
        if (!double.IsFinite(minMidi) || !double.IsFinite(maxMidi) || maxMidi <= minMidi)
        {
            minMidi = 48;
            maxMidi = 72;
        }

        // Gutter backdrop first so grid lines and labels sit above it.
        FillRect(new OverlayRect(timeline.X, timeline.Y, Math.Min(_layout.PitchLabelWidth, timeline.Width), timeline.Height), HeaderBackground);

        // Black-key bands.
        for (int midi = (int)Math.Floor(minMidi); midi <= (int)Math.Ceiling(maxMidi); midi++)
        {
            if (!BlackPitchClasses.Contains(Mod(midi, 12)))
                continue;
            int yTop = MidiToY(midi + 0.5, minMidi, maxMidi, lane);
            int yBottom = MidiToY(midi - 0.5, minMidi, maxMidi, lane);
            int top = Math.Min(yTop, yBottom);
            int bottom = Math.Max(yTop, yBottom);
            if (bottom - top >= 1)
                FillRect(new OverlayRect(lane.X, top, lane.Width, Math.Max(1, bottom - top)), BlackKeyBand);
        }

        // Per-midi row lines + C-octave labels in the gutter.
        for (int midi = (int)Math.Floor(minMidi); midi <= (int)Math.Ceiling(maxMidi); midi++)
        {
            int y = MidiToY(midi, minMidi, maxMidi, lane);
            bool octaveRoot = Mod(midi, 12) == 0;
            if (octaveRoot)
            {
                DrawLine(lane.X, y, lane.Right - 1, y, GridLine.WithAlpha(180));
                DrawGutterLabelRightAligned(
                    timeline,
                    $"C{midi / 12 - 1}",
                    y - Math.Max(1, (PitchAxisTextGlyph - 2) / 2));
            }
            else if (y > lane.Y)
            {
                DrawLine(lane.X, y, lane.Right - 1, y, GridLine.WithAlpha(90));
            }
        }
    }

    private void DrawRhythmRowsChrome(int panelIndex)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        int labelWidth = _layout.PitchLabelWidth;
        FillRect(new OverlayRect(timeline.X, timeline.Y, Math.Min(labelWidth, timeline.Width), timeline.Height), HeaderBackground);
        PanelRowDefinition[] rows = _panels[panelIndex].Rows;
        if (rows.Length == 0)
            return;
        int rowHeight = Math.Max(1, timeline.Height / rows.Length);
        for (int row = 0; row < rows.Length; row++)
        {
            int y = timeline.Y + row * rowHeight;
            DrawLine(timeline.X + labelWidth, y, timeline.Right - 1, y, GridLine);
            DrawGutterLabelRightAligned(
                timeline,
                rows[row].Label,
                y + Math.Max(1, (rowHeight - TextSizeScale1Glyph) / 2));
        }
    }

    private void DrawPcmLanesChrome(int panelIndex)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        int labelWidth = _layout.PitchLabelWidth;
        FillRect(new OverlayRect(timeline.X, timeline.Y, Math.Min(labelWidth, timeline.Width), timeline.Height), HeaderBackground);
        // Distinct sample display names, in first-appearance order (row order).
        PreparedPanel panel = _panels[panelIndex];
        var labels = new List<string>();
        foreach (SampleDefinition sample in panel.Samples)
        {
            string label = PresentationMetadata.OptionalLabel(sample.DisplayName);
            if (label is not null && !labels.Contains(label))
                labels.Add(label);
        }
        int laneCount = Math.Max(1, labels.Count);
        int rowHeight = Math.Max(1, timeline.Height / laneCount);
        for (int row = 0; row < labels.Count; row++)
        {
            DrawGutterLabelRightAligned(
                timeline,
                labels[row],
                timeline.Y + row * rowHeight + Math.Max(1, (rowHeight - TextSizeScale1Glyph) / 2));
        }
        foreach (int y in EnumerateDividers(timeline, laneCount))
            DrawLine(timeline.X + labelWidth, y, timeline.Right - 1, y, GridLine);
    }

    private void DrawEmptyLaneChrome(int panelIndex)
    {
        // Aggregates/noise/placeholder panels keep a plain lane; dynamic marks
        // are drawn by the Notes partial.
        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        int gutter = Math.Min(_layout.PitchLabelWidth, timeline.Width);
        FillRect(new OverlayRect(timeline.X, timeline.Y, gutter, timeline.Height), HeaderBackground);
        _ = panelIndex;
    }

    private void DrawFm3RibbonChrome(int panelIndex)
    {
        OverlayRect ribbons = _layout.GetFm3OperatorRect(panelIndex);
        if (ribbons.Height <= 0)
            return;
        FillRect(ribbons, new OverlayColor(12, 14, 21));
        int rowHeight = Math.Max(1, ribbons.Height / 4);
        for (int op = 0; op < 4; op++)
        {
            int y = ribbons.Y + op * rowHeight;
            DrawLine(ribbons.X, y, ribbons.Right - 1, y, GridLine);
            DrawTextWithLimit($"OP{op + 1}", ribbons.X + 2, y + 1, TextSizeScale1, TertiaryTextColor, ribbons.X + 20);
        }
    }

    private void DrawPanelSeparators()
    {
        // Horizontal separators between panel rows (the vertical borders come
        // from the per-panel border drawn above).
        for (int row = 1; row < _layout.RowCount; row++)
        {
            int y = _layout.GridY + row * _layout.PanelHeight;
            DrawLine(0, y - 1, Width - 1, y - 1, Border);
        }
    }

    private void DrawGutterLabelRightAligned(OverlayRect timeline, string label, int y)
    {
        int gutter = _layout.PitchLabelWidth;
        if (gutter <= 0 || string.IsNullOrEmpty(label))
            return;
        int minX = timeline.X + _layout.PitchLabelInsetLeft;
        int maxX = timeline.X + gutter - _layout.PitchLabelInsetRight;
        SetFontSize(PitchAxisTextSize);
        float labelWidth = _font.MeasureText(label);
        float x = maxX - labelWidth;
        if (x < minX)
            x = minX;
        DrawTextAt(label, x, y, PitchAxisTextSize, TertiaryTextColor.WithAlpha(105));
    }

    private void DrawLaneIdentity(int panelIndex)
    {
        if (!_layout.HasRoll)
            return;

        OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
        int gutter = Math.Min(_layout.PitchLabelWidth, timeline.Width);
        if (gutter <= 0 || timeline.Height <= 0)
            return;

        DrawLine(
            timeline.X + gutter - 1,
            timeline.Y,
            timeline.X + gutter - 1,
            timeline.Bottom - 1,
            Border.WithAlpha(170));

        int left = timeline.X + _layout.PitchLabelInsetLeft;
        int right = timeline.X + gutter - _layout.PitchLabelInsetRight;
        if (right <= left)
            return;

        DrawTextWithLimit(
            _panels[panelIndex].Label,
            left,
            timeline.Y + 2,
            TextSizeScale1,
            _panels[panelIndex].Accent.Lighten(0.12).WithAlpha(225),
            right);
    }

    private static IEnumerable<int> EnumerateDividers(OverlayRect timeline, int divisions)
    {
        if (divisions <= 1)
            yield break;
        int rowHeight = Math.Max(1, timeline.Height / divisions);
        for (int row = 1; row < divisions; row++)
            yield return timeline.Y + row * rowHeight;
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    // ------------------------------------------------------------------
    // Raster helpers used by chrome (delegated to Canvas with reusable paint).
    // ------------------------------------------------------------------

    private void FillRect(OverlayRect rect, OverlayColor color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;
        _fillPaint.Color = ToSk(color);
        Canvas.DrawRect(new SKRect(rect.X, rect.Y, rect.Right, rect.Bottom), _fillPaint);
    }

    private void DrawLine(int x1, int y1, int x2, int y2, OverlayColor color)
    {
        _fillPaint.Color = ToSk(color);
        Canvas.DrawLine(x1, y1, x2, y2, _fillPaint);
    }
}
