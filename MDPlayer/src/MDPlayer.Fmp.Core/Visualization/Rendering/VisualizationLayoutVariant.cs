namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The concrete visual grammar a resolved layout must render with. A single
/// layout mode can resolve to any of these variants depending on whether the
/// canvas geometry can actually fit the full diagnostic panel content. The
/// renderer must not reverse-engineer the fallback from panel dimensions — it
/// reads <see cref="ResolvedVisualizationLayout.Variant"/> directly.
/// </summary>
internal enum VisualizationLayoutVariant
{
    /// <summary>
    /// The full diagnostic grid: channel name, current note/state,
    /// instrument/patch, scope, pitch gutter and piano-roll content. Only used
    /// when every panel meets the full minimum geometry (300×150).
    /// </summary>
    DiagnosticGrid,

    /// <summary>
    /// Compact per-channel overview: channel name, accent identity, current
    /// note/state, a compact per-channel waveform and activity status. Omits the
    /// piano roll, pitch gutter, patch details and operator ribbons.
    /// </summary>
    DiagnosticOverview,

    /// <summary>
    /// Device-level rollup when even channel overview panels cannot fit:
    /// one panel per device showing device label, active/total channel count,
    /// aggregate waveform and a current activity indicator.
    /// </summary>
    DeviceOverview,

    /// <summary>
    /// Performance lanes: every panel becomes one full-width horizontal band
    /// sharing a single common time axis (identical playhead X in every lane),
    /// with no header chrome — the channel label lives in the left gutter and
    /// lane boundaries are thin separators. Used by the native Performance
    /// composition so rhythmic relationships read across channels.
    /// </summary>
    PerformanceLanes,
}