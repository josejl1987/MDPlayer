using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
using Fmp.Application.Inspection;
using Fmp.Application.Preview;

namespace Fmp.Application.Review;

/// <summary>
/// Thin batch wrapper around the production inspector and preview session.
/// It owns review output and presentation only; capture, planning and frame
/// rendering remain session responsibilities.
/// </summary>
public sealed class ReviewGenerator
{
    private readonly StringBuilder _log = new();

    public async Task<ReviewResult> GenerateAsync(
        ReviewGeneratorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ReviewManifest manifest = ReviewManifestReader.Read(options.ManifestPath);
        string corpus = Path.GetFullPath(options.CorpusPath);
        string output = Path.GetFullPath(options.OutputPath);
        PrepareOutputDirectory(output, options.KeepExisting);

        var usedIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var sources = new List<ReviewSourceResult>();
        foreach (ReviewFileEntry entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesFile(entry, options.Filter))
                continue;

            string id = CreateUniqueId(Path.GetFileNameWithoutExtension(entry.Path), usedIds);
            ReviewSourceResult source = await ProcessSourceAsync(
                entry, id, corpus, output, options, cancellationToken);
            sources.Add(source);
        }

        string[] detectedChips = sources
            .SelectMany(source => source.DetectedChips.Concat(source.ActiveChips))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(chip => chip, StringComparer.Ordinal)
            .ToArray();
        string[] coveredChips = sources
            .SelectMany(source => source.ActiveChips)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(chip => chip, StringComparer.Ordinal)
            .ToArray();
        string[] missingChips = ReviewChipCatalog.SupportedChipNames
            .Where(chip => !coveredChips.Contains(chip, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var result = new ReviewResult
        {
            OutputPath = output,
            Sources = sources,
            DetectedChips = detectedChips,
            CoveredChips = coveredChips,
            MissingChips = missingChips,
            ExitCode = sources.Any(source => source.Cases.Any(item => item.Status == ReviewCaseStatus.Failed))
                ? 1
                : missingChips.Length > 0 && !options.AllowMissingChips ? 3 : 0,
        };

        ContactSheetWriter.WriteAll(result);
        ReviewHtmlWriter.Write(result);
        WriteSummary(result);
        if (_log.Length > 0)
            File.WriteAllText(Path.Combine(output, "review.log"), _log.ToString());
        return result;
    }

    private async Task<ReviewSourceResult> ProcessSourceAsync(
        ReviewFileEntry entry,
        string id,
        string corpus,
        string output,
        ReviewGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        string resolvedPath = Path.GetFullPath(Path.Combine(corpus, entry.Path));
        VisualizationInputInfo inspection = await VisualizationInputInspector.InspectAsync(
            resolvedPath, cancellationToken);
        WriteInspection(output, id, inspection);

        string[] detectedChips = inspection.Devices
            .Select(device => ReviewChipCatalog.Format(device.Type ?? device.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(chip => chip, StringComparer.Ordinal)
            .ToArray();
        ReviewCaseSpec[] cases = CasesFor(entry, id, resolvedPath, options.Filter).ToArray();
        if (cases.Length == 0)
        {
            return new ReviewSourceResult
            {
                Id = id,
                Entry = entry,
                ResolvedPath = resolvedPath,
                Inspection = inspection,
                DetectedChips = detectedChips,
            };
        }

        if (!File.Exists(resolvedPath))
        {
            return CompleteSourceWithFailure(
                entry, id, resolvedPath, inspection, detectedChips, cases,
                "MISSING FILE", $"{entry.Path}");
        }

        if (inspection.Issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            return CompleteSourceWithFailure(
                entry, id, resolvedPath, inspection, detectedChips, cases,
                "INSPECTION FAILED", FirstError(inspection.Issues));
        }

        var activeChips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ReviewCaseResult>(cases.Length);
        IVisualizationPreviewSession? session = null;
        try
        {
            session = await options.PreviewSessions.OpenAsync(resolvedPath, cancellationToken);
            foreach (ReviewCaseSpec spec in cases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReviewCaseResult result = await RenderCaseAsync(
                    session, spec, output, options.Filter, activeChips, cancellationToken);
                results.Add(result);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(resolvedPath, ex);
            results.AddRange(cases.Skip(results.Count).Select(spec => FailedCase(
                spec, "Preview session failed; see review.log", ex.Message)));
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }

        return new ReviewSourceResult
        {
            Id = id,
            Entry = entry,
            ResolvedPath = resolvedPath,
            Inspection = inspection,
            DetectedChips = detectedChips.Concat(activeChips)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(chip => chip, StringComparer.Ordinal)
                .ToArray(),
            ActiveChips = activeChips.OrderBy(chip => chip, StringComparer.Ordinal).ToArray(),
            CapturedDurationSeconds = results.Select(item => item.CapturedDurationSeconds)
                .FirstOrDefault(value => value is > 0),
            Cases = results,
        };
    }

    private async Task<ReviewCaseResult> RenderCaseAsync(
        IVisualizationPreviewSession session,
        ReviewCaseSpec spec,
        string output,
        ReviewFilter filter,
        HashSet<string> activeChips,
        CancellationToken cancellationToken)
    {
        double? capturedDuration = null;
        ReviewCaseResult Finish(ReviewCaseResult item) => item with
        {
            CapturedDurationSeconds = capturedDuration,
        };
        if (spec.Moment.TimeSeconds < 0)
            return Finish(UnavailableCase(spec, "INVALID MOMENT", "Configured time must not be negative."));

        string pngRelativePath = Path.Combine(
            "files", spec.SourceId, Slug(spec.Moment.Name),
            $"{CompositionName(spec.Composition)}-{spec.Resolution.Name}.png");
        string pngPath = Path.Combine(output, pngRelativePath);
        VisualizationRequest request = CreateReviewRequest(
            spec.InputPath, spec.Composition, spec.Resolution, pngPath);

        try
        {
            VisualizationPlanResult plan = await session.PlanAsync(request, cancellationToken);
            capturedDuration = plan.EstimatedDurationSeconds;
            AddActiveChips(plan, activeChips);
            if (filter.Chip is not null
                && !plan.Tracks.Any(track => track.ActivityDetected
                    && ReviewChipCatalog.Matches(track.DeviceFamily, filter.Chip)))
            {
                return Finish(UnavailableCase(spec, "FILTERED", $"No active {filter.Chip} track in this source."));
            }

            if (plan.EstimatedDurationSeconds is > 0
                && spec.Moment.TimeSeconds > plan.EstimatedDurationSeconds.Value)
            {
                return Finish(UnavailableCase(spec, "INVALID MOMENT", string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Configured time {0:0.###} s exceeds captured duration {1:0.###} s.",
                    spec.Moment.TimeSeconds, plan.EstimatedDurationSeconds.Value)));
            }

            ValidationIssue? error = plan.ValidationIssues
                .FirstOrDefault(issue => issue.Severity == ValidationSeverity.Error);
            if (error is not null)
                return Finish(UnavailableCase(spec, "UNAVAILABLE", error.Message));
            string requestedComposition = CompositionName(spec.Composition);
            if (!string.Equals(plan.RequestedLayout, requestedComposition, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(plan.ResolvedLayout, requestedComposition, StringComparison.OrdinalIgnoreCase))
            {
                return Finish(UnavailableCase(spec, "UNAVAILABLE",
                    $"Requested composition resolved as '{plan.ResolvedLayout}'."));
            }
            PreviewFrameResult frame = await session.RenderFrameAsync(request, new PreviewFrameRequest
            {
                TimeSeconds = spec.Moment.TimeSeconds,
                Width = spec.Resolution.Width,
                Height = spec.Resolution.Height,
                Fidelity = PreviewFidelity.AccurateStill,
            }, cancellationToken);
            if (frame.HasApproximations
                && frame.ApproximationNotes.Any(note => note.Contains("scope", StringComparison.OrdinalIgnoreCase)))
            {
                return Finish(UnavailableCase(spec, "SCOPE STAGE UNAVAILABLE",
                    string.Join(" ", frame.ApproximationNotes)));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
            await File.WriteAllBytesAsync(pngPath, frame.PngBytes, cancellationToken);
            return Finish(new ReviewCaseResult
            {
                SourceId = spec.SourceId,
                SourceName = spec.SourceName,
                Moment = spec.Moment,
                Composition = spec.Composition,
                Resolution = spec.Resolution,
                Status = ReviewCaseStatus.Rendered,
                ImagePath = ToWebPath(pngRelativePath),
                Chips = activeChips.OrderBy(chip => chip, StringComparer.Ordinal).ToArray(),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.Message.StartsWith("Scope Stage unavailable:", StringComparison.OrdinalIgnoreCase))
            {
                return Finish(UnavailableCase(spec, "SCOPE STAGE UNAVAILABLE", ex.Message));
            }
            LogFailure($"{spec.SourceName} / {spec.Moment.Name} / {CompositionName(spec.Composition)} / {spec.Resolution.Name}", ex);
            return Finish(FailedCase(spec, "Rendering failed; see review.log", ex.Message));
        }
    }

    private static VisualizationRequest CreateReviewRequest(
        string sourceId,
        CompositionKind composition,
        ReviewResolution resolution,
        string outputPath)
        => new()
        {
            InputPath = sourceId,
            OutputPath = outputPath,
            Composition = composition,
            Output = new OutputSettings
            {
                Quality = RenderQuality.Standard,
                Width = resolution.Width,
                Height = resolution.Height,
                FpsNumerator = 60,
                FpsDenominator = 1,
                Encoder = VideoEncoder.Auto,
                Overwrite = true,
            },
            Style = new StyleSettings
            {
                Effects = VisualEffects.Subtle,
                NoteColor = NoteColorMode.Instrument,
                Palette = PaletteKind.Default,
            },
            View = new ViewSettings
            {
                TimeGrid = TimeGridMode.Automatic,
            },
        };

    private static IEnumerable<ReviewCaseSpec> CasesFor(
        ReviewFileEntry entry,
        string sourceId,
        string sourcePath,
        ReviewFilter filter)
    {
        IEnumerable<ReviewMoment> moments = entry.Moments;
        if (!string.IsNullOrWhiteSpace(filter.MomentName))
            moments = moments.Where(moment => string.Equals(
                moment.Name, filter.MomentName, StringComparison.OrdinalIgnoreCase));

        IEnumerable<CompositionKind> compositions = Enum.GetValues<CompositionKind>();
        if (filter.Composition is CompositionKind composition)
            compositions = [composition];

        IEnumerable<ReviewResolution> resolutions = ReviewResolution.All;
        if (!string.IsNullOrWhiteSpace(filter.ResolutionName))
            resolutions = resolutions.Where(resolution => string.Equals(
                resolution.Name, filter.ResolutionName, StringComparison.OrdinalIgnoreCase));

        return from moment in moments
               from kind in compositions
               from resolution in resolutions
               select new ReviewCaseSpec(
                   sourceId,
                   sourcePath,
                   entry.Label ?? Path.GetFileName(entry.Path),
                   moment,
                   kind,
                   resolution);
    }

    private static bool MatchesFile(ReviewFileEntry entry, ReviewFilter filter)
    {
        if (string.IsNullOrWhiteSpace(filter.FileSubstring))
            return true;
        string value = $"{entry.Path} {entry.Label}";
        return value.Contains(filter.FileSubstring, StringComparison.OrdinalIgnoreCase);
    }

    private static ReviewSourceResult CompleteSourceWithFailure(
        ReviewFileEntry entry,
        string id,
        string resolvedPath,
        VisualizationInputInfo inspection,
        IReadOnlyList<string> detectedChips,
        IEnumerable<ReviewCaseSpec> cases,
        string title,
        string reason)
    {
        string displayReason = $"{title}\n{reason}";
        return new ReviewSourceResult
        {
            Id = id,
            Entry = entry,
            ResolvedPath = resolvedPath,
            Inspection = inspection,
            DetectedChips = detectedChips,
            Error = displayReason,
            Cases = cases.Select(spec => FailedCase(spec, displayReason, reason)).ToArray(),
        };
    }

    private static ReviewCaseResult FailedCase(ReviewCaseSpec spec, string reason, string? error = null)
        => new()
        {
            SourceId = spec.SourceId,
            SourceName = spec.SourceName,
            Moment = spec.Moment,
            Composition = spec.Composition,
            Resolution = spec.Resolution,
            Status = ReviewCaseStatus.Failed,
            Reason = reason,
            Error = error,
        };

    private static ReviewCaseResult UnavailableCase(ReviewCaseSpec spec, string title, string reason)
        => new()
        {
            SourceId = spec.SourceId,
            SourceName = spec.SourceName,
            Moment = spec.Moment,
            Composition = spec.Composition,
            Resolution = spec.Resolution,
            Status = ReviewCaseStatus.Unavailable,
            Reason = $"{title}: {reason}",
        };

    private static void AddActiveChips(VisualizationPlanResult plan, HashSet<string> chips)
    {
        foreach (TrackSelectionInfo track in plan.Tracks.Where(track => track.ActivityDetected))
        {
            if (!string.IsNullOrWhiteSpace(track.DeviceFamily))
                chips.Add(ReviewChipCatalog.Format(track.DeviceFamily));
        }
    }

    private static string CreateUniqueId(string? fileName, Dictionary<string, int> used)
    {
        string baseId = Slug(fileName);
        if (!used.TryGetValue(baseId, out int count))
        {
            used[baseId] = 1;
            return baseId;
        }
        count++;
        used[baseId] = count;
        return $"{baseId}-{count}";
    }

    internal static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "file";
        var builder = new StringBuilder();
        bool separator = false;
        foreach (char character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && builder.Length > 0)
                    builder.Append('-');
                builder.Append(character);
                separator = false;
            }
            else
            {
                separator = true;
            }
        }
        return builder.ToString().Trim('-') is { Length: > 0 } result ? result : "file";
    }

    internal static string CompositionName(CompositionKind composition) => "diagnostic";

    private static void PrepareOutputDirectory(string output, bool keepExisting)
    {
        if (!keepExisting && Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
    }

    private static void WriteInspection(string output, string id, VisualizationInputInfo inspection)
    {
        string directory = Path.Combine(output, "files", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "inspection.json"), JsonSerializer.Serialize(inspection, JsonOptions));
    }

    private static void WriteSummary(ReviewResult result)
    {
        var summary = new
        {
            files = result.Files,
            moments = result.Moments,
            cases = result.Cases,
            rendered = result.Rendered,
            unavailable = result.Unavailable,
            failed = result.Failed,
            detectedChips = result.DetectedChips,
            coveredChips = result.CoveredChips,
            missingChips = result.MissingChips,
            failures = result.Sources.SelectMany(source => source.Cases)
                .Where(item => item.Status == ReviewCaseStatus.Failed)
                .Select(item => new
                {
                    source = item.SourceName,
                    moment = item.Moment.Name,
                    composition = CompositionName(item.Composition),
                    resolution = item.Resolution.Name,
                    error = item.Error,
                }).ToArray(),
        };
        File.WriteAllText(Path.Combine(result.OutputPath, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions));
    }

    private void LogFailure(string context, Exception exception)
    {
        _log.AppendLine($"[{DateTimeOffset.UtcNow:O}] {context}");
        _log.AppendLine(exception.ToString());
        _log.AppendLine();
    }

    private static string FirstError(IEnumerable<ValidationIssue> issues)
        => issues.FirstOrDefault(issue => !string.IsNullOrWhiteSpace(issue.Message))?.Message
            ?? "Unsupported or malformed input.";

    private static string ToWebPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record ReviewCaseSpec(
        string SourceId,
        string InputPath,
        string SourceName,
        ReviewMoment Moment,
        CompositionKind Composition,
        ReviewResolution Resolution);
}
