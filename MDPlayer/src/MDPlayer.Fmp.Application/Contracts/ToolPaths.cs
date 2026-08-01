namespace Fmp.Application.Contracts;

/// <summary>
/// Runtime tool-path settings (process-level / application settings). These are
/// intentionally separate from the serialized <see cref="VisualizationRequest"/>
/// so project files never contain workstation tool paths. None of these are
/// persisted into .mdpviz.json.
/// </summary>
public sealed record ToolPaths
{
    public string? FmpComPath { get; init; }
    public string? AssetsDir { get; init; }
    public IReadOnlyList<string> SearchPaths { get; init; } = Array.Empty<string>();
    public string? CorrscopePath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? AnalysisPython { get; init; }
    public string? AnalysisCache { get; init; }
    public string? AnalysisOutput { get; init; }
    public bool AnalysisForce { get; init; }
    public int? AnalysisTimeoutMinutes { get; init; }
    public int? ToolTimeoutMinutes { get; init; }
    public string? CorrscopeVideoTemplate { get; init; }
}