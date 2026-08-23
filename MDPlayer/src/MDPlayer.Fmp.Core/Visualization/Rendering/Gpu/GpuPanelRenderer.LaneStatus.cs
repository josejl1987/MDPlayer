using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Dynamic lane identity state. The channel label is part of cached chrome;
/// this partial only paints the bounded current-note/sample badge.
/// </summary>
internal sealed partial class GpuPanelRenderer
{
    private const string BadgeSeparator = "·";
    private const float BadgeRadius = 3f;
    private const float BadgeTextSize = 9f;
    private const int BadgeHeight = 16;

    private void DrawLaneStatuses(long currentSample)
    {
        if (!_layout.HasRoll)
            return;

        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            CurrentLaneState state = _laneStateResolver.Resolve(
                panelIndex,
                currentSample,
                _samplesPerFrame);
            if (!state.HasLabel)
                continue;

            OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
            int gutter = Math.Min(_layout.PitchLabelWidth, timeline.Width);
            int left = timeline.X + _layout.PitchLabelInsetLeft;
            int right = timeline.X + gutter - _layout.PitchLabelInsetRight;
            int badgeY = timeline.Y + 16;
            if (right <= left || badgeY + BadgeHeight > timeline.Bottom)
                continue;

            DrawCurrentBadge(
                new OverlayRect(left, badgeY, right - left, BadgeHeight),
                state.Note1 is PreparedNote note ? note.Accent : _panels[panelIndex].Accent,
                state);
        }
    }

    private void DrawCurrentBadge(OverlayRect badge, OverlayColor accent, CurrentLaneState state)
    {
        if (badge.Width < 12 || badge.Height < 8)
            return;

        SKRect rect = new(badge.X, badge.Y, badge.Right, badge.Bottom);
        DrawRoundRect(rect, accent.WithAlpha(76), SKPaintStyle.Fill, 0);
        DrawRoundRect(rect, accent.Lighten(0.36).WithAlpha(220), SKPaintStyle.Stroke, 1f);

        Canvas.Save();
        Canvas.ClipRect(rect);
        SetFontSize(BadgeTextSize);
        float x = badge.X + 5;
        float maxX = badge.Right - 4;
        float textY = badge.Y + 2;
        DrawBadgeLabel(state.Label1, textY, ref x, maxX, BrightText);
        if (!string.IsNullOrEmpty(state.Label2))
        {
            DrawBadgeSeparator(textY, ref x, maxX);
            DrawBadgeLabel(state.Label2, textY, ref x, maxX, BrightText);
        }
        if (state.AdditionalNotes > 0)
        {
            DrawBadgeSeparator(textY, ref x, maxX);
            DrawBadgeLabel(state.AdditionalLabel, textY, ref x, maxX, SecondaryTextColor);
        }
        Canvas.Restore();
    }

    private void DrawBadgeSeparator(float textY, ref float x, float maxX)
    {
        float width = _font.MeasureText(BadgeSeparator);
        if (x + width > maxX)
            return;
        DrawTextAt(BadgeSeparator, x, textY, BadgeTextSize, SecondaryTextColor);
        x += width + 4;
    }

    private void DrawBadgeLabel(string label, float textY, ref float x, float maxX, OverlayColor color)
    {
        if (string.IsNullOrEmpty(label))
            return;
        float width = _font.MeasureText(label);
        if (x + width > maxX)
            return;
        DrawTextAt(label, x, textY, BadgeTextSize, color);
        x += width + 2;
    }

    private void DrawRoundRect(SKRect rect, OverlayColor color, SKPaintStyle style, float strokeWidth)
    {
        SKPaintStyle previousStyle = _fillPaint.Style;
        float previousWidth = _fillPaint.StrokeWidth;
        SKColor previousColor = _fillPaint.Color;

        _fillPaint.Style = style;
        _fillPaint.StrokeWidth = strokeWidth;
        _fillPaint.Color = ToSk(color);
        Canvas.DrawRoundRect(rect, BadgeRadius, BadgeRadius, _fillPaint);

        _fillPaint.Color = previousColor;
        _fillPaint.Style = previousStyle;
        _fillPaint.StrokeWidth = previousWidth;
    }
}
