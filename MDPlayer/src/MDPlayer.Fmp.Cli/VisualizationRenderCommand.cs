using Fmp.Application.Contracts;
using Fmp.Application.Export;

namespace Fmp.Cli;

/// <summary>
/// The canonical visualization <c>render</c> command (spec §18). It parses the
/// full option set the Application formatter emits (the same option set the
/// GUI and parity tests pin), builds an authoritative schema-1
/// <see cref="VisualizationRequest"/>, maps it onto the Core-backed pipeline
/// model via <see cref="VisualizeOptions.ApplyRequest"/>, and runs the same
/// capture → prepare → compose pipeline as the visualize command.
///
/// Runtime tool paths (--fmp-com, --corrscope, --ffmpeg, --analysis-python)
/// are process-level and never serialized into the request.
/// </summary>
public static class VisualizationRenderCommand
{
    public static int Handle(string[] args)
    {
        (VisualizationRequest Request, RenderRuntimeOptions Runtime, string? RequestJsonPath)? parsed =
            RenderCommandParser.Parse(args);
        if (parsed is null)
            return 2;

        (VisualizationRequest request, RenderRuntimeOptions runtime, _) = parsed.Value;

        var options = new VisualizeOptions();
        options.ApplyRequest(request);
        ApplyRuntimeOptions(options, runtime);

        if (string.IsNullOrEmpty(options.Input))
        {
            Console.Error.WriteLine("error: no input file specified");
            return 2;
        }

        VisualizationBackendResolution resolution;
        try
        {
            resolution = VisualizationBackendResolver.Resolve(options);
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }

        return string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal)
            ? VisualizationRunner.Run(options)
            : VgmVisualizeCommand.Handle(options, resolution);
    }

    private static void ApplyRuntimeOptions(VisualizeOptions options, RenderRuntimeOptions runtime)
    {
        if (runtime.FmpCom != null)
        {
            options.FmpCom = runtime.FmpCom;
            options.FmpComExplicit = true;
        }
        if (runtime.AssetsDir != null)
            options.AssetsDir = runtime.AssetsDir;
        if (runtime.CorrscopePath != null)
            options.CorrscopePath = runtime.CorrscopePath;
        if (runtime.FfmpegPath != null)
            options.FfmpegPath = runtime.FfmpegPath;
        if (runtime.AnalysisPython != null)
            options.AnalysisPython = runtime.AnalysisPython;
        options.Quiet = runtime.Quiet;
        options.Json = runtime.Json;
        options.ProgressMode = runtime.ProgressMode;
        options.ExternalToolTimeoutMinutes = runtime.ToolTimeoutMinutes;
    }

    /// <summary>Parser seam for parity tests.</summary>
    internal static (VisualizationRequest Request, RenderRuntimeOptions Runtime, string? RequestJsonPath)? ParseArgs(
        string[] args)
        => RenderCommandParser.Parse(args);

    internal static VisualizationRequest ParseRequestStrict(string[] args)
        => RenderCommandParser.ParseCore(args).Request;
}
