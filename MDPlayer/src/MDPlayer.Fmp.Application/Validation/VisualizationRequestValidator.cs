using Fmp.Application.Contracts;

namespace Fmp.Application.Validation;

/// <summary>
/// Request-level validation. Mirrors the CLI's numeric validation exactly and
/// adds the spec's project-level checks (input exists, output != input,
/// schema version, output path present). Layout-vs-timeline validation needs a
/// captured timeline and lives in the planner; it is reported through the same
/// <see cref="ValidationIssue"/> shape.
/// </summary>
public static class VisualizationRequestValidator
{
    public const int MaxSchemaVersion = 1;

    public static IReadOnlyList<ValidationIssue> Validate(VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var issues = new List<ValidationIssue>();

        if (request.SchemaVersion > MaxSchemaVersion)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.UnsupportedSchema,
                Severity = ValidationSeverity.Error,
                Message = $"Project schema {request.SchemaVersion} is newer than this build supports ({MaxSchemaVersion}).",
                SettingPath = nameof(request.SchemaVersion),
                SuggestedAction = "Open read-only or upgrade the application.",
            });
            return issues;
        }

        if (string.IsNullOrWhiteSpace(request.InputPath))
            issues.Add(Error(ValidationCodes.InvalidRequest, "No input file is selected.", "InputPath"));
        else if (!File.Exists(request.InputPath))
            issues.Add(Error(ValidationCodes.InputNotFound, $"Input file not found: {request.InputPath}", "InputPath",
                "Relink the input file or choose another."));

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            issues.Add(Error(ValidationCodes.InvalidRequest, "No output path is set.", "OutputPath",
                "Choose an output video path in Output settings."));
        }
        else if (!string.IsNullOrWhiteSpace(request.InputPath)
                 && PathsEqual(request.OutputPath, request.InputPath))
        {
            issues.Add(Error(ValidationCodes.OutputOverlapsInput,
                "The output path must not overwrite the source music file.", "OutputPath",
                "Choose a different output path."));
        }
        else if (!string.IsNullOrWhiteSpace(request.InputPath))
        {
            // Security/UX: an output outside the input's directory tree (e.g.
            // from an untrusted project file) must never be written silently.
            string? outputDir = SafeDirectoryName(request.OutputPath);
            string? inputDir = SafeDirectoryName(request.InputPath);
            if (outputDir is not null && inputDir is not null
                && !PathEquals(outputDir, inputDir)
                && !PathEquals(outputDir, DefaultOutputDirectory(request.InputPath)))
            {
                issues.Add(new ValidationIssue
                {
                    Code = ValidationCodes.OutputOverlapsInput,
                    Severity = ValidationSeverity.Warning,
                    Message = "The output path is outside the input's directory. Confirm this location before rendering.",
                    SettingPath = nameof(request.OutputPath),
                    SuggestedAction = "Choose an output next to the input, or confirm the custom location.",
                });
            }
        }

        // Numeric validation — mirrors VisualizeOptionsParser's rules exactly.
        if (request.Width < 480)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Width {request.Width} is below the renderer minimum of 480.", "Width"));
        if (request.Height < 270)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Height {request.Height} is below the renderer minimum of 270.", "Height"));
        if (request.FpsNumerator <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Frame rate numerator must be positive (got {request.FpsNumerator}).", "FpsNumerator"));
        if (request.FpsDenominator <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Frame rate denominator must be positive (got {request.FpsDenominator}).", "FpsDenominator"));

        if (!double.IsFinite(request.PastSeconds) || request.PastSeconds is < 0.1 or > 15)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Past seconds must be in [0.1, 15] (got {request.PastSeconds}).", "PastSeconds"));
        if (!double.IsFinite(request.FutureSeconds) || request.FutureSeconds is < 0.1 or > 15)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Future seconds must be in [0.1, 15] (got {request.FutureSeconds}).", "FutureSeconds"));
        if (request.PastSeconds + request.FutureSeconds is < 0.5 or > 20)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                "The time window (past + future) must be in [0.5, 20] seconds.", "PastSeconds"));

        if (!double.IsFinite(request.RollZoom) || request.RollZoom <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Roll zoom must be positive (got {request.RollZoom}).", "RollZoom"));
        if (request.ScopeRatio is < 0 or > 0.8)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Scope ratio must be in [0, 0.8] (got {request.ScopeRatio}).", "ScopeRatio"));

        if (request.LoopCount <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Loop count must be positive (got {request.LoopCount}).", "LoopCount"));
        if (request.FadeSeconds < 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Fade seconds must be non-negative (got {request.FadeSeconds}).", "FadeSeconds"));
        if (request.TailSeconds < 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Tail seconds must be non-negative (got {request.TailSeconds}).", "TailSeconds"));
        if (request.MaximumDurationSeconds is <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Maximum duration must be positive (got {request.MaximumDurationSeconds}).", "MaximumDurationSeconds"));
        if (request.TimeoutSeconds is <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Playback timeout must be positive (got {request.TimeoutSeconds}).", "TimeoutSeconds"));
        if (request.SampleRate <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, $"Sample rate must be positive (got {request.SampleRate}).", "SampleRate"));
        if (request.Tools.AnalysisTimeoutMinutes is <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, "Analysis timeout must be positive.", "Tools"));
        if (request.Tools.ToolTimeoutMinutes is <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest, "External tool timeout must be positive.", "Tools"));

        if (request.ChannelSelection == ChannelSelectionMode.Custom
            && request.IncludedTrackIds.Count == 0 && request.ExcludedTrackIds.Count == 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.InvalidRequest,
                Severity = ValidationSeverity.Warning,
                Message = "Custom channel mode is selected but no tracks are included or excluded.",
                SettingPath = nameof(request.ChannelSelection),
                SuggestedAction = "Toggle at least one track in the channel list.",
            });
        }

        if (request.AnalysisOverlay != AnalysisOverlayMode.None && !request.AnalysisEnabled)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.AnalysisFailed,
                Severity = ValidationSeverity.Warning,
                Message = "An analysis overlay is selected but analysis is disabled.",
                SettingPath = nameof(request.AnalysisOverlay),
                SuggestedAction = "Enable analysis or set the overlay to None.",
            });
        }

        return issues;
    }

    /// <summary>True when there are no Error-severity issues.</summary>
    public static bool IsValid(IReadOnlyList<ValidationIssue> issues)
        => issues.All(issue => issue.Severity != ValidationSeverity.Error);

    private static ValidationIssue Error(string code, string message, string? settingPath, string? action = null)
        => new()
        {
            Code = code,
            Severity = ValidationSeverity.Error,
            Message = message,
            SettingPath = settingPath,
            SuggestedAction = action,
        };

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeDirectoryName(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return Path.GetDirectoryName(full) ?? full;
        }
        catch
        {
            return null;
        }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? DefaultOutputDirectory(string inputPath)
    {
        try
        {
            string full = Path.GetFullPath(inputPath);
            string? directory = Path.GetDirectoryName(full) ?? ".";
            return Path.Combine(directory, Path.GetFileNameWithoutExtension(full) + ".visualization");
        }
        catch
        {
            return null;
        }
    }
}
