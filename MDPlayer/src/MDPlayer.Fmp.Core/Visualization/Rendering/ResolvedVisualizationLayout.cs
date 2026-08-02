namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The fully resolved presentation: the layout mode, the residual layout
/// (full diagnostic grid, diagnostic overview, or device overview) that the
/// canvas geometry could actually support, the panel topology, the concrete
/// geometry, and the density/capability decision that the overlay and Corrscope
/// both consume. The variant tells the renderer which visual grammar to use so
/// it never has to guess the fallback from panel dimensions; Density and
/// Capabilities tell callers which optional regions are actually rendered so
/// they never infer scopes/labels detailedness from pixels.
/// </summary>
internal sealed record ResolvedVisualizationLayout(
    VisualizationLayoutMode Mode,
    VisualizationLayoutVariant Variant,
    VisualizationTopology Topology,
    OverlayLayout Geometry,
    VisualizationLayoutDensity Density,
    VisualizationLayoutCapabilities Capabilities);
