namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Shared CPU-preview lane status presentation. Static channel identity is
/// placed in the cached gutter; only the bounded current state is redrawn.
/// </summary>
internal sealed partial class PanelOverlayRenderer
{
    private const string LaneBadgeSeparator = "·";
    private const int LaneBadgeHeight = 16;

    private void DrawStaticLaneIdentity(Span<byte> frame, PanelData panel, OverlayRect timeline)
    {
        if (!_layout.HasRoll || timeline.Height <= 0)
            return;

        int gutter = Math.Min(_layout.PitchLabelWidth, timeline.Width);
        if (gutter <= 0)
            return;

        DrawVerticalLine(
            frame,
            timeline.X + gutter - 1,
            timeline.Y,
            timeline.Bottom - 1,
            Border.WithAlpha(170));

        int left = timeline.X + _layout.PitchLabelInsetLeft;
        int right = timeline.X + gutter - _layout.PitchLabelInsetRight;
        if (right <= left)
            return;

        DrawText(
            frame,
            left,
            timeline.Y + 2,
            Ellipsize(panel.Label, 1, right - left),
            panel.Prepared.Accent.Lighten(0.12).WithAlpha(225),
            1,
            right);
    }

    private void DrawLaneStatuses(Span<byte> frame, long currentSample)
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
            if (right <= left || badgeY + LaneBadgeHeight > timeline.Bottom)
                continue;

            DrawLaneBadge(
                frame,
                new OverlayRect(left, badgeY, right - left, LaneBadgeHeight),
                state.Note1 is PreparedNote note ? note.Accent : _panels[panelIndex].Prepared.Accent,
                state);
        }
    }

    private void DrawLaneBadge(
        Span<byte> frame,
        OverlayRect badge,
        OverlayColor accent,
        CurrentLaneState state)
    {
        if (badge.Width < 12 || badge.Height < 8)
            return;

        FillRect(frame, badge, accent.WithAlpha(76));
        StrokeRect(frame, badge, accent.Lighten(0.36).WithAlpha(220), 1);

        int x = badge.X + 5;
        int maxX = badge.Right - 4;
        DrawLaneBadgeLabel(frame, state.Label1, badge.Y + 2, ref x, maxX, BrightText);
        if (!string.IsNullOrEmpty(state.Label2))
        {
            DrawLaneBadgeSeparator(frame, badge.Y + 2, ref x, maxX);
            DrawLaneBadgeLabel(frame, state.Label2, badge.Y + 2, ref x, maxX, BrightText);
        }
        if (state.AdditionalNotes > 0)
        {
            DrawLaneBadgeSeparator(frame, badge.Y + 2, ref x, maxX);
            DrawLaneBadgeLabel(frame, state.AdditionalLabel, badge.Y + 2, ref x, maxX, SecondaryText);
        }
    }

    private void DrawLaneBadgeSeparator(
        Span<byte> frame,
        int y,
        ref int x,
        int maxX)
    {
        int width = BitmapFont.MeasureText(LaneBadgeSeparator, 1);
        if (x + width > maxX)
            return;
        DrawText(frame, x, y, LaneBadgeSeparator, SecondaryText, 1, maxX);
        x += width + 4;
    }

    private void DrawLaneBadgeLabel(
        Span<byte> frame,
        string label,
        int y,
        ref int x,
        int maxX,
        OverlayColor color)
    {
        if (string.IsNullOrEmpty(label))
            return;
        int width = BitmapFont.MeasureText(label, 1);
        if (x + width > maxX)
            return;
        DrawText(frame, x, y, label, color, 1, maxX);
        x += width + 2;
    }
}
