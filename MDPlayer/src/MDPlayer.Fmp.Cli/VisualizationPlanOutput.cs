using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

internal static class VisualizationPlanOutput
{
    public static void Emit(
        VisualizationTimeline timeline,
        VisualizationTopology topology,
        OverlayLayout layout,
        VisualizationLayoutMode requestedLayout,
        VisualizationChannelFilter channelFilter,
        VisualizationGroupBy groupBy,
        VisualizationTimeGrid timeGrid,
        VisualizationScopePosition scopePosition,
        double? scopeRatio,
        int fpsNumerator,
        int fpsDenominator,
        string selectedEncoder,
        string analysisAvailability,
        bool print,
        string jsonPath)
    {
        if (!print && string.IsNullOrWhiteSpace(jsonPath))
            return;

        VisualizationLayoutPlan plan = VisualizationLayoutPlan.Create(
            timeline,
            topology,
            layout,
            requestedLayout,
            channelFilter,
            groupBy,
            timeGrid,
            scopePosition,
            scopeRatio,
            fpsNumerator,
            fpsDenominator,
            selectedEncoder,
            analysisAvailability);
        string json = plan.ToJson();
        if (print)
            Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            string fullPath = Path.GetFullPath(jsonPath);
            string parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(fullPath, json);
        }
    }
}
