namespace Fmp.Application.Contracts;

/// <summary>Export pipeline stages (ordered).</summary>
public enum ExportStage
{
    PreparingInput,
    CapturingSemanticTimeline,
    RunningAnalysis,
    RenderingChannelStems,
    AnalyzingEnergy,
    PreparingScopes,
    ComposingFrames,
    EncodingVideo,
    FinalizingOutput,
}

/// <summary>Structured progress event types (CLI `--progress jsonl` and GUI model).</summary>
public static class ExportEventTypes
{
    public const string Started = "started";
    public const string StageStarted = "stage-started";
    public const string StageProgress = "stage-progress";
    public const string StageCompleted = "stage-completed";
    public const string Warning = "warning";
    public const string EncoderSelected = "encoder-selected";
    public const string OutputCreated = "output-created";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One JSON-lines progress event. Serialized by the CLI with camelCase names;
/// the GUI parses the same shape.
/// </summary>
public sealed record ExportProgressEvent
{
    public required string Type { get; init; }
    public double? TimestampUtc { get; init; }
    public string? Stage { get; init; }
    public double? Progress { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public string? OutputPath { get; init; }
    public string? Encoder { get; init; }
    public long? FramesCompleted { get; init; }
    public double? ElapsedSeconds { get; init; }
    public int? ExitCode { get; init; }
}
