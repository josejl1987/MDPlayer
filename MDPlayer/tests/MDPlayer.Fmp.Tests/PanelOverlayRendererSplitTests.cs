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
    private static PanelOverlayRenderer CreateRenderer()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        return new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 30,
                FpsDenominator = 1,
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

        // A pixel in a panel timeline region is opaque chrome.
        int timelineY = layout.GetTimelineRect(0).Y + 4;
        int timelineOffset = timelineY * renderer.Width * 4 + 100 * 4 + 3;
        Assert.Equal(255, staticFrame[timelineOffset]);
    }

    [Fact]
    public void DynamicFrame_IsTransparentEverywhereExceptDrawnContent()
    {
        var renderer = CreateRenderer();
        var frame = new byte[renderer.FrameByteCount];
        renderer.RenderDynamicFrame(0, frame);

        // The scope regions must stay transparent in the dynamic frame — the
        // scope rows come from the separate scope layer.
        int scopeY = renderer.Layout.GetScopeRowDestinationY(1) + renderer.Layout.ScopeHeight / 2;
        int scopeOffset = scopeY * renderer.Width * 4 + 100 * 4 + 3;
        Assert.Equal(0, frame[scopeOffset]);

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
    public void CompositeFrame_PlacesScopeRowsWithOpaqueAlpha()
    {
        var renderer = CreateRenderer();
        int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
        var grid = new byte[gridBytes];
        // Fill the grid with opaque-ish RGB (alpha byte stays 0 like Corrscope's
        // rgb0 frames) plus a distinctive marker so placement can be verified.
        for (int i = 0; i < gridBytes; i += 4)
        {
            grid[i] = 10;      // R
            grid[i + 1] = 20;  // G
            grid[i + 2] = 30;  // B
            grid[i + 3] = 0;   // A (rgb0)
        }

        var frame = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, grid, frame);

        // Scope pixels are present and forced opaque (alpha 255).
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
