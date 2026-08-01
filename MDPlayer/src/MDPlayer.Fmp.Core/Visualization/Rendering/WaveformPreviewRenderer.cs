namespace Fmp.Core.Visualization.Rendering;

/// <summary>Small allocation-free drawing primitives shared by waveform panels.</summary>
internal static class WaveformPreviewRenderer
{
    public static void DrawPeriodicWaveform(
        Span<byte> frame,
        int canvasWidth,
        OverlayRect viewport,
        IReadOnlyList<float> points,
        OverlayColor color)
    {
        if (points is null || points.Count < 2 || viewport.Width <= 0 || viewport.Height <= 0)
            return;
        int mid = viewport.Y + viewport.Height / 2;
        DrawHorizontal(frame, canvasWidth, viewport.X, viewport.Right - 1, mid, new OverlayColor(90, 96, 118, 150));
        int previousX = viewport.X;
        int previousY = ToY(points[0], viewport);
        for (int index = 1; index < points.Count; index++)
        {
            int x = viewport.X + (int)Math.Round(index * (viewport.Width - 1.0) / (points.Count - 1));
            int y = ToY(points[index], viewport);
            DrawLine(frame, canvasWidth, previousX, previousY, x, y, color);
            previousX = x;
            previousY = y;
        }
    }

    public static void DrawEnvelope(
        Span<byte> frame,
        int canvasWidth,
        OverlayRect viewport,
        IReadOnlyList<WaveformEnvelopePoint> envelope,
        OverlayColor color)
    {
        if (envelope is null || envelope.Count == 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return;
        for (int index = 0; index < envelope.Count; index++)
        {
            int x = viewport.X + (int)Math.Round(index * (viewport.Width - 1.0) / Math.Max(1, envelope.Count - 1));
            int minimum = ToY(envelope[index].Minimum, viewport);
            int maximum = ToY(envelope[index].Maximum, viewport);
            DrawVertical(frame, canvasWidth, x, minimum, maximum, color);
        }
    }

    public static void DrawLoopRegion(
        Span<byte> frame,
        int canvasWidth,
        OverlayRect viewport,
        int? loopStart,
        int? loopEnd,
        int sourceLength,
        OverlayColor color)
    {
        if (sourceLength <= 0 || loopStart is not int start || loopEnd is not int end || end <= start)
            return;
        int left = viewport.X + (int)Math.Round(Math.Clamp(start / (double)sourceLength, 0, 1) * Math.Max(0, viewport.Width - 1));
        int right = viewport.X + (int)Math.Round(Math.Clamp(end / (double)sourceLength, 0, 1) * Math.Max(0, viewport.Width - 1));
        DrawVertical(frame, canvasWidth, left, viewport.Y, viewport.Bottom - 1, color);
        DrawVertical(frame, canvasWidth, right, viewport.Y, viewport.Bottom - 1, color);
    }

    public static void DrawPlaybackCursor(
        Span<byte> frame,
        int canvasWidth,
        OverlayRect viewport,
        double progress,
        OverlayColor color)
    {
        if (viewport.Width <= 0)
            return;
        int x = viewport.X + (int)Math.Round(Math.Clamp(progress, 0, 1) * Math.Max(0, viewport.Width - 1));
        DrawVertical(frame, canvasWidth, x, viewport.Y, viewport.Bottom - 1, color);
    }

    private static int ToY(float value, OverlayRect viewport)
    {
        double normalized = Math.Clamp(value, -1, 1);
        return viewport.Bottom - 1 - (int)Math.Round((normalized + 1) * 0.5 * Math.Max(0, viewport.Height - 1));
    }

    private static void DrawLine(Span<byte> frame, int width, int x0, int y0, int x1, int y1, OverlayColor color)
    {
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            Blend(frame, width, x0, y0, color);
            if (x0 == x1 && y0 == y1)
                break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }

    private static void DrawHorizontal(Span<byte> frame, int width, int left, int right, int y, OverlayColor color)
    {
        for (int x = left; x <= right; x++)
            Blend(frame, width, x, y, color);
    }

    private static void DrawVertical(Span<byte> frame, int width, int x, int top, int bottom, OverlayColor color)
    {
        for (int y = top; y <= bottom; y++)
            Blend(frame, width, x, y, color);
    }

    private static void Blend(Span<byte> frame, int width, int x, int y, OverlayColor source)
    {
        if (x < 0 || y < 0 || x >= width || frame.Length / 4 / width <= y || source.A == 0)
            return;
        int offset = (y * width + x) * 4;
        if (source.A == 255)
        {
            frame[offset] = source.R;
            frame[offset + 1] = source.G;
            frame[offset + 2] = source.B;
            frame[offset + 3] = 255;
            return;
        }
        int inverse = 255 - source.A;
        frame[offset] = (byte)((source.R * source.A + frame[offset] * inverse + 127) / 255);
        frame[offset + 1] = (byte)((source.G * source.A + frame[offset + 1] * inverse + 127) / 255);
        frame[offset + 2] = (byte)((source.B * source.A + frame[offset + 2] * inverse + 127) / 255);
        frame[offset + 3] = (byte)Math.Min(255, source.A + (frame[offset + 3] * inverse + 127) / 255);
    }
}
