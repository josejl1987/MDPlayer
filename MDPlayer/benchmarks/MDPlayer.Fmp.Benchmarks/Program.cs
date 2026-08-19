using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Fmp.Application.Export;
using Fmp.Cli;
using Fmp.Core.Midi;
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
        // Reference-MIDI JSON commands must emit pure JSON (no banner) so the
        // output is machine-parseable and the report can reuse it deterministically.
        bool referenceJsonCmd = args.Length > 0 &&
            (args[0] == "--reference-analyze" || args[0] == "--reference-compare" || args[0] == "--reference-report");
        if (!referenceJsonCmd)
        {
            Console.WriteLine("MDPlayer Visualization Benchmark (§23.4)");
            Console.WriteLine();
        }

        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--perf-midi":
                    return RunMidiFixture(args.Skip(1).Prepend("--midi-fixture").ToArray());
                case "--perf-render":
                    return RunPerfRender(args.Skip(1).ToArray());
                case "--perf-encode":
                    return RunPerfEncode(args.Skip(1).ToArray());
                case "--perf-video":
                    return args.Length > 1 && File.Exists(args[1])
                        ? RunPhase2(args.Skip(1).ToArray())
                        : 2;
                case "--perf-scope":
                    return args.Length > 1 && File.Exists(args[1])
                        ? RunPerfScope(args.Skip(1).ToArray())
                        : 2;
                case "--corpus-receipts":
                    return RawMidiCorpusReporter.Run(args);
                case "--reference-analyze":
                    return args.Length > 1 ? RunReferenceAnalyze(args[1]) : 2;
                case "--reference-compare":
                    return RunReferenceCompare(args.Skip(1).ToArray());
                case "--reference-report":
                    return RunReferenceReport(args.Skip(1).ToArray());
            }
        }

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
    /// Runs a tracked VGZ/OVI through capture, the application MIDI service, and
    /// the canonical writer. This is intentionally an evidence mode: it never
    /// substitutes a synthetic or approximate path.
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
        if (ReadDoubleOption(args, "--bpm") is not null)
        {
            Console.Error.WriteLine("error: --bpm was removed; the MIDI transport is fixed at 120 BPM");
            return 2;
        }
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
            harness = "TimelineCaptureService -> MidiTranscriber -> MidiFileWriter",
            input = new { path = Path.GetRelativePath(root, fixture), sha256 = FileHash(fixture), sizeBytes = inputInfo.Length },
            duration = new { seconds = (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate, startSample = timeline.StartSample, endSample = timeline.EndSample, sampleRate = timeline.SampleRate },
            sourceEvents = CountSourceEvents(timeline),
            emittedMidiEvents = allEvents.Length,
            phases = new object[] { new { phase = "capture", wallMilliseconds = captureWatch.ElapsedMilliseconds, cpuMilliseconds = 0L, allocatedBytes = captureBytes, eventCount = CountSourceEvents(timeline) } }.Concat(events.Cast<object>()),
            semantic = new { notesOn = allEvents.OfType<MidiNoteEvent>().Count(e => e.NoteOn), notesOff = allEvents.OfType<MidiNoteEvent>().Count(e => !e.NoteOn), pitchBends = allEvents.OfType<MidiPitchBendEvent>().Count(), controllers = allEvents.OfType<MidiControlChangeEvent>().Count(), tracks = tracks.Count },
            performance = export.Performance?.ToHumanReadable(),
            output = new { sha256 = outputHash, sizeBytes = export.Bytes.Length },
            configuration = new { request.Ppq },
            environment = new { runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), processorCount = Environment.ProcessorCount },
            comparison = new { status = "baseline-unavailable", baseline = (object?)null, candidate = (object?)null, targetSpeedupClaim = (double?)null },
        };
        string json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        Console.WriteLine($"real fixture: {Path.GetRelativePath(root, fixture)}; output sha256={outputHash}; bytes={export.Bytes.Length}");
        return 0;
    }

    private static int RunPerfRender(string[] args)
    {
        int width = ReadIntOption(args, "--width", 1280);
        int height = ReadIntOption(args, "--height", 720);
        int frames = ReadIntOption(args, "--frames", 300);
        int scopeFps = ReadIntOption(args, "--scope-fps", 60);
        double scopeOpacity = ReadDoubleOption(args, "--scope-opacity") ?? 1.0;
        if (scopeOpacity is < 0.05 or > 1.0)
        {
            Console.Error.WriteLine("error: --scope-opacity must be between 0.05 and 1.0");
            return 2;
        }
        VisualizationTimeline timeline = BuildSyntheticTimeline();
        var renderer = new PanelOverlayRenderer(
            timeline,
            BenchmarkLayout.Build(timeline, width, height),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 60,
                FpsDenominator = 1,
                ScopeOpacity = scopeOpacity,
                EnablePerformanceMetrics = true,
            });
        byte[] destination = new byte[renderer.FrameByteCount];
        byte[] scopeGrid = new byte[renderer.ScopeFrameByteCount];
        // The production Corrscope bridge supplies opaque RGBA. Match that
        // contract so this renderer-only mode measures the same placement path
        // as a real export rather than spending its time normalizing zero alpha.
        for (int offset = 3; offset < scopeGrid.Length; offset += 4)
            scopeGrid[offset] = 255;
        // Cadence emulation (plan §6): at --scope-fps N the compositor maps
        // every Nth output frame onto the same scope frame (floor mapping at
        // 60 fps output), so the same grid is placed N consecutive times —
        // exactly what the frame-renderer cache does in production. This
        // isolates the overlay-side cost of frame reuse from the pipe cost.
        int reuseEveryN = Math.Max(1, (int)Math.Round(60.0 / Math.Min(Math.Max(scopeFps, 1), 60)));
        SequentialCompositeSession session = renderer.CreateSequentialSession(scopeFramesAreOpaque: true);
        session.Initialize(destination);
        for (int index = 0; index < 30; index++)
            session.RenderNext(index % renderer.TotalFrames, scopeGrid, destination);
        renderer.ResetPerformanceMetrics();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < frames; index++)
            session.RenderNext((index + 30) % renderer.TotalFrames, scopeGrid, destination);
        watch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "perf-render",
            frames,
            wallSeconds = watch.Elapsed.TotalSeconds,
            fps = frames / Math.Max(1e-9, watch.Elapsed.TotalSeconds),
            allocatedBytes,
            allocatedBytesPerFrame = allocatedBytes / (double)Math.Max(1, frames),
            peakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            scopeFps,
            scopeOpacity,
            reuseEveryN,
            blendPath = scopeOpacity < 1.0,
            renderer = renderer.Performance,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static int RunPerfEncode(string[] args)
    {
        string ffmpeg = Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg";
        int width = ReadIntOption(args, "--width", 320);
        int height = ReadIntOption(args, "--height", 180);
        int frames = ReadIntOption(args, "--frames", 300);
        var info = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba",
            "-s", $"{width}x{height}", "-r", "60", "-i", "pipe:0", "-an", "-f", "null", "-"
        })
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: encoder start failed: {ex.Message}");
            return 2;
        }
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        byte[] frame = new byte[checked(width * height * 4)];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < frames; index++)
            process.StandardInput.BaseStream.Write(frame, 0, frame.Length);
        process.StandardInput.Close();
        process.WaitForExit();
        watch.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        string diagnostics = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"error: encoder exited {process.ExitCode}: {diagnostics}");
            return process.ExitCode;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "perf-encode",
            frames,
            wallSeconds = watch.Elapsed.TotalSeconds,
            fps = frames / Math.Max(1e-9, watch.Elapsed.TotalSeconds),
            allocatedBytes,
            allocatedBytesPerFrame = allocatedBytes / (double)Math.Max(1, frames),
            peakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            processStarts = 1,
            fullFrameCopies = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
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
        Console.WriteLine("MDPlayer MIDI-2 scaling benchmark (real transcriber/writer harness)");
        MidiScaleMeasurement first = MeasureMidi(n);
        MidiScaleMeasurement doubleSize = MeasureMidi(checked(n * 2));
        var report = new
        {
            harness = "MidiTranscriber -> MidiFileWriter",
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
        var transcriber = new MidiTranscriber(960);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        MidiTranscriptionResult result = transcriber.Transcribe(timeline);
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

    private static int RunPerfScope(string[] benchmarkArgs)
    {
        string inputPath = benchmarkArgs[0];
        var extra = benchmarkArgs.Skip(1).ToArray();

        // Scope cadence pair on the same fixture (plan §6): 30 Hz scope vs
        // 1:1 (explicit --scope-fps 60; auto would resolve to 30 at 60 fps
        // output). The JSON comparison isolates the scope-side win of Change B
        // from encode/analysis noise.
        PerfScopeRun? at30 = RunVisualizeCapture(
            inputPath, extra.Concat(["--scope-fps", "30"]).ToArray(), out int exit30);
        if (at30 is null || exit30 != 0)
        {
            Console.Error.WriteLine($"error: --scope-fps 30 run failed (exit {exit30})");
            return exit30 != 0 ? exit30 : 2;
        }
        PerfScopeRun? at60 = RunVisualizeCapture(inputPath, extra, out int exit60);
        if (at60 is null || exit60 != 0)
        {
            Console.Error.WriteLine($"error: 1:1 run failed (exit {exit60})");
            return exit60 != 0 ? exit60 : 2;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "perf-scope",
            input = inputPath,
            scope30 = at30,
            scope60 = at60,
            comparison = new
            {
                scopeFrameReadRatio = at60.ScopeFrameReadSeconds > 0
                    ? Math.Round(at30.ScopeFrameReadSeconds / at60.ScopeFrameReadSeconds, 3)
                    : (double?)null,
                corrscopeWaitRatio = at60.CorrscopeWaitSeconds > 0
                    ? Math.Round(at30.CorrscopeWaitSeconds / at60.CorrscopeWaitSeconds, 3)
                    : (double?)null,
                overlayCpuRatio = at60.OverlayCpuSeconds > 0
                    ? Math.Round(at30.OverlayCpuSeconds / at60.OverlayCpuSeconds, 3)
                    : (double?)null,
                wallTimeRatio = at60.WallSeconds > 0
                    ? Math.Round(at30.WallSeconds / at60.WallSeconds, 3)
                    : (double?)null,
                starvation30 = at30.StarvationCount,
                starvation60 = at60.StarvationCount,
                encoderIdleDelta = Math.Round(at30.EncoderIdleSeconds - at60.EncoderIdleSeconds, 3),
                rendererBlockedDelta = Math.Round(at30.RendererBlockedSeconds - at60.RendererBlockedSeconds, 3),
                frameCountIdentical = at30.FrameCount == at60.FrameCount,
            },
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private sealed record PerfScopeRun(
        int ExitCode,
        double WallSeconds,
        double CorrscopeWaitSeconds,
        double ScopeFrameReadSeconds,
        double OverlayCpuSeconds,
        double EncoderIdleSeconds,
        double RendererBlockedSeconds,
        double QueueWaitSeconds,
        double MuxFinalizationSeconds,
        long StarvationCount,
        long FrameCount,
        long OutputSizeBytes,
        string Bottleneck);

    /// <summary>
    /// Runs the real single-pass visualize pipeline once with the given extra
    /// CLI arguments and returns the scope-side metrics from its JSON summary.
    /// Returns null when the JSON summary cannot be parsed.
    /// </summary>
    private static PerfScopeRun? RunVisualizeCapture(
        string inputPath, string[] extraArgs, out int exitCode)
    {
        string? cliProject = FindCliProject();
        if (cliProject == null)
        {
            Console.Error.WriteLine("  error: MDPlayer.Fmp.Cli project was not found");
            exitCode = 2;
            return null;
        }

        string outputDirectory = Path.Combine(
            Path.GetTempPath(), "mdplayer-fmp-benchmark-" + Guid.NewGuid().ToString("N"));
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
            foreach (string argument in extraArgs)
                startInfo.ArgumentList.Add(argument);
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
            exitCode = process.ExitCode;

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"  visualize exited {process.ExitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.Error.WriteLine(stderr.Trim());
                return null;
            }

            using JsonDocument document = ParseJson(stdout);
            JsonElement root = document.RootElement;
            JsonElement stages = root.GetProperty("stages");
            return new PerfScopeRun(
                ExitCode: 0,
                WallSeconds: wallSeconds,
                CorrscopeWaitSeconds: GetDouble(stages, "corrscopeWaitSeconds"),
                ScopeFrameReadSeconds: GetDouble(stages, "scopeFrameReadSeconds"),
                OverlayCpuSeconds: GetDouble(stages, "overlayCpuSeconds"),
                EncoderIdleSeconds: GetDouble(stages, "encoderIdleSeconds"),
                RendererBlockedSeconds: GetDouble(stages, "rendererBlockedSeconds"),
                QueueWaitSeconds: GetDouble(stages, "queueWaitSeconds"),
                MuxFinalizationSeconds: GetDouble(stages, "muxFinalizationSeconds"),
                StarvationCount: GetLong(stages, "starvationCount"),
                FrameCount: GetLong(stages, "frameCount"),
                OutputSizeBytes: GetLong(root, "outputSizeBytes"),
                Bottleneck: ClassifyVideoBottleneck(stages));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  benchmark failed: {ex.Message}");
            exitCode = 2;
            return null;
        }
        finally
        {
            try { Directory.Delete(outputDirectory, recursive: true); } catch { }
        }
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
            Console.WriteLine($"  Source playback/state:    {GetDouble(stages, "sourcePlaybackStateSeconds"):F3}s");
            Console.WriteLine($"  Timeline capture:         {GetDouble(stages, "timelineCaptureSeconds"):F3}s");
            Console.WriteLine($"  Stem export:              {GetDouble(stages, "stemExportSeconds"):F3}s");
            Console.WriteLine($"  Audio processing:         {GetDouble(stages, "audioProcessingSeconds"):F3}s");
            Console.WriteLine($"  Energy analysis:          {GetDouble(stages, "energyAnalysisSeconds"):F3}s");
            Console.WriteLine($"  Corrscope wait:           {GetDouble(stages, "corrscopeWaitSeconds"):F3}s");
            Console.WriteLine($"  Overlay CPU:              {GetDouble(stages, "overlayCpuSeconds"):F3}s");
            Console.WriteLine($"  FFmpeg write wait:        {GetDouble(stages, "ffmpegWriteWaitSeconds"):F3}s");
            Console.WriteLine($"  Pixel conversion:         {GetDouble(stages, "pixelConversionSeconds"):F3}s");
            Console.WriteLine($"  Mux/finalization:         {GetDouble(stages, "muxFinalizationSeconds"):F3}s");
            Console.WriteLine($"  Render CPU:               {GetDouble(stages, "renderSeconds"):F3}s");
            Console.WriteLine($"  Scope frame read:         {GetDouble(stages, "scopeFrameReadSeconds"):F3}s");
            Console.WriteLine($"  Dynamic layer:            {GetDouble(stages, "dynamicLayerSeconds"):F3}s");
            Console.WriteLine($"  Frame-state update:       {GetDouble(stages, "frameStateUpdateSeconds"):F3}s");
            Console.WriteLine($"  Compositing:              {GetDouble(stages, "compositingSeconds"):F3}s");
            Console.WriteLine($"  Layout:                   {GetDouble(stages, "layoutSeconds"):F3}s");
            Console.WriteLine($"  Static layer:             {GetDouble(stages, "staticLayerSeconds"):F3}s");
            Console.WriteLine($"  Text:                     {GetDouble(stages, "textSeconds"):F3}s");
            Console.WriteLine($"  Piano roll:               {GetDouble(stages, "pianoRollSeconds"):F3}s");
            Console.WriteLine($"  Waveform:                 {GetDouble(stages, "waveformSeconds"):F3}s");
            Console.WriteLine($"  Full/partial redraws:     {GetLong(stages, "fullRedraws")}/{GetLong(stages, "partialRedraws")}");
            Console.WriteLine($"  Full-frame copies:        {GetLong(stages, "fullFrameCopies")}");
            long frameCount = GetLong(stages, "frameCount");
            long copiedBytes = GetLong(stages, "copiedBytes");
            Console.WriteLine($"  Full-frame copies/frame:  {GetLong(stages, "fullFrameCopies") / (double)Math.Max(1, frameCount):F2}");
            Console.WriteLine($"  Copied bytes:             {copiedBytes}");
            Console.WriteLine($"  Copied bytes/frame:       {copiedBytes / (double)Math.Max(1, frameCount):F1}");
            Console.WriteLine($"  Piano-roll cursor moves:  {GetLong(stages, "pianoRollCursorAdvances")}");
            Console.WriteLine($"  Visible notes visited:    {GetLong(stages, "visibleNotesVisited")}");
            Console.WriteLine($"  Peak RSS:                 {GetLong(stages, "peakWorkingSetBytes")} bytes");
            long pipelineFrames = frameCount;
            long pipelineAllocated = GetLong(stages, "allocatedBytes");
            Console.WriteLine($"  Allocated bytes/frame:    {(pipelineAllocated / (double)Math.Max(1, pipelineFrames)):F1}");
            Console.WriteLine($"  Queue wait:               {GetDouble(stages, "queueWaitSeconds"):F3}s");
            Console.WriteLine($"  Renderer idle:            {GetDouble(stages, "rendererIdleSeconds"):F3}s");
            Console.WriteLine($"  Renderer blocked:         {GetDouble(stages, "rendererBlockedSeconds"):F3}s");
            Console.WriteLine($"  Encoder idle:             {GetDouble(stages, "encoderIdleSeconds"):F3}s");
            Console.WriteLine($"  Encoder blocked:          {GetDouble(stages, "encoderBlockedSeconds"):F3}s");
            Console.WriteLine($"  Queue high-water:         {GetLong(stages, "maxQueueDepth")}");
            Console.WriteLine($"  Scope+overlay+encode:     {compositionSeconds:F3}s");
            Console.WriteLine($"  Overall total:            {totalSeconds:F3}s (wall {wallSeconds:F3}s)");
            Console.WriteLine($"  Effective output FPS:     {GetDouble(root, "effectiveOutputFps"):F1}");
            Console.WriteLine($"  Real-time factor:         {GetDouble(root, "realTimeFactor"):F2}x");
            Console.WriteLine($"  Encoder:                  {GetString(root, "encoder")}");
            Console.WriteLine($"  Bottleneck:               {ClassifyVideoBottleneck(stages)}");
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

    private static string ClassifyVideoBottleneck(JsonElement stages)
    {
        double scopeRead = GetDouble(stages, "scopeFrameReadSeconds");
        double render = GetDouble(stages, "renderSeconds");
        double encoderBlocked = GetDouble(stages, "encoderBlockedSeconds");
        double source = GetDouble(stages, "sourcePlaybackStateSeconds");
        if (scopeRead > render && scopeRead > encoderBlocked)
            return "SOURCE/SCOPE";
        if (encoderBlocked > render && encoderBlocked > scopeRead)
            return "ENCODER";
        if (render > source)
            return "RENDERER";
        return "SOURCE STATE";
    }

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

    /// <summary>
    /// Reference-driven MIDI analysis: prints the structural JSON for a .mid path.
    /// </summary>
    private static int RunReferenceAnalyze(string midiPath)
    {
        if (!File.Exists(midiPath))
        {
            Console.Error.WriteLine($"error: MIDI file not found: {midiPath}");
            return 2;
        }
        var result = ReferenceMidiAnalyzer.Analyze(midiPath);
        Console.WriteLine(ReferenceMidiAnalyzer.ToJson(result));
        return 0;
    }

    /// <summary>
    /// Reference-driven MIDI comparison: --reference-compare &lt;ref.mid&gt; &lt;cand.mid&gt; [trackmap.json].
    /// </summary>
    private static int RunReferenceCompare(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --reference-compare <ref.mid> <cand.mid> [trackmap.json]");
            return 2;
        }
        string refPath = args[0];
        string candPath = args[1];
        string? trackMap = args.Length > 2 ? args[2] : null;
        if (!File.Exists(refPath))
        {
            Console.Error.WriteLine($"error: reference MIDI not found: {refPath}");
            return 2;
        }
        if (!File.Exists(candPath))
        {
            Console.Error.WriteLine($"error: candidate MIDI not found: {candPath}");
            return 2;
        }
        var result = ReferenceMidiComparator.Compare(refPath, candPath, trackMap);
        Console.WriteLine(ReferenceMidiComparator.ToJson(result));
        return 0;
    }

    /// <summary>
    /// Reference-driven report: --reference-report &lt;manifest.json&gt; [outputDir].
    /// </summary>
    private static int RunReferenceReport(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: --reference-report <manifest.json> [outputDir]");
            return 2;
        }
        string manifest = args[0];
        string outputDir = args.Length > 1 ? args[1] : ".";
        return ReferenceMidiReport.Run(manifest, outputDir);
    }
}
