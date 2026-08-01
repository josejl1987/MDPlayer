using System.Diagnostics;
using System.Text.Json;
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

        RunPhase1();

        if (args.Length > 0 && File.Exists(args[0]))
            return RunPhase2(args);
        else
            Console.WriteLine("Phase 2 skipped: no input file (pass an OVI path for full-pipeline metrics).");
        return 0;
    }

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
        var renderer = new PanelOverlayRenderer(timeline, new PanelOverlayRenderer.Options
        {
            Width = width,
            Height = height,
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
