using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Fmp.Core.Analysis;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

internal static class AnalysisRunner
{
    public static int Run(AnalyzeOptions options) => Execute(options).ExitCode;

    public static AnalysisExecutionResult Execute(AnalyzeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Stopwatch totalWatch = Stopwatch.StartNew();
        TextWriter output = options.Output ?? Console.Out;

        string defaultOutputDir = options.Timeline != null
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Timeline)) ?? ".", "analysis")
            : !string.IsNullOrWhiteSpace(options.Input)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Input)) ?? ".", Path.GetFileNameWithoutExtension(options.Input) + ".analysis")
                : Path.Combine(Directory.GetCurrentDirectory(), "analysis");
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

        VisualizationTimeline timeline = options.CapturedTimeline ?? (options.Timeline != null
            ? VisualizationJsonWriter.Read(options.Timeline)
            : CaptureTimeline(options));
        AnalysisInput input = FmpSymbolicNormalizer.Normalize(timeline);
        string inputHash = AnalysisCacheKey.ComputeInputHash(input);
        Dictionary<string, string> captureOptions = BuildCaptureOptions(options);
        string captureHash = HashDictionary(captureOptions);

        var expectedMetadata = new AnalysisCacheMetadata
        {
            SchemaVersion = 2,
            InputHash = inputHash,
            NormalizerVersion = FmpSymbolicNormalizer.Version,
            WorkerVersion = AnalysisResultValidator.ExpectedWorkerVersion,
            RequiredMusic21Version = AnalysisResultValidator.ExpectedMusic21Version,
            AnalysisDetail = detail,
            CaptureHash = captureHash,
            NormalizedHash = inputHash,
            CaptureOptions = captureOptions,
            Dependencies = new Dictionary<string, string>(options.CaptureDependencies, StringComparer.Ordinal),
        };

        if (!options.Force && TryReadValidCache(outputPath, metadataPath, input, expectedMetadata))
        {
            AnalysisOutput cached = AnalysisResultValidator.ReadAndValidate(outputPath, input);
            PrintSummary(cached, output);
            output.WriteLine($"Analysis cache hit: {outputPath}");
            return new AnalysisExecutionResult(0, AnalysisCacheStatus.Hit, outputPath,
                new AnalysisExecutionMetrics(totalWatch.Elapsed, 0, new FileInfo(outputPath).Length, true), cached);
        }

        // Keep one canonical UTF-8 representation for the worker input and its
        // byte metrics. The cache identity intentionally excludes analysisId,
        // while the worker still receives the id for result validation.
        string canonicalInput = AnalysisJson.Serialize(input, indented: false);
        WriteText(inputPath, canonicalInput);
        string repositoryRoot = FindRepositoryRoot();
        // Python is intentionally resolved only after the complete cache miss
        // path. A valid cache therefore works on machines without Python.
        AnalysisPython python = AnalysisPythonResolver.Resolve(options.AnalysisPython, repositoryRoot);
        string script = ResolveWorkerScript(repositoryRoot);
        AnalysisWorkerProbeResult workerProbe = AnalysisProcessRunner.Probe(
            python.Path, script, TimeSpan.FromMinutes(Math.Min(1, options.TimeoutMinutes)));
        if (!workerProbe.Compatible)
            throw new InvalidOperationException($"analysis worker compatibility probe failed: {workerProbe.Diagnostics}");
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
            PrintSummary(validated, output);
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
                NormalizedHash = inputHash,
                Dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workerScript"] = FileIdentity(script),
                    ["pythonExecutable"] = FileIdentity(python.Path),
                },
                CaptureOptions = expectedMetadata.CaptureOptions,
                CaptureHash = expectedMetadata.CaptureHash,
            };
            foreach (KeyValuePair<string, string> dependency in options.CaptureDependencies)
                metadata.Dependencies[dependency.Key] = dependency.Value;
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

        output.WriteLine($"Analysis written to: {outputPath}");
        AnalysisOutput result = AnalysisResultValidator.ReadAndValidate(outputPath, input);
        return new AnalysisExecutionResult(0, AnalysisCacheStatus.Miss, outputPath,
            new AnalysisExecutionMetrics(totalWatch.Elapsed, Encoding.UTF8.GetByteCount(canonicalInput), new FileInfo(outputPath).Length, false), result);
    }

    private static VisualizationTimeline CaptureTimeline(AnalyzeOptions options)
    {
        var input = new FileInfo(options.Input);
        if (FmpFormat.IsSupportedExtension(input.Extension))
            return CaptureFmpTimeline(options);

        IReadOnlyList<string> searchPaths = VisualizationBackendResolver.BuildSearchPaths(input, options);
        var environment = new PlaybackEnvironment(searchPaths, true, options.SampleRate);
        string fmpCom = PlaybackBackendRegistry.ResolveFmpCom(options.FmpCom, searchPaths);
        PlaybackBackendRegistry registry = PlaybackBackendRegistry.CreateDefault(environment, fmpCom);
        if (!registry.TrySelect(
                input,
                environment,
                "auto",
                out IPlaybackBackend backend,
                out PlaybackProbeResult probe))
        {
            string details = probe.Warnings.Count == 0
                ? "no playback backend accepted the input"
                : string.Join("; ", probe.Warnings);
            throw new TrackPreparationException(
                $"unsupported format: {input.Extension.ToLowerInvariant()} ({details})", 3);
        }
        if (!probe.Visualizable)
            throw new TrackPreparationException(
                "MDPlayer can play this track, but none of its active devices expose supported note data", 3);

        options.CaptureDependencies["input"] = FileIdentity(input.FullName);
        int timelineSampleRate = probe.NativeSampleRate > 0
            ? probe.NativeSampleRate
            : options.SampleRate;
        var eventSink = new TimelineDecoderEventSink(timelineSampleRate);
        using IPlaybackCaptureSession session = backend.Open(
            input,
            new PlaybackOptions(
                options.Loops,
                options.Fade,
                options.Tail,
                options.MaxDuration,
                OutputAudioPath: null,
                options.SampleRate,
                WriteSpcStems: false,
                SpcPitchMode.Estimate,
                options.SsgGainDb),
            eventSink);
        session.Run();
        VisualizationTimeline timeline = eventSink.Complete(
            session.SamplePosition,
            "completed",
            new TrackMetadata(
                input.Extension.TrimStart('.').ToLowerInvariant(),
                Path.GetFileNameWithoutExtension(input.Name),
                backend.Id,
                input.Name));
        if (!VisualizationContentAvailability.HasRenderableContent(timeline))
            throw new TrackPreparationException(
                "visualization capture contains neither semantic events nor waveform activity", 3);
        return timeline;
    }

    private static VisualizationTimeline CaptureFmpTimeline(AnalyzeOptions options)
    {
        PreparedTrack track = TrackPreparation.Prepare(options.Input, options);
        options.CaptureDependencies["input"] = FileIdentity(track.Input.FullName);
        options.CaptureDependencies["fmpCom"] = FileIdentity(track.Assets.FmpComPath);
        options.CaptureDependencies["virtualFileSystem"] = string.Join("|", track.FileSystem.SearchPaths);
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
            {
                return false;
            }
            AnalysisCacheMetadata metadata = AnalysisJson.Deserialize<AnalysisCacheMetadata>(
                File.ReadAllText(metadataPath));
            bool metadataMatches = metadata.SchemaVersion == expected.SchemaVersion
                && string.Equals(metadata.InputHash, expected.InputHash, StringComparison.Ordinal)
                && string.Equals(metadata.NormalizerVersion, expected.NormalizerVersion, StringComparison.Ordinal)
                && string.Equals(metadata.WorkerVersion, expected.WorkerVersion, StringComparison.Ordinal)
                && string.Equals(metadata.RequiredMusic21Version, expected.RequiredMusic21Version, StringComparison.Ordinal)
                && string.Equals(metadata.AnalysisDetail, expected.AnalysisDetail, StringComparison.Ordinal)
                && string.Equals(metadata.CaptureHash, expected.CaptureHash, StringComparison.Ordinal)
                && string.Equals(metadata.NormalizedHash, expected.NormalizedHash, StringComparison.Ordinal)
                && DictionaryEqual(metadata.CaptureOptions, expected.CaptureOptions)
                && expected.Dependencies.All(item => metadata.Dependencies.TryGetValue(item.Key, out string value)
                    && string.Equals(value, item.Value, StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(metadata.OutputHash);
            if (!metadataMatches)
            {
                return false;
            }

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
    {
        using FileStream stream = File.OpenRead(path);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) hash.AppendData(buffer, 0, read);
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildCaptureOptions(AnalyzeOptions options)
    {
        if (options.CapturedTimeline is not null || options.Timeline is not null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = options.CapturedTimeline is not null ? "captured-timeline" : "timeline-file",
            };
        }
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = options.CapturedTimeline is not null ? "captured-timeline" : options.Timeline is not null ? "timeline-file" : "track-capture",
            ["sampleRate"] = options.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["loops"] = options.Loops.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fadeSeconds"] = options.Fade.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["tailSeconds"] = options.Tail.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["maxDurationSeconds"] = options.MaxDuration.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ["timeoutSeconds"] = options.Timeout?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }

    private static string HashDictionary(IReadOnlyDictionary<string, string> values)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", values.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Key + "=" + item.Value))))).ToLowerInvariant();

    private static string FileIdentity(string path)
    {
        FileInfo info = new(path);
        return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{ComputeFileHash(path)}";
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => (left ?? new Dictionary<string, string>()).Count == (right ?? new Dictionary<string, string>()).Count
            && (left ?? new Dictionary<string, string>()).All(item =>
                (right ?? new Dictionary<string, string>()).TryGetValue(item.Key, out string value)
                && string.Equals(value, item.Value, StringComparison.Ordinal));

    private static void PrintSummary(AnalysisOutput analysis, TextWriter output)
    {
        KeyInterpretation key = analysis.Global?.Key;
        KeyCandidate primary = key?.Primary;
        if (primary is not null
            && AnalysisDisplayPolicy.DisplayKey(key.Confidence, AnalysisOverlayMode.Standard))
            output.WriteLine($"KEY {primary.Tonic} {primary.Mode}");
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
