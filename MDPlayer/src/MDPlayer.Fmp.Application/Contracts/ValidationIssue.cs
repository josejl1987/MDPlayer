namespace Fmp.Application.Contracts;

/// <summary>
/// A validation finding with a stable machine-readable code. Shared by the
/// request validator, layout validation, tool resolution and runtime stages.
/// </summary>
public sealed record ValidationIssue
{
    public required string Code { get; init; }
    public required ValidationSeverity Severity { get; init; }
    public required string Message { get; init; }

    public string? SettingPath { get; init; }
    public string? Detail { get; init; }
    public string? SuggestedAction { get; init; }
}

/// <summary>Well-known stable error/validation codes (see spec §27.4).</summary>
public static class ValidationCodes
{
    public const string InputNotFound = "INPUT_NOT_FOUND";
    public const string InputUnsupported = "INPUT_UNSUPPORTED";
    public const string CaptureFailed = "CAPTURE_FAILED";
    public const string LayoutTooSmall = "LAYOUT_TOO_SMALL";
    public const string FontGlyphMissing = "FONT_GLYPH_MISSING";
    public const string CorrscopeNotFound = "CORRSCOPE_NOT_FOUND";
    public const string FfmpegNotFound = "FFMPEG_NOT_FOUND";
    public const string EncoderUnavailable = "ENCODER_UNAVAILABLE";
    public const string OutputExists = "OUTPUT_EXISTS";
    public const string PreviewCancelled = "PREVIEW_CANCELLED";
    public const string AnalysisFailed = "ANALYSIS_FAILED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnsupportedSchema = "UNSUPPORTED_SCHEMA";
    public const string OutputOverlapsInput = "OUTPUT_OVERLAPS_INPUT";
    public const string NoRenderableContent = "NO_RENDERABLE_CONTENT";
    public const string AnalysisEnvironmentMissing = "ANALYSIS_ENVIRONMENT_MISSING";
    public const string MissingStems = "MISSING_STEMS";
    public const string ToolNotFound = "TOOL_NOT_FOUND";

    /// <summary>Full diagnostic panels could not fit; a simpler layout is used.</summary>
    public const string DiagnosticOverviewFallback = "DIAGNOSTIC_OVERVIEW_FALLBACK";
    /// <summary>Individual channel panels could not fit; channels are grouped by device.</summary>
    public const string DeviceOverviewFallback = "DEVICE_OVERVIEW_FALLBACK";
}
