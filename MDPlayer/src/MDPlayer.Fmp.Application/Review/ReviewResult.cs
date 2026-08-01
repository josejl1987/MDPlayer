using System.Text.RegularExpressions;
using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Core.Visualization;

namespace Fmp.Application.Review;

public enum ReviewCaseStatus
{
    Rendered,
    Unavailable,
    Failed,
}

public sealed record ReviewResolution(string Name, int Width, int Height)
{
    public static ReviewResolution P720 { get; } = new("720p", 1280, 720);
    public static ReviewResolution P1080 { get; } = new("1080p", 1920, 1080);
    public static IReadOnlyList<ReviewResolution> All { get; } = [P720, P1080];
}

public sealed record ReviewFilter
{
    public string? FileSubstring { get; init; }
    public string? Chip { get; init; }
    public CompositionKind? Composition { get; init; }
    public string? MomentName { get; init; }
    public string? ResolutionName { get; init; }
}

public sealed record ReviewGeneratorOptions
{
    public required string ManifestPath { get; init; }
    public required string CorpusPath { get; init; }
    public required string OutputPath { get; init; }
    public required IVisualizationPreviewSessionFactory PreviewSessions { get; init; }
    public ReviewFilter Filter { get; init; } = new();
    public bool KeepExisting { get; init; }
    public bool AllowMissingChips { get; init; }
}

public sealed record ReviewCaseResult
{
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    public required ReviewMoment Moment { get; init; }
    public required CompositionKind Composition { get; init; }
    public required ReviewResolution Resolution { get; init; }
    public required ReviewCaseStatus Status { get; init; }
    public string? ImagePath { get; init; }
    public string? Reason { get; init; }
    public string? Error { get; init; }
    public double? CapturedDurationSeconds { get; init; }
    public IReadOnlyList<string> Chips { get; init; } = Array.Empty<string>();
}

public sealed record ReviewSourceResult
{
    public required string Id { get; init; }
    public required ReviewFileEntry Entry { get; init; }
    public required string ResolvedPath { get; init; }
    public VisualizationInputInfo? Inspection { get; init; }
    public IReadOnlyList<string> DetectedChips { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActiveChips { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ReviewCaseResult> Cases { get; init; } = Array.Empty<ReviewCaseResult>();
    public double? CapturedDurationSeconds { get; init; }
    public string? Error { get; init; }
}

public sealed record ReviewResult
{
    public required string OutputPath { get; init; }
    public IReadOnlyList<ReviewSourceResult> Sources { get; init; } = Array.Empty<ReviewSourceResult>();
    public IReadOnlyList<string> DetectedChips { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoveredChips { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingChips { get; init; } = Array.Empty<string>();
    public int Files => Sources.Count;
    public int Moments => Sources.Sum(source => source.Entry.Moments.Count);
    public int Cases => Sources.Sum(source => source.Cases.Count);
    public int Rendered => Sources.Sum(source => source.Cases.Count(item => item.Status == ReviewCaseStatus.Rendered));
    public int Unavailable => Sources.Sum(source => source.Cases.Count(item => item.Status == ReviewCaseStatus.Unavailable));
    public int Failed => Sources.Sum(source => source.Cases.Count(item => item.Status == ReviewCaseStatus.Failed));
    public int ExitCode { get; init; }
}

/// <summary>Names the chips registered by the production decoder registry.</summary>
public static class ReviewChipCatalog
{
    public static IReadOnlyList<string> SupportedChipNames
        => ChipTimelineDecoderRegistry.CreateDefault().SupportedChipNames
            .Select(Format)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    public static string Format(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";
        string raw = value.Split('.', 2)[0];
        if (Enum.TryParse(raw, true, out ChipType chip))
            raw = chip.ToString();
        return Regex.Replace(raw, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
    }

    public static bool Matches(string? value, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || string.Equals(Format(value), Format(filter), StringComparison.OrdinalIgnoreCase);
}
