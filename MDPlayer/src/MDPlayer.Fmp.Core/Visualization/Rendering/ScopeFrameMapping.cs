#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Output-frame → scope-frame cadence mapping (plan §5). The scope renders at
/// its own frame rate (YAML <c>fps:</c>, e.g. 30 Hz) while the output runs at
/// the video frame rate (e.g. 60 Hz). The mapping is the single choke point
/// shared by the frame renderer, the legacy process-bridge composer and the
/// YAML writer so every consumer agrees on which scope frame belongs to which
/// output frame.
/// </summary>
internal static class ScopeFrameMapping
{
    /// <summary>
    /// Maps an output frame index to the scope frame index to display. When
    /// the scope cadence is at least the output cadence the mapping is 1:1
    /// (skipping ahead would waste rendered scope frames); otherwise it is
    /// <c>floor(out * scopeFps / outputFps)</c> — monotonic non-decreasing, so
    /// a sequential scope source only ever reads forward (no restarts).
    /// </summary>
    public static long Map(long outputFrame, double scopeFps, double outputFps)
        => scopeFps >= outputFps
            ? outputFrame
            : (long)Math.Floor(outputFrame * scopeFps / outputFps);

    /// <summary>
    /// Resolves the effective scope frame rate: an explicit positive value is
    /// clamped to the output frame rate (1:1 — never render faster than the
    /// output), and null (or a non-positive value) defaults to
    /// <c>min(outputFps, 30)</c>.
    /// </summary>
    public static double Resolve(double? scopeFps, double outputFps)
        => scopeFps is > 0
            ? Math.Min(scopeFps.Value, outputFps)
            : Math.Min(outputFps, 30.0);
}
