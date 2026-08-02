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
            return RunCore(request, runtime, resolution);
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
    }
}
