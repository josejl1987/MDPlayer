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

        // The roll lane contains drawn note content (alpha 255), and the scope
        // cells stay transparent for Corrscope.
        OverlayRect roll = renderer.Layout.GetTimelineRect(0);
        bool rollHasOpaqueNote = false;
        for (int y = roll.Y; y < roll.Bottom && !rollHasOpaqueNote; y++)
        {
            for (int x = roll.X + 30; x < roll.Right - 10 && !rollHasOpaqueNote; x++)
                rollHasOpaqueNote = AlphaAt(frame, renderer.Width, x, y) == 255;
        }
        Assert.True(rollHasOpaqueNote, "the roll lane must contain opaque note content");
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
                // The scope layer is an alpha mask (Change A): only opaque
                // pixels carry RGB through the blend. Mark the whole cell
                // opaque so the composite must show its color.
                scopeGrid[offset + 3] = 255;
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

    [Fact]
    public void Diagnostic_WaveformColumnAndPlayheadShareLayoutX()
    {
        PanelOverlayRenderer renderer = CreateRenderer(VisualizationLayoutMode.Diagnostic);
        byte[] scopeGrid = new byte[renderer.ScopeFrameByteCount];
        int frameIndex = 40;
        int panelIndex = 0;
        OverlayRect scope = renderer.Layout.GetScopeRect(panelIndex);
        int expectedX = renderer.Layout.GetPlayheadX(panelIndex);
        int localX = expectedX - scope.X;
        Assert.InRange(localX, 0, scope.Width - 1);

        for (int y = 0; y < scope.Height; y++)
        {
            int offset = (y * renderer.Layout.CorrscopeGridWidth + localX) * 4;
            scopeGrid[offset] = 255;
            scopeGrid[offset + 3] = 255;
        }

        byte[] frame = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(frameIndex, scopeGrid, frame);

        Assert.Equal(255, frame[((scope.Y + scope.Height / 2) * renderer.Width + expectedX) * 4 + 3]);
        OverlayRect timeline = renderer.Layout.GetTimelineRect(panelIndex);
        Assert.True(AlphaAt(frame, renderer.Width, expectedX, timeline.Y + timeline.Height / 2) > 0);
    }

    [Fact]
    public void Diagnostic_WaveformLayerPreservesAlphaBoundsAndFm3RhythmSemanticVariants()
    {
        PanelOverlayRenderer renderer = CreateRenderer(VisualizationLayoutMode.Diagnostic);
        byte[] baseline = renderer.RenderFrame(40);
        byte[] scopeGrid = new byte[renderer.ScopeFrameByteCount];
        Array.Fill(scopeGrid, (byte)90);
        byte[] composed = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(40, scopeGrid, composed);

        for (int panel = 0; panel < renderer.Layout.PanelCount; panel++)
        {
            OverlayRect scope = renderer.Layout.GetScopeRect(panel);
            for (int y = scope.Y; y < scope.Bottom; y++)
            for (int x = scope.X; x < scope.Right; x++)
                Assert.Equal(255, AlphaAt(composed, renderer.Width, x, y));
        }

        // FM3 (panel 2) and rhythm (panel 9) retain their semantic lanes when
        // a waveform layer is present; scope compositing must not overwrite them.
        foreach (int panelIndex in new[] { 2, 9 })
        {
            OverlayRect timeline = renderer.Layout.GetTimelineRect(panelIndex);
            int coloredBaseline = CountColored(baseline, renderer.Width, timeline);
            int coloredComposed = CountColored(composed, renderer.Width, timeline);
            Assert.True(coloredBaseline > 0, $"panel {panelIndex} baseline should contain semantic content");
            // The composite may add the opaque waveform, but it must never remove
            // the FM3/rhythm semantic pixels that were already there.
            Assert.True(coloredComposed >= coloredBaseline,
                $"panel {panelIndex} composite must not overwrite semantic lanes");
        }
    }

    [Theory]
    [InlineData(960, 300, false)]
    [InlineData(320, 120, true)]
    public void OverviewVariants_DoNotReceiveDiagnosticWaveformCells(
        int width, int height, bool deviceOverview)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline, VisualizationChannelFilter.All, VisualizationGroupBy.None);
        ResolvedVisualizationLayout resolved = VisualizationLayoutResolver.Resolve(
            width, height, 0.75, 2.25, VisualizationLayoutMode.Diagnostic, topology,
            topology.Panels.Count, VisualizationScopePosition.Top);
        Assert.Equal(
            deviceOverview ? VisualizationLayoutVariant.DeviceOverview : VisualizationLayoutVariant.DiagnosticOverview,
            resolved.Variant);

        using var renderer = new PanelOverlayRenderer(timeline, resolved);
        byte[] baseline = renderer.RenderFrame(0);
        byte[] scopeGrid = new byte[renderer.ScopeFrameByteCount];
        Array.Fill(scopeGrid, (byte)200);
        byte[] composed = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, scopeGrid, composed);

        for (int offset = 0; offset < composed.Length; offset += 4)
        {
            if (composed[offset] == baseline[offset]
                && composed[offset + 1] == baseline[offset + 1]
                && composed[offset + 2] == baseline[offset + 2]
                && composed[offset + 3] == baseline[offset + 3])
                continue;

            int pixel = offset / 4;
            int x = pixel % renderer.Width;
            int y = pixel / renderer.Width;
            Assert.Contains(Enumerable.Range(0, renderer.Layout.PanelCount), panel =>
            {
                OverlayRect scope = renderer.Layout.GetScopeRect(panel);
                return x >= scope.X && x < scope.Right && y >= scope.Y && y < scope.Bottom;
            });
        }
    }

    private static byte AlphaAt(byte[] frame, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return frame[offset + 3];
    }

    private static int CountColored(byte[] frame, int width, OverlayRect rect)
    {
        int count = 0;
        for (int y = rect.Y; y < rect.Bottom; y++)
        for (int x = rect.X; x < rect.Right; x++)
        {
            int offset = (y * width + x) * 4;
            if ((frame[offset] | frame[offset + 1] | frame[offset + 2]) != 0)
                count++;
        }
        return count;
    }
}
