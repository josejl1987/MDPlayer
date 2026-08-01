using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace MDPlayer.Fmp.Tests;

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
        => VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            new VisualizationLayoutSettings(
                width,
                height,
                pastSeconds,
                futureSeconds,
                1.0,
                null,
                null,
                scopeRatio,
                scopePosition,
                channels,
                VisualizationGroupBy.None));
}
