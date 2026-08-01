using Fmp.Application.Contracts;

namespace Fmp.Application.Validation;

/// <summary>
/// Request-level validation for the final schema 2 contract. Layout-vs-timeline
/// validation needs a captured timeline and lives in the planner; it is
/// reported through the same <see cref="ValidationIssue"/> shape.
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
                Message =
                    $"Project schema {request.SchemaVersion} is newer than this build supports ({MaxSchemaVersion}).",
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

        // Output profile validation.
        OutputSettings output = request.Output;
        if (output.Width < 480)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Width {output.Width} is below the renderer minimum of 480.", "Output.Width"));
        if (output.Height < 270)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Height {output.Height} is below the renderer minimum of 270.", "Output.Height"));
        if (output.FpsNumerator <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Frame rate numerator must be positive (got {output.FpsNumerator}).", "Output.FpsNumerator"));
        if (output.FpsDenominator <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Frame rate denominator must be positive (got {output.FpsDenominator}).", "Output.FpsDenominator"));

        // View settings (time window).
        double past = request.View.PastSeconds;
        double future = request.View.FutureSeconds;
        if (!double.IsFinite(past) || past is < 0.2 or > 15)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Past seconds must be in [0.2, 15] (got {past}).", "View.PastSeconds"));
        if (!double.IsFinite(future) || future is < 0.1 or > 15)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Future seconds must be in [0.1, 15] (got {future}).", "View.FutureSeconds"));
        if (past + future is < 0.5 or > 20)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                "The time window (past + future) must be in [0.5, 20] seconds.", "View.PastSeconds"));

        // Track settings.
        if (request.Tracks.Selection == TrackSelectionMode.Custom
            && request.Tracks.IncludedIds.Count == 0 && request.Tracks.ExcludedIds.Count == 0)
        {
            issues.Add(Error(ValidationCodes.InvalidRequest,
                "Custom track selection is set but no tracks are included or excluded.", "Tracks.Selection",
                "Toggle at least one track in the track list."));
        }

        // Playback settings.
        PlaybackSettings playback = request.Playback;
        if (playback.LoopCount <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Loop count must be positive (got {playback.LoopCount}).", "Playback.LoopCount"));
        if (playback.FadeSeconds < 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Fade seconds must be non-negative (got {playback.FadeSeconds}).", "Playback.FadeSeconds"));
        if (playback.TailSeconds < 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Tail seconds must be non-negative (got {playback.TailSeconds}).", "Playback.TailSeconds"));
        if (playback.MaximumDurationSeconds is <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Maximum duration must be positive (got {playback.MaximumDurationSeconds}).", "Playback.MaximumDurationSeconds"));
        if (playback.SampleRate <= 0)
            issues.Add(Error(ValidationCodes.InvalidRequest,
                $"Sample rate must be positive (got {playback.SampleRate}).", "Playback.SampleRate"));

        return issues;
    }

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