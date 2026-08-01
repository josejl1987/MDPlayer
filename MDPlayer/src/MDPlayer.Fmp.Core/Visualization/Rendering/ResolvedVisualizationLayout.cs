namespace Fmp.Core.Visualization.Rendering;

internal sealed record ResolvedVisualizationLayout(
    VisualizationLayoutMode Mode,
    VisualizationTopology Topology,
    OverlayLayout Geometry);
