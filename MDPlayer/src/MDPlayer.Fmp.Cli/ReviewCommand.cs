using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Application.Review;

namespace Fmp.Cli;

/// <summary>Generates the real-file visual review gallery.</summary>
public static class ReviewCommand
{
    public static int Handle(string[] args)
    {
        try
        {
            ReviewCliOptions options = Parse(args ?? Array.Empty<string>());
            string corpus = ResolveCorpus(options.Corpus);
            var generator = new ReviewGeneratorOptions
            {
                ManifestPath = Path.GetFullPath(options.Manifest),
                CorpusPath = corpus,
                OutputPath = Path.GetFullPath(options.Output),
                KeepExisting = options.KeepExisting,
                AllowMissingChips = options.AllowMissingChips,
                Filter = new ReviewFilter
                {
                    FileSubstring = options.File,
                    Chip = options.Chip,
                    Composition = options.Composition,
                    MomentName = options.Moment,
                    ResolutionName = options.Resolution,
                },
                PreviewSessions = new InProcessVisualizationPreviewSessionFactory(
                    BuildRuntime()),
            };

            ReviewResult result = new ReviewGenerator().GenerateAsync(generator).GetAwaiter().GetResult();
            Console.WriteLine($"Review output: {result.OutputPath}");
            Console.WriteLine($"Files: {result.Files}; moments: {result.Moments}; cases: {result.Cases}");
            Console.WriteLine($"Rendered: {result.Rendered}; unavailable: {result.Unavailable}; failed: {result.Failed}");
            if (result.MissingChips.Count > 0)
                Console.WriteLine($"Missing chips: {string.Join(", ", result.MissingChips)}");
            return result.ExitCode;
        }
        catch (ReviewManifestException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: review failed — {ex.Message}");
            return 1;
        }
    }

    private static ReviewCliOptions Parse(string[] args)
    {
        var options = new ReviewCliOptions();
        var reader = new ArgumentReader(args);
        while (reader.HasMore)
        {
            if (!reader.TryReadOption(out string name, out string value))
                throw new ArgumentException($"unexpected argument '{reader.Next()}'");
            switch (name)
            {
                case "--manifest": options.Manifest = reader.RequireValue(name); break;
                case "--corpus": options.Corpus = reader.RequireValue(name); break;
                case "--output": options.Output = reader.RequireValue(name); break;
                case "--file": options.File = reader.RequireValue(name); break;
                case "--chip": options.Chip = reader.RequireValue(name); break;
                case "--moment": options.Moment = reader.RequireValue(name); break;
                case "--composition": options.Composition = ParseComposition(reader.RequireValue(name)); break;
                case "--resolution": options.Resolution = ParseResolution(reader.RequireValue(name)); break;
                case "--keep-existing" when value == null: options.KeepExisting = true; break;
                case "--allow-missing-chips" when value == null: options.AllowMissingChips = true; break;
                default: throw new ArgumentException($"unknown option '{name}'");
            }
        }
        if (string.IsNullOrWhiteSpace(options.Manifest))
            throw new ArgumentException("--manifest PATH is required");
        if (string.IsNullOrWhiteSpace(options.Output))
            throw new ArgumentException("--output PATH is required");
        return options;
    }

    private static string ResolveCorpus(string explicitCorpus)
    {
        if (!string.IsNullOrWhiteSpace(explicitCorpus))
            return explicitCorpus;
        string environment = Environment.GetEnvironmentVariable("MDPLAYER_REVIEW_CORPUS");
        return !string.IsNullOrWhiteSpace(environment)
            ? environment
            : Path.Combine(Environment.CurrentDirectory, "visual-review", "corpus");
    }

    private static CompositionKind ParseComposition(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "diagnostic" => CompositionKind.Diagnostic,
        // Canonical spelling; "miditrail" remains accepted for existing scripts.
        "performance" => CompositionKind.Performance,
        "miditrail" => CompositionKind.Performance,
        _ => throw new ArgumentException("unknown composition (expected diagnostic or performance)"),
    };

    private static string ParseResolution(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "720p" => "720p",
        "1080p" => "1080p",
        _ => throw new ArgumentException("unknown resolution (expected 720p or 1080p)"),
    };

    private sealed class ReviewCliOptions
    {
        public string Manifest;
        public string Corpus;
        public string Output;
        public string File;
        public string Chip;
        public string Moment;
        public CompositionKind? Composition;
        public string Resolution;
        public bool KeepExisting;
        public bool AllowMissingChips;
    }

    private static RenderRuntimeOptions BuildRuntime()
    {
        // Review uses default tool-path resolution (FMP.COM next to the CLI,
        // Corrscope/python/ffmpeg on PATH). Explicit tool paths could be added
        // to ReviewCliOptions in a future change.
        return new RenderRuntimeOptions();
    }

    private sealed class InProcessReviewPreviewSessionFactory : IVisualizationPreviewSessionFactory
    {
        public Task<IVisualizationPreviewSession> OpenAsync(string inputPath, CancellationToken cancellationToken)
            => new InProcessVisualizationPreviewSessionFactory(BuildRuntime()).OpenAsync(inputPath, cancellationToken);
    }
}
