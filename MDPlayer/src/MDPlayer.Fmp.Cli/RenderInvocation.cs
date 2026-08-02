namespace Fmp.Cli;

internal sealed record RenderInvocation(
    Fmp.Application.Contracts.VisualizationRequest Request,
    RenderRuntimeOptions Runtime,
    string? CaptureDirectory = null);
