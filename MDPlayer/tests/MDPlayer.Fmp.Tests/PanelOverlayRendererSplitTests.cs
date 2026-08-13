using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using System.Security.Cryptography;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Verifies the static/dynamic/composite frame split of the panel overlay
/// renderer: static chrome lives in the exported still, dynamic content is
/// drawn per frame on a transparent base, and the composite path places the
/// Corrscope scope grid into the transparent scope holes.
/// </summary>
public sealed class PanelOverlayRendererSplitTests
{
    private static PanelOverlayRenderer CreateRenderer(double scopeOpacity = 1.0)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        return new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 30,
                FpsDenominator = 1,
                ScopeOpacity = scopeOpacity,
            });
    }

    [Fact]
    public void StaticFrame_HasOpaqueChromeAndTransparentScopeHoles()
    {
        var renderer = CreateRenderer();
        var staticFrame = new byte[renderer.FrameByteCount];
        renderer.WriteStaticFrame(new MemoryStream(staticFrame));

        OverlayLayout layout = renderer.Layout;

        // A pixel in the top metadata bar is opaque chrome.
        int topBarOffset = (layout.TopBarHeight / 2) * renderer.Width * 4 + 100 * 4 + 3;
        Assert.Equal(255, staticFrame[topBarOffset]);

        // A pixel in the scope region is fully transparent (alpha 0).
        int scopeY = layout.GetScopeRowDestinationY(1) + layout.ScopeHeight / 2;
        int scopeOffset = scopeY * renderer.Width * 4 + 100 * 4 + 3;
        Assert.Equal(0, staticFrame[scopeOffset]);

        // The pitch gutter (left of the scope) is opaque chrome inside the panel.
        OverlayRect timeline = layout.GetTimelineRect(0);
        int gutterOffset = (timeline.Y + 4) * renderer.Width * 4 + (timeline.X + 5) * 4 + 3;
        Assert.Equal(255, staticFrame[gutterOffset]);
    }

    [Fact]
    public void DynamicFrame_IsTransparentEverywhereExceptDrawnContent()
    {
        var renderer = CreateRenderer();
        var frame = new byte[renderer.FrameByteCount];
        renderer.RenderDynamicFrame(0, frame);

        // The scope region is a transparent hole in the dynamic frame — the
        // scope rows come from the separate scope layer. Reference chrome (the
        // pitch grid, time lines, playhead) draws over the integrated body, so
        // assert the hole exists rather than a single reference-free pixel.
        bool transparentScopePixel = false;
        for (int panel = 0; panel < renderer.Layout.PanelCount && !transparentScopePixel; panel++)
        {
            OverlayRect scope = renderer.Layout.GetScopeRect(panel);
            for (int y = scope.Y; y < scope.Bottom && !transparentScopePixel; y++)
            for (int x = scope.X + 4; x < scope.Right - 4 && !transparentScopePixel; x++)
            {
                int offset = (y * renderer.Width + x) * 4 + 3;
                transparentScopePixel = frame[offset] == 0;
            }
        }
        Assert.True(transparentScopePixel, "the scope hole must contain transparent pixels");

        // The clock is drawn in the top bar, so some pixel in that row is opaque.
        bool clockPixels = false;
        for (int x = 0; x < renderer.Width; x++)
        {
            int offset = (renderer.Layout.TopBarHeight / 2) * renderer.Width * 4 + x * 4 + 3;
            if (frame[offset] != 0)
            {
                clockPixels = true;
                break;
            }
        }
        Assert.True(clockPixels, "clock text should produce opaque pixels in the top bar");
    }

    [Fact]
    public void CompositeFrame_PlacesScopeRowsAtFullOpacity()
    {
        var renderer = CreateRenderer();
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        // Fill the grid with opaque RGB plus a distinctive marker so placement
        // can be verified. Alpha 255 makes the pixels visible at ScopeOpacity 1.
        for (int i = 0; i < gridBytes; i += 4)
        {
            grid[i] = 10;      // R
            grid[i + 1] = 20;  // G
            grid[i + 2] = 30;  // B
            grid[i + 3] = 255; // A
        }

        var frame = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, grid, frame);

        // Scope pixels are present with baked opaque alpha (the encode drops
        // alpha, so the blend always writes 255 into the destination).
        int scopeY = renderer.Layout.GetScopeRowDestinationY(0) + 2;
        int scopeOffset = scopeY * renderer.Width * 4 + 100 * 4;
        Assert.Equal(10, frame[scopeOffset]);
        Assert.Equal(20, frame[scopeOffset + 1]);
        Assert.Equal(30, frame[scopeOffset + 2]);
        Assert.Equal(255, frame[scopeOffset + 3]);

        // Content outside the scope holes is unaffected chrome (not the scope
        // color).
        int topBarOffset = (renderer.Layout.TopBarHeight / 2) * renderer.Width * 4 + 100 * 4;
        Assert.NotEqual(10, frame[topBarOffset]);
    }

    [Fact]
    public void CompositeFrame_TransparentScopePixels_ContributeNothing()
    {
        // Alpha-0 grid pixels are pure mask background: their RGB must not
        // influence the composite at all. Two grids with identical zero alpha
        // but different RGB must produce byte-identical frames (the alpha
        // channel gets baked to 255, RGB is untouched by the blend).
        var renderer = CreateRenderer();
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var gridA = new byte[gridBytes];
        var gridB = new byte[gridBytes];
        for (int i = 0; i < gridBytes; i += 4)
        {
            gridA[i] = 10;
            gridA[i + 1] = 20;
            gridA[i + 2] = 30;
            gridA[i + 3] = 0;
            gridB[i] = 200;
            gridB[i + 1] = 160;
            gridB[i + 2] = 90;
            gridB[i + 3] = 0;
        }

        var withoutScope = new byte[renderer.FrameByteCount];
        var withGridA = new byte[renderer.FrameByteCount];
        var withGridB = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, ReadOnlySpan<byte>.Empty, withoutScope);
        renderer.RenderCompositeFrame(0, gridA, withGridA);
        renderer.RenderCompositeFrame(0, gridB, withGridB);

        Assert.Equal(SHA256.HashData(withGridA), SHA256.HashData(withGridB));
        // At a pixel no dynamic content touches, the alpha-0 background leaves
        // the static RGB alone but the blend bakes alpha to 255.
        int scopePixel = FindUntouchedScopePixel(renderer, withoutScope);
        Assert.True(scopePixel >= 0, "expected an untouched pixel in the scope region");
        Assert.Equal(0, withGridA[scopePixel]);
        Assert.Equal(0, withGridA[scopePixel + 1]);
        Assert.Equal(0, withGridA[scopePixel + 2]);
        Assert.Equal(255, withGridA[scopePixel + 3]);
    }

    [Fact]
    public void CompositeFrame_Blend_OpaqueSourceAtHalfOpacity_MatchesSrcHalfPlusDstHalf()
    {
        // Acceptance AC-A1: at ScopeOpacity 0.5 with an opaque source pixel,
        // dst.RGB = src * 0.5 + dst * 0.5 per channel; alpha stays 255.
        var renderer = CreateRenderer(scopeOpacity: 0.5);
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        for (int i = 0; i < gridBytes; i += 4)
        {
            grid[i] = 100;
            grid[i + 1] = 150;
            grid[i + 2] = 200;
            grid[i + 3] = 255;
        }

        var reference = new byte[renderer.FrameByteCount];
        var blended = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, ReadOnlySpan<byte>.Empty, reference);
        renderer.RenderCompositeFrame(0, grid, blended);

        // Sample a scope pixel that is untouched by playhead/dynamic content
        // (the empty-grid reference equals the raw static frame there), so the
        // pre-blend destination is exactly what the blend saw.
        int scopeOffset = FindUntouchedScopePixel(renderer, reference);
        Assert.True(scopeOffset >= 0, "expected an untouched pixel in the scope region");

        for (int channel = 0; channel < 3; channel++)
        {
            double expected = grid[scopeOffset + channel] * 0.5
                + reference[scopeOffset + channel] * 0.5;
            Assert.InRange(
                blended[scopeOffset + channel],
                (int)Math.Floor(expected - 1),
                (int)Math.Ceiling(expected + 1));
        }
        Assert.Equal(255, blended[scopeOffset + 3]);
    }

    [Fact]
    public void CompositeFrame_Blend_UsesSourceAlphaAsMask()
    {
        // A half-alpha source pixel at ScopeOpacity 1.0 contributes exactly
        // srcAlpha/255 of its RGB (plan §4.3: a = (srcAlpha / 255) * opacity).
        var renderer = CreateRenderer();
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        for (int i = 0; i < gridBytes; i += 4)
        {
            grid[i] = 255;
            grid[i + 1] = 128;
            grid[i + 2] = 64;
            grid[i + 3] = 128; // half mask
        }

        var reference = new byte[renderer.FrameByteCount];
        var blended = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, ReadOnlySpan<byte>.Empty, reference);
        renderer.RenderCompositeFrame(0, grid, blended);

        int scopeOffset = FindUntouchedScopePixel(renderer, reference);
        Assert.True(scopeOffset >= 0, "expected an untouched pixel in the scope region");

        for (int channel = 0; channel < 3; channel++)
        {
            double expected = grid[scopeOffset + channel] * (128.0 / 255.0)
                + reference[scopeOffset + channel] * (1.0 - 128.0 / 255.0);
            Assert.InRange(
                blended[scopeOffset + channel],
                (int)Math.Floor(expected - 1),
                (int)Math.Ceiling(expected + 1));
        }
        Assert.Equal(255, blended[scopeOffset + 3]);
    }

    [Fact]
    public void MotionBlur_BlendPathAtFullOpacity_IsByteIdenticalToFastPath()
    {
        // Regression: with an opaque source and ScopeOpacity 1.0 the alpha
        // blend must equal the raw-copy fast path pixel-for-pixel, including
        // the motion-blur averaging path (plan §4.6).
        var timeline = VisualizationTimelineFixture.Create();
        using var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 30,
                FpsDenominator = 1,
                MotionBlurSamples = 3,
            });
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        for (int index = 0; index < grid.Length; index += 4)
        {
            grid[index] = (byte)(index / 4 % 251);
            grid[index + 1] = 17;
            grid[index + 2] = 83;
            grid[index + 3] = 255;
        }

        var blended = new byte[renderer.FrameByteCount];
        var fastPath = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(40, grid, blended);
        renderer.RenderCompositeFrame(40, grid, fastPath, scopeFramesAreOpaque: true);

        Assert.Equal(SHA256.HashData(blended), SHA256.HashData(fastPath));
    }

    /// <summary>
    /// Finds a pixel inside the first panel's scope region that the playhead
    /// and the dynamic pass leave untouched (the reference composite equals
    /// the raw static frame there), returning its byte offset or -1.
    /// </summary>
    private static int FindUntouchedScopePixel(PanelOverlayRenderer renderer, byte[] reference)
    {
        var staticFrame = new byte[renderer.FrameByteCount];
        renderer.WriteStaticFrame(new MemoryStream(staticFrame));
        OverlayLayout layout = renderer.Layout;
        for (int panel = 0; panel < layout.PanelCount; panel++)
        {
            OverlayRect scope = layout.GetScopeRect(panel);
            int playheadX = layout.GetPlayheadX(panel);
            for (int y = scope.Y + 4; y < scope.Bottom - 4; y++)
            for (int x = scope.X + 8; x < scope.Right - 8; x++)
            {
                if (x == playheadX)
                    continue;
                int offset = (y * renderer.Width + x) * 4;
                if (reference[offset] == staticFrame[offset]
                    && reference[offset + 1] == staticFrame[offset + 1]
                    && reference[offset + 2] == staticFrame[offset + 2]
                    && reference[offset + 3] == staticFrame[offset + 3]
                    && reference[offset + 3] == 0)
                {
                    return offset;
                }
            }
        }
        return -1;
    }

    [Fact]
    public void CompositeFrame_OpaqueScopeFastPathMatchesAlphaNormalization()
    {
        var renderer = CreateRenderer();
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        for (int index = 0; index < grid.Length; index += 4)
        {
            grid[index] = (byte)(index / 4 % 251);
            grid[index + 1] = 17;
            grid[index + 2] = 83;
            grid[index + 3] = 255;
        }

        var normalized = new byte[renderer.FrameByteCount];
        var opaqueFastPath = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, grid, normalized);
        renderer.RenderCompositeFrame(0, grid, opaqueFastPath, scopeFramesAreOpaque: true);

        Assert.Equal(SHA256.HashData(normalized), SHA256.HashData(opaqueFastPath));
    }

    [Fact]
    public void CompositeFrame_RejectsUndersizedScopeGrid()
    {
        var renderer = CreateRenderer();
        var frame = new byte[renderer.FrameByteCount];
        var tinyGrid = new byte[16];

        Assert.Throws<ArgumentException>(
            () => renderer.RenderCompositeFrame(0, tinyGrid, frame));
    }

    [Fact]
    public void SequentialSession_MatchesRandomAccessCompositeFrames()
    {
        var renderer = CreateRenderer();
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        for (int index = 0; index < grid.Length; index += 4)
        {
            grid[index] = (byte)(index / 4 % 251);
            grid[index + 1] = 17;
            grid[index + 2] = 83;
        }

        var session = renderer.CreateSequentialSession();
        var sequential = new byte[renderer.FrameByteCount];
        session.Initialize(sequential);
        var random = new byte[renderer.FrameByteCount];

        foreach (long frameIndex in new[] { 0L, 1L, 17L, 89L })
        {
            session.RenderNext(frameIndex, grid, sequential);
            renderer.RenderCompositeFrame(frameIndex, grid, random);
            Assert.Equal(SHA256.HashData(random), SHA256.HashData(sequential));
        }
    }
}
