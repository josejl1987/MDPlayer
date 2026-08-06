namespace Fmp.Cli;

/// <summary>
/// Runtime-only options for the canonical <c>render</c> command (spec §18).
/// Tool paths are process-level settings and are never serialized into the
/// request (schema-1 rule: ToolPaths is runtime-only).
///
/// Lives in the application assembly (kept in the Fmp.Cli namespace for
/// compatibility) so the GUI can construct visualization preview sessions
/// without an assembly reference to the CLI parser.
/// </summary>
internal sealed record RenderRuntimeOptions
{
    public string? FmpCom { get; init; }
    public string? AssetsDir { get; init; }
    public IReadOnlyList<string> SearchPaths { get; init; } = Array.Empty<string>();
    public string? CorrscopePath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? AnalysisPython { get; init; }
    public bool Quiet { get; init; }
    public bool Json { get; init; }
    public string? ProgressMode { get; init; }
    public string Backend { get; init; } = "auto";
    public int ToolTimeoutMinutes { get; init; } = 60;
}
