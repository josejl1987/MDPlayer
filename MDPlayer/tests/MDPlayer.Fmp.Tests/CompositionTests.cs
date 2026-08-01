using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Geometry and rendering contracts for the three canonical publishing
/// compositions: Performance (unified roll), ScopeStage (scope wall +
/// activity strip) and Diagnostic (existing grid).
/// </summary>
public sealed class CompositionTests
{
    // ------------------------------------------------------------------
    // Performance geometry
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 12)]
    [InlineData(1280, 720, 1)]
    [InlineData(1280, 720, 8)]
    public void Performance_UsesDominantSharedSemanticRegion(int width, int height, int panelCount)
    {
        var layout = new OverlayLayout(
            width, height, 0.75, 2.25, panelCount,
            VisualizationLayoutMode.Performance);

        Assert.True(layout.IsSharedComposition);
        Assert.True(layout.HasRoll);
        Assert.True(layout.HasScopes);

        // The main roll must own at least 60% of the usable grid area.
        double semanticRatio = layout.SharedSemanticRect.Height / (double)layout.GridHeight;
        Assert.True(
            semanticRatio >= 0.60,
            $"Expected semantic region >= 60% of grid, got {semanticRatio:P0} "
            + $"(grid {layout.GridHeight}px, semantic {layout.SharedSemanticRect.Height}px).");
    }

    [Fact]
    public void Performance_ScopeStripIsCompact()
    {
        var layout = new OverlayLayout(
            1920, 1080, 0.75, 2.25, panelCount: 6,
            VisualizationLayoutMode.Performance);

        // ≈170px at 1080p: the strip must stay far below the semantic region.
        Assert.InRange(layout.ScopeHeight, 120, 240);
        Assert.True(layout.ScopeHeight < layout.SharedSemanticRect.Height);
    }

    [Fact]
    public void Performance_CompactLanesStackBelowTheSemanticRegion()
    {
        var layout = new OverlayLayout(
            1920, 1080, 0.75, 2.25, panelCount: 5,
            VisualizationLayoutMode.Performance);

        OverlayRect main = layout.SharedSemanticRect;
        Assert.True(layout.GetPanelRect(0) == main, "panel 0 is the shared semantic region");
        // Compact lanes for the remaining panels sit directly below the main
        // roll inside the same grid band.
        OverlayRect firstLane = layout.GetPanelRect(1);
        Assert.Equal(main.X, firstLane.X);
        Assert.Equal(main.Width, firstLane.Width);
        Assert.Equal(main.Bottom, firstLane.Y);
    }

    // ------------------------------------------------------------------
    // ScopeStage geometry
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1920, 1080, 12)]
    [InlineData(1920, 1080, 8)]
    [InlineData(1280, 720, 6)]
    [InlineData(2560, 1440, 16)]
    public void ScopeStage_ReservesStripAndFillsMosaic(int width, int height, int panelCount)
    {
        var layout = new OverlayLayout(
            width, height, 0.75, 2.25, panelCount,
            VisualizationLayoutMode.ScopeStage);

        Assert.True(layout.IsScopeStage);
        Assert.True(layout.HasScopes);
        Assert.False(layout.HasRoll, "scope wall panels carry no per-panel roll");

        OverlayRect strip = layout.ScopeStageStripRect;
        Assert.True(strip.Height > 0, "ScopeStage always reserves the activity strip");
        Assert.Equal(width, strip.Width);
        // The strip sits between the mosaic and the bottom progress bar.
        Assert.Equal(layout.BottomBarRect.Y, strip.Bottom);

        // Mosaic height equals the scope rows exactly (Corrscope agreement).
        Assert.Equal(layout.GridHeight, layout.CorrscopeGridHeight);
        Assert.Equal(0, layout.GridHeight % layout.RowCount);
        Assert.Equal(layout.ScopeHeight * layout.RowCount, layout.CorrscopeGridHeight);

        // The mosaic owns the large majority of the reserved grid band.
        Assert.True(
            layout.GridHeight >= 0.8 * (layout.GridHeight + strip.Height),
            "ScopeStage mosaic must dominate its grid band.");
    }

    [Fact]
    public void ScopeStage_ScopeCellsCoverTheMosaic()
    {
        var layout = new OverlayLayout(
            1920, 1080, 0.75, 2.25, panelCount: 8,
            VisualizationLayoutMode.ScopeStage);

        for (int row = 0; row < layout.RowCount; row++)
        {
            int firstInRow = Math.Min(row * layout.ColumnCount, layout.PanelCount - 1);
            OverlayRect scope = layout.GetScopeRect(firstInRow);
            Assert.Equal(layout.GridY + row * layout.ScopeHeight, scope.Y);
            Assert.Equal(layout.ScopeHeight, scope.Height);
            Assert.Equal(0, layout.GetHeaderRect(firstInRow).Height);
        }
    }

    // ------------------------------------------------------------------
    // Renderer smoke tests
    // ------------------------------------------------------------------

    private static PanelOverlayRenderer CreateRenderer(VisualizationLayoutMode mode, int width = 960, int height = 540)
        => new(
            VisualizationTimelineFixture.Create(),
            new PanelOverlayRenderer.Options
            {
                Width = width,
                Height = height,
                FpsNumerator = 20,
                FpsDenominator = 1,
                PastSeconds = 0.75,
                FutureSeconds = 2.25,
                LayoutMode = mode,
            });

    [Fact]
    public void Performance_RendersFramesWithoutThrowing()
    {
        PanelOverlayRenderer renderer = CreateRenderer(VisualizationLayoutMode.Performance);
        Assert.Equal(1, renderer.Layout.ColumnCount);

        byte[] frame = renderer.RenderFrame(40); // 2 s in
        Assert.Equal(renderer.FrameByteCount, frame.Length);

        // The unified semantic region contains drawn note content (alpha 255),
        // and the scope strip stays transparent for Corrscope.
        OverlayRect semantic = renderer.Layout.SharedSemanticRect;
        Assert.Equal(255, AlphaAt(frame, renderer.Width, semantic.X + 100, semantic.Y + semantic.Height / 2));
        OverlayRect scope = renderer.Layout.SharedScopeRect;
        Assert.Equal(0, AlphaAt(frame, renderer.Width, scope.X + scope.Width / 2, scope.Y + scope.Height / 2));
    }

    [Fact]
    public void ScopeStage_DrawsTheActivityStripAndKeepsScopeCellsTransparent()
    {
        PanelOverlayRenderer renderer = CreateRenderer(VisualizationLayoutMode.ScopeStage);
        byte[] frame = renderer.RenderFrame(30); // 1.5 s in — the fixture notes are active

        OverlayRect strip = renderer.Layout.ScopeStageStripRect;
        Assert.True(strip.Height > 0);

        // The strip is dynamic content: fully drawn (opaque background) even
        // though the fixture carries no scope stems.
        Assert.Equal(255, AlphaAt(frame, renderer.Width, strip.X + 10, strip.Y + 2));

        // Notes land in the strip lanes: with the fixture's 12-panel legacy
        // topology the strip holds one lane per panel, and notes at t=1.5s
        // (samples 1000–3700) are visible inside the window (0.75s past/2.25s
        // future at sample 1500 → window 750..3750).
        bool hasToken = false;
        for (int y = strip.Y; y < strip.Bottom; y++)
        {
            for (int x = strip.X; x < strip.Right; x += 8)
            {
                if (AlphaAt(frame, renderer.Width, x, y) == 255)
                {
                    byte r = frame[(y * renderer.Width + x) * 4 + 0];
                    byte g = frame[(y * renderer.Width + x) * 4 + 1];
                    byte b = frame[(y * renderer.Width + x) * 4 + 2];
                    if (r != 0 || g != 0 || b != 0)
                        hasToken = true;
                }
            }
        }
        Assert.True(hasToken, "Expected note/activity tokens in the ScopeStage strip");

        // Scope cells stay transparent (Corrscope fills them in the final pass).
        OverlayRect scope = renderer.Layout.GetScopeRect(0);
        Assert.Equal(0, AlphaAt(frame, renderer.Width, scope.X + scope.Width / 2, scope.Y + scope.Height / 2));
    }

    private static byte AlphaAt(byte[] frame, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return frame[offset + 3];
    }
}
