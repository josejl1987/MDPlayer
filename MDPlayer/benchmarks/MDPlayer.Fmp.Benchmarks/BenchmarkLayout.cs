using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Benchmarks;

internal static class BenchmarkLayout
{
    public static ResolvedVisualizationLayout Build(
        VisualizationTimeline timeline,
        int width,
        int height,
        VisualizationChannelFilter channels = VisualizationChannelFilter.All)
        => VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            new VisualizationLayoutSettings(
                width,
                height,
                0.75,
                2.25,
                1.0,
                null,
                null,
                null,
                VisualizationScopePosition.Top,
                channels,
                VisualizationGroupBy.None));
}
