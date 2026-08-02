using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class VisualizationLayoutResolutionTests
{
    [Fact]
    public void DiagnosticCompositionMapsToDiagnosticLayout()
    {
        Assert.Equal(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutModeMapper.FromComposition(CompositionKind.Diagnostic));
    }

    [Fact]
    public void EveryPublicCompositionHasAnInternalLayoutMapping()
    {
        foreach (CompositionKind composition in Enum.GetValues<CompositionKind>())
            _ = VisualizationLayoutModeMapper.FromComposition(composition);
    }

    [Fact]
    public void DiagnosticActiveLayoutExcludesInactivePanels()
    {
        VisualizationTimeline timeline = TimelineWithActiveAndInactiveVoices();

        ResolvedVisualizationLayout result = VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            Settings(VisualizationChannelFilter.Active));

        Assert.Single(result.Topology.Panels);
        Assert.Equal("ym2612.0.fm.1", result.Topology.Panels[0].Id);
    }

    [Fact]
    public void DiagnosticAllLayoutIncludesInactivePanels()
    {
        VisualizationTimeline timeline = TimelineWithActiveAndInactiveVoices();

        ResolvedVisualizationLayout result = VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            Settings(VisualizationChannelFilter.All));

        Assert.Equal(2, result.Topology.Panels.Count);
    }

    [Fact]
    public void RendererUsesSuppliedTopologyAndGeometry()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        ResolvedVisualizationLayout full = RendererTestLayout.Build(timeline);
        VisualizationTopology oneTopology = new([full.Topology.Panels[0]]);
        var oneGeometry = new OverlayLayout(960, 540, 0.75, 2.25, 1, VisualizationLayoutMode.Diagnostic);
        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            VisualizationLayoutResolver.Decide(960, 540, oneGeometry.ScopeHeight, rollPossible: true);
        var onePanel = new ResolvedVisualizationLayout(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutVariant.DiagnosticGrid,
            oneTopology,
            oneGeometry,
            density,
            caps);

        using var renderer = new PanelOverlayRenderer(
            timeline,
            onePanel,
            new PanelOverlayRenderer.Options { FpsNumerator = 20 });

        Assert.Equal(1, renderer.Layout.PanelCount);
        Assert.Same(onePanel.Geometry, renderer.Layout);
        Assert.Same(onePanel.Topology, renderer.Topology);
    }

    private static VisualizationLayoutSettings Settings(VisualizationChannelFilter channels)
        => new(
            960,
            540,
            0.75,
            2.25,
            1.0,
            null,
            null,
            null,
            VisualizationScopePosition.Top,
            channels,
            VisualizationGroupBy.None);

    private static VisualizationTimeline TimelineWithActiveAndInactiveVoices()
    {
        VoiceDescriptor[] voices = VisualizationDeviceCatalog.Ym2612Voices().Take(2).ToArray();
        return new VisualizationTimeline
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [VisualizationDeviceCatalog.Ym2612()],
            Voices = voices,
            Notes =
            [
                new NoteEvent(
                    voices[0].Id.ToString(),
                    0,
                    1_000,
                    440,
                    69,
                    "instrument",
                    VisualizationNoteMode.Fm,
                    false,
                    []),
            ],
        };
    }
}
