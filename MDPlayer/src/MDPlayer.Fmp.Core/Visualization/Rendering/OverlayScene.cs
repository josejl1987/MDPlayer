using Fmp.Core.Metadata;
using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Immutable prepared scene data for CPU overlay rendering. Built once from
/// the visualization timeline by <see cref="OverlaySceneBuilder"/> and held
/// read-only during frame rendering.
/// </summary>
internal sealed class OverlayScene
{
    public required OverlayLayout Layout { get; init; }
    public required VisualizationTopology Topology { get; init; }
    public required PreparedPanel[] Panels { get; init; }
    public required VisualizationMetadata Metadata { get; init; }
    public required int SampleRate { get; init; }
    public required long StartSample { get; init; }
    public required long EndSample { get; init; }
}
