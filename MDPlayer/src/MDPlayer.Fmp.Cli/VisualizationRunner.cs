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

            // When the caller supplied a captured bundle directory, load and
            // validate the published capture and reuse it for the render
            // (semantic timeline, scope/stem synthesis, and master audio are all
            // taken from the bundle instead of being recomputed).
            string? seedTimelinePath = null;
            PreparedCapture? reusableCapture = null;
            if (!string.IsNullOrWhiteSpace(invocation.CaptureDirectory)
                && File.Exists(VisualizationCaptureBundle.ManifestPath(invocation.CaptureDirectory)))
            {
                var inputFile = new FileInfo(Path.GetFullPath(request.InputPath));
                TimelineCaptureKey key = TimelineCaptureKey.From(request, inputFile, runtime);
                reusableCapture = VisualizationCaptureBundle.LoadPreparedCaptureAsync(
                    invocation.CaptureDirectory,
                    VisualizationCaptureBundle.ComputeCaptureKey(key),
                    request,
                    resolution.Backend.Id,
                    CancellationToken.None).GetAwaiter().GetResult();
                string bundleTimeline = Path.Combine(invocation.CaptureDirectory, "timeline.json");
                if (File.Exists(bundleTimeline))
                    seedTimelinePath = bundleTimeline;
            }

            return RunCore(request, runtime, resolution, seedTimelinePath, reusableCapture);
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (VisualizationExecutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
    }
}
