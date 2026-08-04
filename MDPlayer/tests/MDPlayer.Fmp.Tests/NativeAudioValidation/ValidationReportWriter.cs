using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fmp.Core.Rendering.NativeAudioValidation;

namespace MDPlayer.Fmp.Tests.NativeAudioValidation;

/// <summary>
/// Serialization of the native-audio validation reports. JSON is authoritative
/// (single canonical writer); Markdown is a human-readable summary. This is an
/// internal validation helper — never invoked during ordinary production
/// rendering, and it does not define a general public trace-export format.
/// </summary>
internal static class ValidationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializes a full per-fixture validation result to JSON.</summary>
    public static string ToJson(FixtureValidationResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    /// <summary>
    /// Serializes a batch of per-fixture results to one authoritative JSON
    /// document with a leading summary object.
    /// </summary>
    public static string ToJson(IReadOnlyList<FixtureValidationResult> results, ValidationRunSummary summary)
        => JsonSerializer.Serialize(new { summary, fixtures = results }, JsonOptions);

    /// <summary>Clean ANSI-free, newline-safe value for Markdown tables.</summary>
    private static string Cell(object o)
    {
        string s = o?.ToString() ?? "";
        s = Regex.Replace(s, @"[\r\n]+", " ");
        if (s.Length > 60) s = s[..57] + "...";
        return s.Replace("|", "\\|");
    }

    /// <summary>Builds a human-readable Markdown summary.</summary>
    public static string ToMarkdown(ValidationRunSummary summary, IReadOnlyList<FixtureValidationResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# native-audio FMP validation report");
        sb.AppendLine();
        sb.AppendLine($"- Date/time: {summary.RanAtUtc:O}");
        sb.AppendLine($"- Tier: `{summary.Tier}`  (`fast` / `extended`)");
        sb.AppendLine($"- Result: **{summary.Result}**");
        sb.AppendLine($"- Fixtures executed: {summary.FixturesExecuted} / {summary.FixturesTotal}");
        sb.AppendLine($"- Fixtures passed: {summary.FixturesPassed}");
        sb.AppendLine($"- Total duration: {summary.TotalDurationSeconds:F2} s");
        sb.AppendLine($"- Aggregate allocated bytes: {summary.TotalAllocatedBytes}");
        sb.AppendLine();
        sb.AppendLine("## Per-fixture summary");
        sb.AppendLine();
        sb.AppendLine("| fixture | rate | frames | capture sha | pcm sha | result |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {Cell(r.FixtureId)} | {r.OutputRate} | {r.FrameCount:N0} | " +
                          $"{Cell(r.CaptureHash)} | {Cell(r.PcmHash)} | {Cell(r.Result)} |");
        }
        return sb.ToString();
    }
}

/// <summary>Aggregate over one validation run.</summary>
internal sealed class ValidationRunSummary
{
    /// <summary>Report timestamp — presentational only; never part of a capture hash.</summary>
    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;
    public string Tier { get; set; }
    public string Result { get; set; }
    public int FixturesExecuted { get; set; }
    public int FixturesTotal { get; set; }
    public int FixturesPassed { get; set; }
    public double TotalDurationSeconds { get; set; }
    public long TotalAllocatedBytes { get; set; }
}
