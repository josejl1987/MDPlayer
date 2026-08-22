using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Equivalence gates for the vectorized row helpers and the scope-blend fast
/// paths: the fast paths must produce byte-identical output to the scalar
/// reference equations for every input class they claim to handle.
/// </summary>
public sealed class SimdRowOpsTests
{
    [Fact]
    public void AllPixelsEqual_MatchesNaiveScan()
    {
        var rng = new Random(1234);
        for (int trial = 0; trial < 200; trial++)
        {
            int pixels = rng.Next(1, 300);
            byte[] buffer = new byte[pixels * 4];
            rng.NextBytes(buffer);
            bool makeUniform = rng.Next(2) == 0;
            byte r = (byte)rng.Next(256), g = (byte)rng.Next(256), b = (byte)rng.Next(256), a = (byte)rng.Next(256);
            if (makeUniform)
                for (int i = 0; i < pixels; i++)
                {
                    buffer[i * 4] = r; buffer[i * 4 + 1] = g; buffer[i * 4 + 2] = b; buffer[i * 4 + 3] = a;
                }
            else if (rng.Next(2) == 0)
            {
                // Corrupt exactly one channel of one pixel.
                int px = rng.Next(pixels);
                buffer[px * 4 + rng.Next(4)] ^= 0xFF;
            }

            bool expected = true;
            for (int i = 0; i < pixels && expected; i++)
                expected = buffer[i * 4] == r && buffer[i * 4 + 1] == g
                    && buffer[i * 4 + 2] == b && buffer[i * 4 + 3] == a;

            Assert.Equal(expected, SimdRowOps.AllPixelsEqual(buffer, r, g, b, a));
        }
    }

    [Fact]
    public void BlendOverUniform_MatchesPerPixelEquation()
    {
        var rng = new Random(99);
        for (int trial = 0; trial < 5000; trial++)
        {
            var src = new OverlayColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            var bg = new OverlayColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
            var resolved = SimdRowOps.BlendOverUniform(src, bg);
            // Per-pixel reference over every identical background pixel.
            byte[] dst = new byte[12];
            for (int i = 0; i < 3; i++)
            {
                dst[i * 4] = bg.R; dst[i * 4 + 1] = bg.G; dst[i * 4 + 2] = bg.B; dst[i * 4 + 3] = 255;
            }
            int a = src.A, inv = 255 - a;
            byte er = (byte)((src.R * a + bg.R * inv + 127) / 255);
            byte eg = (byte)((src.G * a + bg.G * inv + 127) / 255);
            byte eb = (byte)((src.B * a + bg.B * inv + 127) / 255);
            Assert.Equal(er, resolved.R);
            Assert.Equal(eg, resolved.G);
            Assert.Equal(eb, resolved.B);
            Assert.Equal((byte)255, resolved.A);
        }
    }

    [Fact]
    public void FillRect_Translucent_OverUniformAndMixed_DestMatchesReference()
    {
        var timeline = Fixtures.VisualizationTimelineFixture.Create();
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options { FpsNumerator = 30 });

        int w = renderer.Width, h = renderer.Height;
        var rng = new Random(7);
        var frame = new byte[renderer.FrameByteCount];
        rng.NextBytes(frame);
        for (int i = 3; i < frame.Length; i += 4) frame[i] = 255; // opaque canvas

        var color = new OverlayColor(250, 120, 40, 90);
        foreach (var (left, top, right, bottom) in new[]
                 {
                     (10, 10, 500, 60),      // large rect
                     (10, 10, 14, 14),       // tiny
                     (0, 0, w, h),           // whole frame
                     (5, 5, 400, 6),         // single row
                     (w - 50, h - 20, w, h), // corner
                 })
        {
            byte[] work = (byte[])frame.Clone();
            renderer.RenderFillRectForTest(work, left, top, right - left, bottom - top, color);

            // Scalar reference on a clone of the ORIGINAL buffer.
            byte[] reference = (byte[])frame.Clone();
            int alpha = color.A, inv = 255 - alpha;
            for (int y = top; y < bottom; y++)
            {
                int offset = y * w * 4 + left * 4;
                for (int x = left; x < right; x++, offset += 4)
                {
                    reference[offset] = (byte)((color.R * alpha + reference[offset] * inv + 127) / 255);
                    reference[offset + 1] = (byte)((color.G * alpha + reference[offset + 1] * inv + 127) / 255);
                    reference[offset + 2] = (byte)((color.B * alpha + reference[offset + 2] * inv + 127) / 255);
                    reference[offset + 3] = 255;
                }
            }
            Assert.True(work.AsSpan().SequenceEqual(reference),
                $"FillRect mismatch for rect ({left},{top},{right},{bottom})");
        }
    }
}
