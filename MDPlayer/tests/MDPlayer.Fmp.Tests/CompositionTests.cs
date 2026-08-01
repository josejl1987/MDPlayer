using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Geometry and rendering contracts for the single canonical publishing
/// composition: Diagnostic (full channel grid). The class name is retained
/// from the multi-composition era; future layouts extend this file.
/// </summary>
public sealed class CompositionTests
{
    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 12)]
    [InlineData(1280, 720, 1)]
    [InlineData(1280, 720, 8)]
    public void Diagnostic_GridCoversTheContentBand(int width, int height, int panelCount)
    {
        var layout = new OverlayLayout(
            width, height, 0.75, 2.25, panelCount,
            VisualizationLayoutMode.Diagnostic);

        Assert.True(layout.HasRoll);
        Assert.True(layout.HasScopes);

        // The scope mosaic height equals the scope rows exactly (Corrscope
        // agreement), and each panel keeps a header plus roll.
        Assert.Equal(layout.ScopeHeight * layout.RowCount, layout.CorrscopeGridHeight);
        Assert.Equal(layout.Width, layout.CorrscopeGridWidth);
        Assert.True(layout.PanelHeaderHeight > 0);
        Assert.True(layout.TimelineHeight > 0);
        Assert.Equal(layout.BottomBarRect.Y, layout.GridY + layout.GridHeight);
    }

    [Fact]
    public void Diagnostic_ScopeCellsCoverTheMosaic()
    {
        var layout = new OverlayLayout(
            1920, 1080, 0.75, 2.25, panelCount: 8,
            VisualizationLayoutMode.Diagnostic);

        for (int row = 0; row < layout.RowCount; row++)
        {
            int firstInRow = Math.Min(row * layout.ColumnCount, layout.PanelCount - 1);
            OverlayRect panel = layout.GetPanelRect(firstInRow);
            OverlayRect scope = layout.GetScopeRect(firstInRow);
            Assert.Equal(panel.Y + layout.PanelHeaderHeight, scope.Y);
            Assert.Equal(layout.ScopeHeight, scope.Height);
            Assert.True(layout.GetHeaderRect(firstInRow).Height > 0);
        }
    }

    // ------------------------------------------------------------------
    // Renderer smoke tests
    // ------------------------------------------------------------------

    private static PanelOverlayRenderer CreateRenderer(VisualizationLayoutMode mode, int width = 960, int height = 540)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        return new(
            timeline,
            RendererTestLayout.Build(timeline, width, height),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 20,
                FpsDenominator = 1,
            });
    }

    [Fact]
    public void Diagnostic_RendersFramesWithoutThrowing()
    {
        PanelOverlayRenderer renderer = CreateRenderer(VisualizationLayoutMode.Diagnostic);
        Assert.True(renderer.Layout.ColumnCount >= 2);

        byte[] frame = renderer.RenderFrame(40); // 2 s in
        Assert.Equal(renderer.FrameByteCount, frame.Length);

        // The roll lanes contain drawn note content (alpha 255), and the
        // scope cells stay transparent for Corrscope.
        OverlayRect roll = renderer.Layout.GetTimelineRect(0);
        Assert.Equal(255, AlphaAt(frame, renderer.Width, roll.X + 100, roll.Y + roll.Height / 2));
        OverlayRect scope = renderer.Layout.GetScopeRect(0);
        Assert.Equal(0, AlphaAt(frame, renderer.Width, scope.X + scope.Width / 2, scope.Y + scope.Height / 2));
    }

    [Fact]
    public void Diagnostic_CompositesEveryCorrscopeCell()
    {
        PanelOverlayRenderer renderer = CreateRenderer(VisualizationLayoutMode.Diagnostic);
        int columns = renderer.Layout.ColumnCount;
        int rows = renderer.Layout.RowCount;
        int cellWidth = renderer.Layout.CorrscopeGridWidth / columns;
        int cellHeight = renderer.Layout.ScopeHeight;
        var scopeGrid = new byte[renderer.ScopeFrameByteCount];

        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            byte red = (byte)(40 + row * 30);
            byte green = (byte)(60 + column * 30);
            byte blue = (byte)(100 + (row + column) * 10);
            for (int y = row * cellHeight; y < (row + 1) * cellHeight; y++)
            for (int x = column * cellWidth; x < (column + 1) * cellWidth; x++)
            {
                int offset = (y * renderer.Layout.CorrscopeGridWidth + x) * 4;
                scopeGrid[offset] = red;
                scopeGrid[offset + 1] = green;
                scopeGrid[offset + 2] = blue;
            }
        }

        byte[] frame = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, scopeGrid, frame);
        for (int panel = 0; panel < renderer.Layout.PanelCount; panel++)
        {
            OverlayRect scope = renderer.Layout.GetScopeRect(panel);
            int offset = (scope.Y + scope.Height / 2) * renderer.Width * 4
                + (scope.X + scope.Width / 2) * 4;
            Assert.Equal(255, frame[offset + 3]);
            Assert.NotEqual(0, frame[offset] | frame[offset + 1] | frame[offset + 2]);
        }
    }

    private static byte AlphaAt(byte[] frame, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return frame[offset + 3];
    }
}
