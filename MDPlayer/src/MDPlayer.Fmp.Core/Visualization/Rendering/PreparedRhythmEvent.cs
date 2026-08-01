namespace Fmp.Core.Visualization.Rendering;

/// <summary>A rhythm event prepared for CPU overlay rendering.</summary>
internal sealed class PreparedRhythmEvent
{
    public string Voice { get; init; } = "";
    public long SamplePosition { get; init; }
    public float Strength { get; init; }
    public float Pan { get; init; }
}
