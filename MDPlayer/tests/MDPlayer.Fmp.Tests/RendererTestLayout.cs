using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Builds a resolved layout that always targets the full diagnostic grid
/// geometry, regardless of whether small canvases would otherwise resolve to a
/// fallback variant. Low-level renderer unit tests exercise the full-grid
/// rendering path (pitch lanes, scope regions, rhythm rows, PCM lanes) and so
/// must not be converted to compact overview panels by the responsive
/// fallback. Asserted-dimension tests that want the fallback decision should
/// call the resolver directly instead.
/// </summary>
internal static class RendererTestLayout
{
    public static ResolvedVisualizationLayout Build(
        VisualizationTimeline timeline,
        int width = 960,
        int height = 540,
        double pastSeconds = 0.75,
        double futureSeconds = 2.25,
        VisualizationChannelFilter channels = VisualizationChannelFilter.All,
        VisualizationScopePosition scopePosition = VisualizationScopePosition.Top,
        double? scopeRatio = null)
    {
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            channels,
            VisualizationGroupBy.None);
        ResolvedPanelGrid grid = OverlayLayout.DefaultGrid(topology.Panels.Count);
        var geometry = new OverlayLayout(
            width,
            height,
            pastSeconds,
            futureSeconds,
            topology.Panels.Count,
            VisualizationLayoutMode.Diagnostic,
            grid.Columns,
            grid.Rows,
            VisualizationLayoutVariant.DiagnosticGrid,
            scopeHeightOverride: null,
            timelineHeightOverride: null,
            rollZoom: 1.0,
            scopeRatioOverride: scopeRatio,
            scopePosition: scopePosition);

        // Preserve the full-grid publishing contract: too-narrow grids must
        // fail actionably (the validator throws a "reduce channels/reduce
        // output width" message) rather than render an illegible panel.
        if (VisualizationContentAvailability.HasRenderableContent(timeline))
            VisualizationLayoutValidator.Validate(geometry, topology);

        return new ResolvedVisualizationLayout(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutVariant.DiagnosticGrid,
            topology,
            geometry);
    }
}