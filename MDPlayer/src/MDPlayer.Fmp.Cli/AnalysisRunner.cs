using System.Security.Cryptography;
using System.Text;
using Fmp.Core.Analysis;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

internal static class AnalysisRunner
{
    public static int Run(AnalyzeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string defaultOutputDir = options.Timeline != null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Timeline)) ?? ".", "analysis")
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Input)) ?? ".", Path.GetFileNameWithoutExtension(options.Input) + ".analysis");
        string configuredOutput = options.AnalysisOutput;
        bool outputIsFile = !string.IsNullOrWhiteSpace(configuredOutput)
            && string.Equals(Path.GetExtension(configuredOutput), ".json", StringComparison.OrdinalIgnoreCase);
        string outputDir = Path.GetFullPath(outputIsFile
            ? Path.GetDirectoryName(configuredOutput) ?? "."
            : configuredOutput ?? defaultOutputDir);
        Directory.CreateDirectory(outputDir);

        string inputPath = Path.Combine(outputDir, "input.json");
        string outputPath = outputIsFile
            ? Path.GetFullPath(configuredOutput)
            : Path.Combine(outputDir, "analysis.json");
        if (options.Timeline != null)
        {
            string timelinePath = Path.GetFullPath(options.Timeline);
            if (string.Equals(timelinePath, inputPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(timelinePath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("analysis output must not overwrite the input timeline");
        }
        string metadataPath = ResolveCacheMetadataPath(options.AnalysisCache, outputDir);
        string detail = options.Detail.ToString().ToLowerInvariant();

        VisualizationTimeline timeline = options.Timeline != null
            ? VisualizationJsonWriter.Read(options.Timeline)
            : CaptureTimeline(options);
        AnalysisInput input = FmpSymbolicNormalizer.Normalize(timeline);
        string inputHash = AnalysisCacheKey.ComputeInputHash(input);

        var expectedMetadata = new AnalysisCacheMetadata
        {
            SchemaVersion = 1,
            InputHash = inputHash,
            NormalizerVersion = FmpSymbolicNormalizer.Version,
            WorkerVersion = AnalysisResultValidator.ExpectedWorkerVersion,
            RequiredMusic21Version = AnalysisResultValidator.ExpectedMusic21Version,
            AnalysisDetail = detail,
        };

        if (!options.Force && TryReadValidCache(outputPath, metadataPath, input, expectedMetadata))
        {
            PrintSummary(AnalysisResultValidator.ReadAndValidate(outputPath, input));
            Console.WriteLine($"Analysis cache hit: {outputPath}");
            return 0;
        }

        WriteText(inputPath, AnalysisJson.Serialize(input));
        string repositoryRoot = FindRepositoryRoot();
        // Python is intentionally resolved only after the complete cache miss
        // path. A valid cache therefore works on machines without Python.
        AnalysisPython python = AnalysisPythonResolver.Resolve(options.AnalysisPython, repositoryRoot);
        string script = ResolveWorkerScript(repositoryRoot);
        string temporaryOutputPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N");
        string temporaryMetadataPath = metadataPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            AnalysisProcessResult process = AnalysisProcessRunner.Run(
                python.Path,
                script,
                inputPath,
                temporaryOutputPath,
                options.Detail,
                options.TimeoutMinutes);
            if (process.TimedOut)
                throw new InvalidOperationException($"analysis worker timed out after {options.TimeoutMinutes} minutes");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"analysis worker exited {process.ExitCode}: {process.StandardError}");

            AnalysisOutput validated = AnalysisResultValidator.ReadAndValidate(temporaryOutputPath, input);
            PrintSummary(validated);
            string outputHash = ComputeFileHash(temporaryOutputPath);
            var metadata = new AnalysisCacheMetadata
            {
                SchemaVersion = expectedMetadata.SchemaVersion,
                InputHash = expectedMetadata.InputHash,
                NormalizerVersion = expectedMetadata.NormalizerVersion,
                WorkerVersion = expectedMetadata.WorkerVersion,
                RequiredMusic21Version = expectedMetadata.RequiredMusic21Version,
                AnalysisDetail = expectedMetadata.AnalysisDetail,
                OutputHash = outputHash,
            };
            WriteText(temporaryMetadataPath, AnalysisJson.Serialize(metadata));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath) ?? ".");
            File.Move(temporaryOutputPath, outputPath, overwrite: true);
            File.Move(temporaryMetadataPath, metadataPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryOutputPath);
            TryDelete(temporaryMetadataPath);
        }

        Console.WriteLine($"Analysis written to: {outputPath}");
        return 0;
    }

    private static VisualizationTimeline CaptureTimeline(AnalyzeOptions options)
    {
        PreparedTrack track = TrackPreparation.Prepare(options.Input, options);
        var capture = new VisualizationPipeline(track.Assets, track.FileSystem, options.SampleRate).Capture(
            track.Data,
            track.Input.FullName,
            new VisualizationPipeline.Options
            {
                LoopCount = options.Loops,
                FadeSeconds = options.Fade,
                TailSeconds = options.Tail,
                MaxDurationSeconds = options.MaxDuration,
                TimeoutSeconds = options.Timeout,
            });
        if (!capture.Success || capture.Timeline == null)
            throw new InvalidOperationException($"visualization capture failed: {capture.LastError}");
        return capture.Timeline;
    }

    private static bool TryReadValidCache(
        string outputPath,
        string metadataPath,
        AnalysisInput input,
        AnalysisCacheMetadata expected)
    {
        try
        {
            if (!File.Exists(outputPath) || !File.Exists(metadataPath))
                return false;
            AnalysisCacheMetadata metadata = AnalysisJson.Deserialize<AnalysisCacheMetadata>(
                File.ReadAllText(metadataPath));
            if (metadata.SchemaVersion != expected.SchemaVersion
                || !string.Equals(metadata.InputHash, expected.InputHash, StringComparison.Ordinal)
                || !string.Equals(metadata.NormalizerVersion, expected.NormalizerVersion, StringComparison.Ordinal)
                || !string.Equals(metadata.WorkerVersion, expected.WorkerVersion, StringComparison.Ordinal)
                || !string.Equals(metadata.RequiredMusic21Version, expected.RequiredMusic21Version, StringComparison.Ordinal)
                || !string.Equals(metadata.AnalysisDetail, expected.AnalysisDetail, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(metadata.OutputHash))
                return false;

            AnalysisResultValidator.ReadAndValidate(outputPath, input);
            return string.Equals(
                metadata.OutputHash,
                ComputeFileHash(outputPath),
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // A malformed/truncated cache is a miss. Existing files remain in
            // place so a failed replacement worker cannot destroy a valid old
            // result.
            return false;
        }
    }

    private static string ResolveWorkerScript(string repositoryRoot)
    {
        string script = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "music-analysis", "mdplayer_music_analysis.py"),
            Path.Combine(AppContext.BaseDirectory, "mdplayer_music_analysis.py"),
            Path.Combine(repositoryRoot, "tools", "music-analysis", "mdplayer_music_analysis.py"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "music-analysis", "mdplayer_music_analysis.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "music-analysis", "mdplayer_music_analysis.py"),
        }.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (!File.Exists(script))
            throw new InvalidOperationException($"analysis worker not found: {script}");
        return script;
    }

    private static string FindRepositoryRoot()
    {
        string current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "MDPlayer", "MDPlayer.sln"))
                || Directory.Exists(Path.Combine(current, "tools", "music-analysis")))
                return current;
            string parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string ResolveCacheMetadataPath(string configured, string outputDir)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(outputDir, "cache-metadata.json");
        string full = Path.GetFullPath(configured);
        return Directory.Exists(full) || string.IsNullOrEmpty(Path.GetExtension(full))
            ? Path.Combine(full, "cache-metadata.json")
            : full;
    }

    private static string ComputeFileHash(string path)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void PrintSummary(AnalysisOutput output)
    {
        KeyInterpretation key = output.Global?.Key;
        KeyCandidate primary = key?.Primary;
        if (primary is not null
            && AnalysisDisplayPolicy.DisplayKey(key.Confidence, AnalysisOverlayMode.Standard))
            Console.WriteLine($"KEY {primary.Tonic} {primary.Mode}");
    }

    private static void WriteText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
