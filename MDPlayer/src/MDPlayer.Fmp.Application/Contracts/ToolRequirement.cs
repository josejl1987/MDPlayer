namespace Fmp.Application.Contracts;

/// <summary>
/// The canonical CLI command for a request. <see cref="Arguments"/> is the
/// argument-safe form used for execution; <see cref="DisplayText"/> is for
/// display and clipboard only.
/// </summary>
public sealed record CanonicalCommand
{
    public required string Executable { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string DisplayText { get; init; }
}

/// <summary>Which tool a feature needs and why.</summary>
public enum ToolRequirementKind
{
    Required,
    Optional,
}

/// <summary>Tool roles understood by the requirement/status model.</summary>
public static class ToolRoles
{
    public const string Ffmpeg = "ffmpeg";
    public const string Corrscope = "corrscope";
    public const string AnalysisPython = "analysis-python";
    public const string RenderCli = "mdplayer-render";
    public const string Nvenc = "h264_nvenc";
    public const string FmpCom = "fmp-com";
}

/// <summary>
/// Feature-gated tool requirement. Requirement calculation is a pure function
/// of the request (see Validation.ToolRequirementResolver).
/// </summary>
public sealed record ToolRequirement
{
    public required string Role { get; init; }
    public required ToolRequirementKind Kind { get; init; }
    public string? Feature { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Resolved tool status (path/version/availability/source).</summary>
public sealed record ToolStatus
{
    public required string Role { get; init; }
    public bool IsRequired { get; init; }
    public bool IsAvailable { get; init; }
    public string? ResolvedPath { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public string? Feature { get; init; }
    public string? Detail { get; init; }
    public string? SuggestedAction { get; init; }
}
