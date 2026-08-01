using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Describes the visual contract of a panel. Renderers select a presentation
/// from this descriptor instead of interpreting chip- or voice-specific IDs.
/// </summary>
internal enum PanelPresentationSchema
{
    Unknown,
    GenericLane,
    PitchedLane,
    NoiseLane,
    SampleLane,
    PercussionRows,
    WaveTableLane,
    FmOperatorGroup,
    AggregateActivity,
}

internal sealed record PanelRowDefinition(string Id, string Label)
{
    public int StableOrder { get; init; }
    public VisualizationRowKind Kind { get; init; } = VisualizationRowKind.Other;
}
