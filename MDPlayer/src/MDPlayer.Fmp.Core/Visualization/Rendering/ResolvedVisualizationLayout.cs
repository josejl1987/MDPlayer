namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The fully resolved presentation: the layout mode, the residual layout
/// (full diagnostic grid, diagnostic overview, or device overview) that the
/// canvas geometry could actually support, the panel topology, and the concrete
/// geometry. The variant tells the renderer which visual grammar to use so it
/// never has to guess the fallback from panel dimensions.
/// </summary>
internal sealed record ResolvedVisualizationLayout(
    VisualizationLayoutMode Mode,
    VisualizationLayoutVariant Variant,
    VisualizationTopology Topology,
    OverlayLayout Geometry);