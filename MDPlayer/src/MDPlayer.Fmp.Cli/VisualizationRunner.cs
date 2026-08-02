using Fmp.Application.Contracts;

namespace Fmp.Cli;

/// <summary>
/// Single linear visualization execution entry point. Backend-specific work
/// is selected by the capture and scope coordinators; the command itself has
/// one orchestration path.
/// </summary>
internal static partial class VisualizationRunner
{
    public static int Run(RenderInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        VisualizationRequest request = invocation.Request;
        RenderRuntimeOptions runtime = invocation.Runtime;
        try
        {
            VisualizationBackendResolution resolution =
                VisualizationBackendResolver.Resolve(request, runtime);

            // When the caller supplied a captured bundle directory, seed the
            // semantic timeline from it (reuse) instead of re-capturing. The
            // scope/stem artifacts are still projected per request in the
            // render workspace; only the playback/semantic capture is skipped.
            string? seedTimelinePath = null;
            if (!string.IsNullOrWhiteSpace(invocation.CaptureDirectory))
            {
                string bundleTimeline = Path.Combine(invocation.CaptureDirectory, "timeline.json");
                if (File.Exists(bundleTimeline))
                    seedTimelinePath = bundleTimeline;
            }

            return RunCore(request, runtime, resolution, seedTimelinePath);
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
    }
}
