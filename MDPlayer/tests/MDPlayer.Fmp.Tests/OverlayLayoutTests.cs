using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class OverlayLayoutTests
{
    private static OverlayLayout CreateLayout(int width = 1920, int height = 1080)
        => new(width, height, pastSeconds: 0.75, futureSeconds: 2.25);

    [Fact]
    public void Constructor_DistributesWidthRemainderAcrossColumns()
    {
        int width = 1921; // 1921 % 3 != 0
        int height = 1080;
        var layout = new OverlayLayout(width, height, 0.75, 2.25);

        Assert.Equal(width, Enumerable.Range(0, OverlayLayout.Columns)
            .Sum(column => layout.GetPanelRect(column).Width));
        Assert.Equal(layout.GetPanelRect(0).Right, layout.GetPanelRect(1).X);
    }

    [Fact]
    public void Constructor_AlwaysProducesGridDivisibleByRows()
    {
        // Any valid height must yield a grid divisible by Rows, because the
        // bottom bar absorbs the remainder so Corrscope and the overlay agree.
        foreach (int height in new[] { 540, 720, 900, 1040, 1080, 1440, 2160 })
        {
            var layout = new OverlayLayout(1920, height, 0.75, 2.25);
            Assert.Equal(0, layout.GridHeight % OverlayLayout.Rows);
            Assert.Equal(layout.TopBarHeight + layout.GridHeight + layout.BottomBarHeight, layout.Height);
        }
    }

    [Fact]
    public void Constructor_RejectsHeightTooSmallForBandsAndGrid()
    {
        // 200px cannot fit two metadata bands plus a 240px grid.
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayLayout(480, 200, 0.75, 2.25));
    }

    [Fact]
    public void CorrscopeGridHeight_EqualsScopeHeightTimesRows()
    {
        var layout = CreateLayout();
        Assert.Equal(layout.ScopeHeight * OverlayLayout.Rows, layout.CorrscopeGridHeight);
    }

    [Fact]
    public void FocusGeometry_UsesCompactFixedGridForTwoPanels()
    {
        var layout = new OverlayLayout(1920, 1080, 0.75, 2.25, panelCount: 2);

        Assert.Equal(2, layout.PanelCount);
        Assert.Equal(2, layout.ColumnCount);
        Assert.Equal(1, layout.RowCount);
        Assert.Equal(layout.ScopeHeight, layout.CorrscopeGridHeight);
        Assert.Equal(layout.GridY, layout.GetPanelRect(0).Y);
        Assert.Equal(layout.GetPanelRect(0).Bottom, layout.BottomBarRect.Y);
    }

    [Fact]
    public void EightPanels_UseBalancedFourByTwoGrid()
    {
        // The diagnostic grid must never leave an unused rectangle: 8 panels
        // pack into 4x2 exactly instead of 3x3-with-one-empty-cell.
        var layout = new OverlayLayout(1920, 1080, 0.75, 2.25, panelCount: 8);

        Assert.Equal(4, layout.ColumnCount);
        Assert.Equal(2, layout.RowCount);
        Assert.Equal(layout.BottomBarRect.Y, layout.GetPanelRect(7).Bottom);
    }

    [Fact]
    public void DefaultGrid_UsesBalancedPublishingShapes()
    {
        Assert.Equal((3, 2), (OverlayLayout.DefaultGrid(6).Columns, OverlayLayout.DefaultGrid(6).Rows));
        Assert.Equal((3, 2), (OverlayLayout.DefaultGrid(5).Columns, OverlayLayout.DefaultGrid(5).Rows));
        Assert.Equal((2, 2), (OverlayLayout.DefaultGrid(4).Columns, OverlayLayout.DefaultGrid(4).Rows));
    }

    [Fact]
    public void LargePcmTopologyFitsAdaptiveGrid()
    {
        var layout = new OverlayLayout(1920, 1080, 0.75, 2.25, panelCount: 32);

        Assert.Equal(6, layout.ColumnCount);
        Assert.Equal(6, layout.RowCount);
        Assert.Equal(layout.BottomBarRect.Y, layout.GetPanelRect(31).Bottom);
    }

    [Fact]
    public void GetScopeRowDestinationY_ReturnsCorrectYForEachRow()
    {
        var layout = CreateLayout();
        for (int row = 0; row < OverlayLayout.Rows; row++)
        {
            int expectedY = layout.GetScopeRect(row * OverlayLayout.Columns).Y;
            Assert.Equal(expectedY, layout.GetScopeRowDestinationY(row));
        }
    }

    [Fact]
    public void GetScopeRowDestinationY_ThrowsForNegativeRow()
    {
        var layout = CreateLayout();
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetScopeRowDestinationY(-1));
    }

    [Fact]
    public void GetScopeRowDestinationY_ThrowsForRowAtOrAboveCount()
    {
        var layout = CreateLayout();
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.GetScopeRowDestinationY(OverlayLayout.Rows));
    }

    // ---- Final 1920x1080 geometry (PR5) ----

    [Fact]
    public void Geometry_1080p_HasExactTopBarGridBottomSplit()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(64, layout.TopBarHeight);
        Assert.Equal(976, layout.GridHeight);
        Assert.Equal(40, layout.BottomBarHeight);
        Assert.Equal(1080, layout.TopBarHeight + layout.GridHeight + layout.BottomBarHeight);
    }

    [Fact]
    public void Geometry_1080p_GridHeightIsDivisibleByFour()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(0, layout.GridHeight % OverlayLayout.Rows);
    }

    [Fact]
    public void Geometry_1080p_PanelWidthIsDivisibleByThree()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(0, layout.Width % OverlayLayout.Columns);
        Assert.Equal(640, layout.PanelWidth);
    }

    [Fact]
    public void Geometry_1080p_DiagnosticGridUsesAlignedTimelineBody()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(244, layout.PanelHeight);
        Assert.Equal(28, layout.PanelHeaderHeight);
        Assert.Equal(216, layout.ScopeHeight);
        Assert.Equal(0, layout.DividerHeight);
        Assert.Equal(216, layout.TimelineHeight);
        Assert.Equal(layout.PanelHeight - layout.PanelHeaderHeight, layout.GetTimelineRect(0).Height);
        Assert.Equal(layout.GetTimelineRect(0).X + layout.PitchLabelWidth, layout.GetScopeRect(0).X);
        Assert.Equal(layout.GetTimelineRect(0).Y, layout.GetScopeRect(0).Y);
    }

    [Fact]
    public void Geometry_1080p_FirstPanelStartsAtTopBarHeight()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(64, layout.GetPanelRect(0).Y);
    }

    [Fact]
    public void Geometry_1080p_FinalPanelEndsBeforeFooter()
    {
        var layout = CreateLayout(1920, 1080);
        // Row 3 ends at 64 + 4*244 = 1040; footer occupies 1040–1079.
        Assert.Equal(1040, layout.GetPanelRect(11).Bottom);
    }

    [Fact]
    public void Geometry_1080p_FooterOccupiesBottomFortyRows()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(1040, layout.BottomBarRect.Y);
        Assert.Equal(1080, layout.BottomBarRect.Bottom);
        Assert.Equal(40, layout.BottomBarRect.Height);
    }

    [Fact]
    public void Geometry_1080p_CorrscopeGridSpansFullBody()
    {
        var layout = CreateLayout(1920, 1080);
        // The integrated DiagnosticGrid scope is the full body height, so the
        // Corrscope grid spans ScopeHeight * RowCount = 216 * 4.
        Assert.Equal(864, layout.CorrscopeGridHeight);
    }

    [Fact]
    public void Geometry_1080p_ScopeRowDestinationsAreCorrect()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(92, layout.GetScopeRowDestinationY(0));
        Assert.Equal(336, layout.GetScopeRowDestinationY(1));
        Assert.Equal(580, layout.GetScopeRowDestinationY(2));
        Assert.Equal(824, layout.GetScopeRowDestinationY(3));
    }

    [Fact]
    public void TopBarRect_CoversFullWidthAtTop()
    {
        var layout = CreateLayout(1920, 1080);
        Assert.Equal(0, layout.TopBarRect.X);
        Assert.Equal(0, layout.TopBarRect.Y);
        Assert.Equal(1920, layout.TopBarRect.Width);
        Assert.Equal(64, layout.TopBarRect.Height);
    }
}
