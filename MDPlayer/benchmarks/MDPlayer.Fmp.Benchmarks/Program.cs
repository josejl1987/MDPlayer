using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Fmp.Application.Export;
using Fmp.Cli;
using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Benchmarks;

/// <summary>
/// PR 10 D2 (§23.4): opt-in benchmark for the visualization renderer.
/// Phase 1 (always runs): times RenderCompositeFrame over a synthetic
/// timeline — preparation time, average/P95 frame time, effective FPS,
/// and real-time factor. No machine-specific thresholds block CI.
/// Phase 2 (requires user OVI): parses --json output of a real visualize
/// run for Corrscope/pipe throughput. Skipped when the input file is absent.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("MDPlayer Visualization Benchmark (§23.4)");
        Console.WriteLine();

        if (args.Length >= 2 && args[0] == "--profile")
        {
            ProfileReal.Run(args[1]);
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--fmp-native")
        {
            int rate = args.Length >= 3 && int.TryParse(args[2], out var r) ? r : 48000;
            double maxS = args.Length >= 4 && double.TryParse(args[3], out var m) ? m : 20.0;
            return FmpNativeAudioBenchmarks.Run(args[1], rate, maxS);
        }

        if (args.Length >= 2 && args[0] == "--midi-scale")
        {
            int n = int.TryParse(args[1], out int parsed) ? parsed : 1_000;
            return RunMidiScale(n);
        }

        if (args.Length >= 1 && args[0] == "--midi-fixture")
            return RunMidiFixture(args);

        RunPhase1();

        if (args.Length > 0 && File.Exists(args[0]))
            return RunPhase2(args);
        else
            Console.WriteLine("Phase 2 skipped: no input file (pass an OVI path for full-pipeline metrics).");
        return 0;
    }

    /// <summary>
    /// Runs a tracked VGZ/OVI through capture, SourceTimeline, the application
    /// MIDI service, and the canonical writer. This is intentionally an evidence
    /// mode: it never substitutes a synthetic or approximate path.
    /// </summary>
    private static int RunMidiFixture(string[] args)
    {
        string? requested = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1] : null;
        string? fixture = MidiFixtureResolver.Resolve(requested);
        if (fixture is null)
        {
            Console.Error.WriteLine(requested is null
                ? "error: no tracked VGZ/OVI fixture found"
                : $"error: fixture not found or unsupported: {requested}");
            return 2;
        }

        int ppq = ReadIntOption(args, "--ppq", 960);
        double? bpm = ReadDoubleOption(args, "--bpm");
        string root = MidiFixtureResolver.FindRepositoryRoot(fixture);
        var settings = new BatchRenderSettings
        {
            AssetsDir = root,
            Loops = 1,
            MaxDuration = 300,
            Timeout = 120,
            SampleRate = 44_100,
        };
        settings.ValidateCommon();
        var inputInfo = new FileInfo(fixture);
        long captureAllocated = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch captureWatch = Stopwatch.StartNew();
        VisualizationTimeline timeline = TimelineCaptureService.Capture(fixture, null, settings);
        captureWatch.Stop();
        long captureBytes = GC.GetAllocatedBytesForCurrentThread() - captureAllocated;

        var request = new MidiExportRequest
        {
            Ppq = ppq,
            Bpm = bpm,
            TempoSource = bpm is null ? Fmp.Application.Export.MidiTempoSource.Auto : Fmp.Application.Export.MidiTempoSource.Fixed,
            EnablePerformanceReceipts = true,
            PerformanceFixture = Path.GetFileName(fixture),
        };
        MidiExportResult export = new MidiExportService().Export(timeline, request);
        if (!export.Succeeded || export.Bytes is null)
        {
            Console.Error.WriteLine($"error: MIDI export failed: {export.Error}");
            return 4;
        }
        string outputHash = Convert.ToHexString(SHA256.HashData(export.Bytes)).ToLowerInvariant();
        var events = export.Performance?.Phases ?? Array.Empty<ExportPhaseReceipt>();
        var tracks = export.Tracks ?? Array.Empty<MidiTrack>();
        var allEvents = tracks.SelectMany(t => t.Events).ToArray();
        var receipt = new
        {
            schema = "mdplayer.midi-fixture-receipt/v1",
            harness = "TimelineCaptureService -> SourceTimeline -> MusicalMidiExporter -> MidiFileWriter",
            input = new { path = Path.GetRelativePath(root, fixture), sha256 = FileHash(fixture), sizeBytes = inputInfo.Length },
            duration = new { seconds = (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate, startSample = timeline.StartSample, endSample = timeline.EndSample, sampleRate = timeline.SampleRate },
            sourceEvents = CountSourceEvents(timeline),
            emittedMidiEvents = allEvents.Length,
            phases = new object[] { new { phase = "capture", wallMilliseconds = captureWatch.ElapsedMilliseconds, cpuMilliseconds = 0L, allocatedBytes = captureBytes, eventCount = CountSourceEvents(timeline) } }.Concat(events.Cast<object>()),
            semantic = new { notesOn = allEvents.OfType<MidiNoteEvent>().Count(e => e.NoteOn), notesOff = allEvents.OfType<MidiNoteEvent>().Count(e => !e.NoteOn), pitchBends = allEvents.OfType<MidiPitchBendEvent>().Count(), controllers = 0, tracks = tracks.Count },
            output = new { sha256 = outputHash, sizeBytes = export.Bytes.Length },
            configuration = new { request.Ppq, request.Bpm, request.Quantize, request.EmitPitchBend, request.BendRangeSemitones, request.UsePercussionChannel, request.Velocity },
            environment = new { runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), processorCount = Environment.ProcessorCount },
            comparison = new { status = "baseline-unavailable", baseline = (object?)null, candidate = (object?)null, targetSpeedupClaim = (double?)null },
        };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        Console.WriteLine($"real fixture: {Path.GetRelativePath(root, fixture)}; output sha256={outputHash}; bytes={export.Bytes.Length}");
        return 0;
    }

    private static int CountSourceEvents(VisualizationTimeline timeline) =>
        (timeline.Notes?.Count() ?? 0) + (timeline.Rhythm?.Count() ?? 0) +
        (timeline.Beats?.Count() ?? 0) + (timeline.Timing?.Count() ?? 0);

    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static int ReadIntOption(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int value) ? value : fallback;
    }
    private static double? ReadDoubleOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], out double value) ? value : null;
    }

    private static int RunMidiScale(int n)
    {
        if (n < 1) return 2;
        Console.WriteLine("MDPlayer MIDI-2 scaling benchmark (real exporter/writer harness)");
        MidiScaleMeasurement first = MeasureMidi(n);
        MidiScaleMeasurement doubleSize = MeasureMidi(checked(n * 2));
        var report = new
        {
            harness = "MusicalMidiExporter -> MidiFileWriter",
            input = "deterministic generated MIDI timeline; fixed 44.1kHz/120BPM/960PPQ",
            baseline = (object?)null,
            candidate = new { n = first, twoN = doubleSize },
            comparison = new { status = "baseline-unavailable", targetSpeedupClaim = (double?)null },
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static MidiScaleMeasurement MeasureMidi(int sourceEvents)
    {
        VisualizationTimeline timeline = BuildMidiTimeline(sourceEvents);
        var map = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions { FixedBpm = 120 }).Map;
        var exporter = new MusicalMidiExporter(map, 960);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        MusicalMidiExportResult result = exporter.Export(timeline);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        int emitted = result.Tracks.Sum(t => t.Events.Count);
        int noteOns = result.Tracks.Sum(t => t.Events.OfType<MidiNoteEvent>().Count(e => e.NoteOn));
        int noteOffs = result.Tracks.Sum(t => t.Events.OfType<MidiNoteEvent>().Count(e => !e.NoteOn));
        return new(sourceEvents, emitted, result.Bytes.Length, watch.Elapsed.TotalMilliseconds,
            allocated, GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1,
            GC.CollectionCount(2) - gen2, noteOns, noteOffs);
    }

    private static VisualizationTimeline BuildMidiTimeline(int count)
    {
        const int sampleRate = 44_100;
        long spacing = sampleRate / 8;
        return new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = Math.Max(spacing, count * spacing + spacing),
            Notes = Enumerable.Range(0, count).Select(i => new NoteEvent(
                "midi-bench", i * spacing, i * spacing + spacing / 2, 440, 60 + i % 24,
                "bench", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>())).ToArray(),
        };
    }

    private sealed record MidiScaleMeasurement(int SourceEvents, int EmittedMidiEvents,
        int OutputBytes, double WallMilliseconds, long ManagedAllocatedBytes,
        int Gen0Collections, int Gen1Collections, int Gen2Collections,
        int SemanticNoteOns, int SemanticNoteOffs);

    /// <summary>
    /// Phase 1: CPU-only frame timing using a synthetic timeline with 12 active
    /// panels, notes, and rhythm events. Measures the pure overlay cost.
    /// </summary>
    private static void RunPhase1()
    {
        Console.WriteLine("Phase 1: CPU frame timing (synthetic timeline)");
        Console.WriteLine("─────────────────────────────────────────────");

        var timeline = BuildSyntheticTimeline();
        int width = 1920;
        int height = 1080;
        int fps = 60;

        var stopwatch = Stopwatch.StartNew();
        var renderer = new PanelOverlayRenderer(
            timeline,
            BenchmarkLayout.Build(timeline, width, height),
            new PanelOverlayRenderer.Options
        {
            FpsNumerator = fps,
            FpsDenominator = 1,
        });
        stopwatch.Stop();
        Console.WriteLine($"  Preparation:        {stopwatch.Elapsed.TotalMilliseconds:F1} ms");

        int frameBytes = width * height * 4;
        var buffer = new byte[frameBytes];
        var scopeGrid = new byte[width * renderer.Layout.CorrscopeGridHeight * 4];
        int warmupFrames = 30;
        int measuredFrames = 300;

        // Warmup
        for (long f = 0; f < warmupFrames; f++)
            renderer.RenderCompositeFrame(f, scopeGrid, buffer);

        // Timed batch
        var frameTimes = new double[measuredFrames];
        var batchWatch = Stopwatch.StartNew();
        for (int i = 0; i < measuredFrames; i++)
        {
            var frameWatch = Stopwatch.StartNew();
            renderer.RenderCompositeFrame(warmupFrames + i, scopeGrid, buffer);
            frameWatch.Stop();
            frameTimes[i] = frameWatch.Elapsed.TotalMilliseconds;
        }
        batchWatch.Stop();

        Array.Sort(frameTimes);
        double avgMs = frameTimes.Average();
        double p95Ms = frameTimes[(int)(measuredFrames * 0.95)];
        double effectiveFps = 1000.0 / avgMs;
        double realTimeFactor = effectiveFps / fps;

        Console.WriteLine($"  Average frame time: {avgMs:F3} ms");
        Console.WriteLine($"  P95 frame time:     {p95Ms:F3} ms");
        Console.WriteLine($"  Effective FPS:      {effectiveFps:F1}");
        Console.WriteLine($"  Real-time factor:   {realTimeFactor:F2}x (≥1.0 = real-time capable)");
        Console.WriteLine($"  Resolution:         {width}×{height} @ {fps} fps");
        Console.WriteLine($"  Frames measured:    {measuredFrames} (warmup: {warmupFrames})");
        Console.WriteLine();
    }

    /// <summary>
    /// Phase 2: parse the JSON summary from a real visualize run to report
    /// Corrscope throughput, pipe throughput, and encoder selection.
    /// </summary>
    private static int RunPhase2(string[] benchmarkArgs)
    {
        string inputPath = benchmarkArgs[0];
        Console.WriteLine($"Phase 2: Full pipeline ({inputPath})");
        Console.WriteLine("─────────────────────────────────────────────");

        string? cliProject = FindCliProject();
        if (cliProject == null)
        {
            Console.Error.WriteLine("  error: MDPlayer.Fmp.Cli project was not found");
            return 2;
        }

        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "mdplayer-fmp-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(cliProject);
            startInfo.ArgumentList.Add("--no-restore");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("visualize");
            startInfo.ArgumentList.Add(inputPath);
            for (int index = 1; index < benchmarkArgs.Length; index++)
                startInfo.ArgumentList.Add(benchmarkArgs[index]);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputDirectory);
            startInfo.ArgumentList.Add("--overwrite");
            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add("--json");

            using var process = new Process { StartInfo = startInfo };
            long start = Stopwatch.GetTimestamp();
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            double wallSeconds = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"  visualize exited {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.Error.WriteLine(stderr.Trim());
                return process.ExitCode;
            }

            using JsonDocument document = ParseJson(stdout);
            JsonElement root = document.RootElement;
            JsonElement stages = root.GetProperty("stages");
            double trackSeconds = GetDouble(root, "trackDurationSeconds");
            double totalSeconds = GetDouble(stages, "overallSeconds");
            double compositionSeconds = GetDouble(stages, "scopeOverlayEncodeSeconds");

            Console.WriteLine($"  Track duration:           {trackSeconds:F3}s");
            Console.WriteLine($"  Timeline capture:         {GetDouble(stages, "timelineCaptureSeconds"):F3}s");
            Console.WriteLine($"  Stem export:              {GetDouble(stages, "stemExportSeconds"):F3}s");
            Console.WriteLine($"  Energy analysis:          {GetDouble(stages, "energyAnalysisSeconds"):F3}s");
            Console.WriteLine($"  Corrscope wait:           {GetDouble(stages, "corrscopeWaitSeconds"):F3}s");
            Console.WriteLine($"  Overlay CPU:              {GetDouble(stages, "overlayCpuSeconds"):F3}s");
            Console.WriteLine($"  FFmpeg write wait:        {GetDouble(stages, "ffmpegWriteWaitSeconds"):F3}s");
            Console.WriteLine($"  Scope+overlay+encode:     {compositionSeconds:F3}s");
            Console.WriteLine($"  Overall total:            {totalSeconds:F3}s (wall {wallSeconds:F3}s)");
            Console.WriteLine($"  Effective output FPS:     {GetDouble(root, "effectiveOutputFps"):F1}");
            Console.WriteLine($"  Real-time factor:         {GetDouble(root, "realTimeFactor"):F2}x");
            Console.WriteLine($"  Encoder:                  {GetString(root, "encoder")}");
            Console.WriteLine($"  Output size:              {GetLong(root, "outputSizeBytes")} bytes");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  benchmark failed: {ex.Message}");
            return 2;
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch { }
        }
    }

    private static string? FindCliProject()
    {
        string? current = Environment.CurrentDirectory;
        for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
        {
            string direct = Path.Combine(current, "src", "MDPlayer.Fmp.Cli", "MDPlayer.Fmp.Cli.csproj");
            if (File.Exists(direct))
                return direct;
            string nested = Path.Combine(current, "MDPlayer", "src", "MDPlayer.Fmp.Cli", "MDPlayer.Fmp.Cli.csproj");
            if (File.Exists(nested))
                return nested;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private static JsonDocument ParseJson(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new InvalidOperationException("visualize did not emit a JSON summary");
        return JsonDocument.Parse(output[start..(end + 1)]);
    }

    private static double GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result)
            ? result : 0;

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result)
            ? result : 0;

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";

    /// <summary>
    /// Builds a synthetic timeline covering all 12 panel families with notes,
    /// pitch bends, and rhythm events — exercising every rendering path.
    /// </summary>
    private static VisualizationTimeline BuildSyntheticTimeline()
    {
        var notes = new List<NoteEvent>();
        int sampleRate = 55467;
        long noteSpacing = sampleRate / 4; // 4 notes per second

        // FM channels 1–6
        for (int ch = 1; ch <= 6; ch++)
        {
            string channelId = $"ym2608.0.fm.{ch}";
            for (int n = 0; n < 50; n++)
            {
                long start = n * noteSpacing;
                double pitch = 60 + (ch * 2) + (n % 12);
                notes.Add(new NoteEvent(
                    channelId, start, start + noteSpacing * 3 / 4,
                    440.0 * Math.Pow(2, (pitch - 69) / 12),
                    pitch, "ym2608:aaaaaa1111111111",
                    VisualizationNoteMode.Fm, false,
                    [new PitchChange(start + noteSpacing / 2, 466.16, pitch + 0.5)]));
            }
        }

        // SSG channels 1–3
        for (int ch = 1; ch <= 3; ch++)
        {
            string channelId = $"ym2608.0.ssg.{ch}";
            var mode = ch switch
            {
                1 => VisualizationNoteMode.SsgTone,
                2 => VisualizationNoteMode.SsgToneNoise,
                _ => VisualizationNoteMode.SsgEnvelopeTone,
            };
            for (int n = 0; n < 30; n++)
            {
                long start = n * noteSpacing;
                notes.Add(new NoteEvent(
                    channelId, start, start + noteSpacing,
                    440.0, 60 + n, "ym2608:ssg", mode, false, Array.Empty<PitchChange>()));
            }
        }

        // Rhythm events
        var rhythm = new List<RhythmEvent>();
        for (int n = 0; n < 100; n++)
        {
            rhythm.Add(new RhythmEvent(
                n % 2 == 0 ? "bd" : "sd",
                "ym2608.0.rhythm",
                n * sampleRate / 8,
                0.5f + (n % 3) * 0.25f,
                (n % 3) * 0.5f - 0.5f));
        }

        return new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = 50 * sampleRate,
            Instruments = [new InstrumentDefinition("ym2608:aaaaaa1111111111", "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>())],
            Notes = notes.ToArray(),
            Rhythm = rhythm.ToArray(),
        };
    }
}