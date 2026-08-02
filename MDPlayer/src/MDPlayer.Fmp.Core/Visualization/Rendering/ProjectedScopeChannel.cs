using Fmp.Core.Audio;
using Fmp.Core.Rendering;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Binds a scope channel to exact output geometry. Unlike an ordered
/// <see cref="ScopeRenderer.StemResult"/> list, the panel index is explicit so
/// sparse channel sets never misplace a waveform into the wrong panel. The
/// interactive scope source skips panels with no record; it never shifts later
/// channels forward to fill a gap.
/// </summary>
internal sealed record ProjectedScopeChannel(
    int PanelIndex,
    string Name,
    string Label,
    string WavPath,
    ScopeSemanticClass SemanticClass,
    int WindowWidth,
    double DefaultAmplification,
    string? DefaultColor);
