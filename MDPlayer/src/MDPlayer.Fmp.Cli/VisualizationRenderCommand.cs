using Fmp.Application.Contracts;
using Fmp.Application.Export;

namespace Fmp.Cli;

/// <summary>
/// The canonical visualization <c>render</c> command (spec §18). It parses the
/// full option set the Application formatter emits (the same option set the
/// GUI and parity tests pin), builds an authoritative schema-1
/// <see cref="VisualizationRequest"/>, maps it onto the Core-backed pipeline
/// request/runtime contract and runs the capture → prepare → compose pipeline.
///
/// Runtime tool paths (--fmp-com, --corrscope, --ffmpeg, --analysis-python)
/// are process-level and never serialized into the request.
/// </summary>
public static class VisualizationRenderCommand
{
    public static int Handle(string[] args)
    {
        RenderInvocation? invocation = RenderCommandParser.ParseInvocation(args);
        if (invocation is null)
            return 2;
        return VisualizationRunner.Run(invocation);
    }

    /// <summary>Parser seam for parity tests.</summary>
    internal static (VisualizationRequest Request, RenderRuntimeOptions Runtime, string? RequestJsonPath, string? CaptureDirectory, string? CaptureKey)? ParseArgs(
        string[] args)
        => RenderCommandParser.Parse(args);

    internal static VisualizationRequest ParseRequestStrict(string[] args)
        => RenderCommandParser.ParseCore(args).Request;
}
