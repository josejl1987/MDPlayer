namespace Fmp.Core.Visualization.Rendering;

internal sealed record VisualizationGpuProbe(bool Supported, string Reason);

/// <summary>
/// Probes the actual OpenCL semantic primitive renderer. A filter-list lookup
/// or encoder capability check alone is deliberately insufficient.
/// </summary>
internal static class VisualizationGpuSupport
{
    public static VisualizationGpuProbe Probe()
    {
        try
        {
            if (!VisualizationOpenClRenderer.TryCreate(
                    1,
                    1,
                    1,
                    out VisualizationOpenClRenderer renderer,
                    out string reason))
                return new(false, reason);
            renderer.Dispose();
            return new(true, reason);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }
}
