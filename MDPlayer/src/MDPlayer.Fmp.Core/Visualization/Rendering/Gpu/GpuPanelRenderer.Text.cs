using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Text drawing for the GPU renderer. The CPU bitmap font is replaced by
/// Skia's font rasterizer (an accepted regression); every string the CPU
/// renders is still drawn here, in the same slots, with Skia-based
/// measurement for right-alignment and ellipsis truncation.
/// </summary>
internal sealed partial class GpuPanelRenderer
{
    /// <summary>Approximate bitmap-font scale-1 glyph (7px) lifted for AA text.</summary>
    private const float TextSizeScale1 = 9f;
    private const float TextSizeScale2 = 14f;
    private const float TextSizeScale3 = 21f;

    /// <summary>Int glyph height used for vertical centering at scale 1.</summary>
    private const int TextSizeScale1Glyph = 9;

    private OverlayColor PrimaryTextColor => _primaryText;
    private OverlayColor SecondaryTextColor => _secondaryText;
    private OverlayColor TertiaryTextColor => _tertiaryText;

    private void DrawClock(long currentSample)
    {
        OverlayRect bar = _layout.TopBarRect;
        string clock = FormatClock(currentSample);
        SetFontSize(TextSizeScale2);
        float clockWidth = _font.MeasureText(clock);
        float clockX = bar.Right - _layout.SafeHorizontalMargin - clockWidth;
        int clockY = bar.Y + Math.Max(2, (bar.Height - 14) / 2);
        DrawTextAt(clock, clockX, clockY, TextSizeScale2, PrimaryTextColor);
    }

    private void DrawLoopLabel(long frameIndex)
    {
        if ((ulong)frameIndex >= (ulong)_loopLabelByFrame.Length)
            return;
        string label = _loopLabelByFrame[frameIndex];
        if (string.IsNullOrEmpty(label))
            return;

        OverlayRect bar = _layout.TopBarRect;
        long frameSample = Math.Min(
            _timeline.EndSample,
            _timeline.StartSample + FrameSampleClock.SampleAtFrame(
                frameIndex, _timeline.SampleRate, FpsNumerator, FpsDenominator));
        SetFontSize(TextSizeScale2);
        float clockWidth = _font.MeasureText(FormatClock(frameSample));
        SetFontSize(TextSizeScale1);
        float labelWidth = _font.MeasureText(label);
        float clockX = bar.Right - _layout.SafeHorizontalMargin - clockWidth;
        DrawTextAt(
            label,
            clockX - labelWidth - 8,
            bar.Y + Math.Max(2, (bar.Height - 7) / 2),
            TextSizeScale1,
            MutedText);
    }

    private void DrawProgress(long currentSample)
    {
        OverlayRect bar = _layout.BottomBarRect;

        // Progress track: a 4px strip at the top edge of the bottom bar.
        const int progressHeight = 4;
        FillRect(new OverlayRect(bar.X, bar.Y, bar.Width, progressHeight), new OverlayColor(33, 36, 49));
        double progress = _timeline.EndSample <= _timeline.StartSample
            ? 0
            : (currentSample - _timeline.StartSample) / (double)(_timeline.EndSample - _timeline.StartSample);
        progress = Math.Clamp(progress, 0, 1);
        double progressRight = bar.X + progress * bar.Width;
        if (progressRight > bar.X)
            FillRect(
                new OverlayRect(bar.X, bar.Y, Math.Max(1, (int)Math.Round(progressRight - bar.X)), progressHeight),
                new OverlayColor(122, 164, 255));
    }

    private string FormatClock(long currentSample)
    {
        long second = Math.Clamp(
            (currentSample - _timeline.StartSample) / _timeline.SampleRate,
            0,
            _clockBySecond.Length - 1);
        return _clockBySecond[second];
    }

    /// <summary>Width the clock occupies in the top bar (used to limit the title).</summary>
    private float ClockReservedWidth()
    {
        SetFontSize(TextSizeScale2);
        return _font.MeasureText("00:00 / " + _totalClockString);
    }

    private void SetFontSize(float size)
    {
        if (_font.Size != size)
            _font.Size = size;
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its glyph box top at
    /// (<paramref name="x"/>, <paramref name="glyphTopY"/>). The y argument is
    /// the line TOP (matching the CPU bitmap-font contract); the Skia baseline
    /// is derived from the font size.
    /// </summary>
    private void DrawTextAt(string text, float x, float y, float fontSize, OverlayColor color)
    {
        if (string.IsNullOrEmpty(text))
            return;
        SetFontSize(fontSize);
        _textPaint.Color = ToSk(color);
        Canvas.DrawText(text, x, y + fontSize * 0.82f, SKTextAlign.Left, _font, _textPaint);
    }

    /// <summary>Draws text truncated with "..." so it never exceeds <paramref name="maxX"/>.</summary>
    private void DrawTextWithLimit(string text, int x, int y, float fontSize, OverlayColor color, float maxX)
    {
        if (string.IsNullOrEmpty(text) || maxX <= x)
            return;
        SetFontSize(fontSize);
        _textPaint.Color = ToSk(color);
        float width = _font.MeasureText(text);
        if (x + width <= maxX)
        {
            Canvas.DrawText(text, x, y + fontSize * 0.82f, SKTextAlign.Left, _font, _textPaint);
            return;
        }
        Canvas.DrawText(FitText(text, maxX - x), x, y + fontSize * 0.82f, SKTextAlign.Left, _font, _textPaint);
    }

    /// <summary>Truncates <paramref name="text"/> (appending "...") to fit <paramref name="maxWidth"/>.</summary>
    private string FitText(string text, float maxWidth)
    {
        if (maxWidth <= 0 || string.IsNullOrEmpty(text))
            return "";
        string ellipsis = "...";
        float ellipsisWidth = _font.MeasureText(ellipsis);
        float available = maxWidth - ellipsisWidth;
        if (available <= 0)
            return "";
        int maxChars = text.Length;
        while (maxChars > 0 && _font.MeasureText(text[..maxChars]) > available)
            maxChars--;
        if (maxChars <= 0)
            return "";
        return maxChars >= text.Length ? text : text[..maxChars] + ellipsis;
    }
}