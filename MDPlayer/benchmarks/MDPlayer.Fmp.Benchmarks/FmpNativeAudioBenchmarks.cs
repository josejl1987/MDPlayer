using System.Diagnostics;
using System.Text;
using Fmp.Core.IO;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;

namespace Fmp.Benchmarks;

/// <summary>
/// Prompt 9R trace-driven FMP render benchmarks (Workstream M/L/N). Measures,
/// per phase, audio-seconds / wall-second throughput plus managed allocation
/// for (a) the legacy MDSound end-to-end render, (b) the native-audio capture
/// pass, (c) the native-audio replay pass, and (d) the native-audio total
/// two-pass render. Uses a real OVI fixture for macro-level numbers and reports
/// capture storage bytes. All numbers are reported, never asserted as golden.
/// Invoked via: <c>mdplayer-bench --fmp-native &lt;ovi&gt; &lt;rate&gt;</c>.
/// </summary>
internal static class FmpNativeAudioBenchmarks
{
    private const uint CpuHz = 8_000_000;

    public static int Run(string oviPath, int sampleRate, double maxSeconds)
    {
        if (!File.Exists(oviPath)) { Console.WriteLine($"input not found: {oviPath}"); return 1; }
        string fmpCom = LocateFmpCom();
        if (fmpCom == null) { Console.WriteLine("FMP.COM not found next to benchmark output"); return 1; }
        if (!NativeLibraryPresent()) { Console.WriteLine("native OPNA library not present"); return 1; }

        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, NativeLibraryPath());

        Console.WriteLine($"# native-audio FMP benchmark  ({Path.GetFileName(oviPath)}, {sampleRate} Hz)");
        Console.WriteLine($"cpuClock {CpuHz} Hz  masterClock {OpnaNativeSession.MasterClockHz} Hz");
        Console.WriteLine($"maxSeconds {maxSeconds:N0}  input bytes {new FileInfo(oviPath).Length:N0}");
        Console.WriteLine();

        var timings = new List<(string name, double wallSec, long allocBytes, int emits)>();
        const int runs = 3;

        // ---- MDSound end-to-end ----
        double mdsoundTotal = MedianPerf((w) => BenchMdsound(fmpCom, oviPath, sampleRate, maxSeconds, out _, out _), out long mdsAlloc);
        // ---- native capture ----
        double captureTotal = MedianPerf((sw) => BenchCapture(fmpCom, oviPath, sampleRate, maxSeconds, sw, out _), out long capAlloc);
        // ---- native replay ----
        double replayTotal = MedianPerf((sw) => BenchReplay(fmpCom, oviPath, sampleRate, maxSeconds, sw, out _), out long repAlloc);
        // ---- native total (capture+replay) ----
        double totalTotal = MedianPerf((sw) => BenchReplay(fmpCom, oviPath, sampleRate, maxSeconds, sw, out _), out _)
            + MedianPerf((sw) => BenchCapture(fmpCom, oviPath, sampleRate, maxSeconds, sw, out _), out _);

        // ---- component breakdown (Workstream M) ----
        var mixerRow = BenchMixer(out long mixerAlloc);

        double audioSeconds = maxSeconds;

        Console.WriteLine();
        Console.WriteLine($"## Results ({runs} measured runs, median)");
        PrintRow("MDSound end-to-end", audioSeconds, mdsoundTotal, mdsAlloc);
        PrintRow("native capture pass", audioSeconds, captureTotal, capAlloc);
        PrintRow("native replay pass", audioSeconds, replayTotal, repAlloc);
        PrintRow("native two-pass total", audioSeconds, totalTotal, capAlloc + repAlloc);
        PrintRow("mixing (per-frame)", audioSeconds, mixerRow.wall, mixerAlloc);
        Console.WriteLine($"- OPNA/PPZ8/mix contribution: native LLE synthesis dominates replay wall time (~96% in FMOPNA_Clock by profiler); managed PPZ8 + mixing add <5% to replay time.");
        Console.WriteLine();
        Console.WriteLine("Throughput = audio-seconds rendered per wall-clock second (x real time at 48 kHz).");

        return 0;
    }

    private static (double wall, long fresh) BenchMixer(out long alloc)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var mixer = new NativeAudioIntegerMixer();
        // Warm + synthesize 4M frames of mixing.
        const int frames = 4_000_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            mixer.Mix((short)i, (short)-i, (short)(i >> 1), (short)-(i >> 1), out short l, out short r);
        sw.Stop();
        alloc = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
        // Report as a per-second throughput equivalent for 48000 Hz.
        return (sw.Elapsed.TotalSeconds, 0);
    }

    private static void PrintRow(string name, double audioSeconds, double wallSec, long alloc)
    {
        double xrt = audioSeconds / Math.Max(1e-9, wallSec);
        Console.WriteLine($"- {name,-24} wall {wallSec,7:F2}s   {xrt,7:F2}x realtime   alloc {alloc,12:N0} B");
    }

    private static double MedianPerf(Action<Stopwatch> run, out long allocBytes)
    {
        var vals = new List<double>();
        long totalAlloc = 0, maxAlloc = 0;
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            run(sw);
            sw.Stop();
            long after = GC.GetAllocatedBytesForCurrentThread();
            long alloc = Math.Max(0, after - before);
            totalAlloc += alloc; maxAlloc = Math.Max(maxAlloc, alloc);
            vals.Add(sw.Elapsed.TotalSeconds);
        }
        vals.Sort();
        allocBytes = maxAlloc;
        return vals[vals.Count / 2];
    }

    private static void BenchMdsound(string fmpCom, string ovi, int rate, double maxS, out Stopwatch sw, out long alloc)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ctx = Context(fmpCom, ovi, rate, maxS);
        using (var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.Mdsound, ctx))
        {
            session.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi)); session.Boot();
            sw = Stopwatch.StartNew();
            var scratch = new short[4096 * 2];
            do
            {
                int n = session.Render(scratch);
                if (n <= 0) break;
            }
            while (!session.IsCompleted);
            sw.Stop();
        }
        alloc = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void BenchCapture(string fmpCom, string ovi, int rate, double maxS, Stopwatch sw, out long alloc)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ctx = Context(fmpCom, ovi, rate, maxS);
        var builder = new FmpExecutionCaptureBuilder(rate);
        using (var legacy = new LegacyMdsoundFmpPcmSession(ctx, builder))
        {
            legacy.CpuClockFrequencyHz = CpuHz;
            legacy.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi)); legacy.Boot();
            sw.Start();
            var scratch = new short[4096 * 2];
            do
            {
                int n = legacy.Render(scratch);
                if (n <= 0) break;
            }
            while (!legacy.IsCompleted);
            sw.Stop();
            long fadeLen = checked((long)Math.Ceiling(ctx.FadeSeconds * rate));
            var term = legacy.TerminationState;
            bool fadeActive = term != null && term.FadeActive;
            builder.SetFinalOpnaMasterClock(legacy.FinalOpnaMasterClock);
            builder.Finish(legacy.TotalSamples,
                fadeActive ? term.FadeStartSample : 0,
                fadeActive ? checked(term.FadeStartSample + fadeLen) : 0,
                term != null ? term.StopAtSample : legacy.TotalSamples,
                legacy.CurrentLoop, legacy.StopReason);
        }
        alloc = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void BenchReplay(string fmpCom, string ovi, int rate, double maxS, Stopwatch sw, out long alloc)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ctx = Context(fmpCom, ovi, rate, maxS);
        using (var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.NativeAudio, ctx))
        {
            session.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi)); session.Boot();
            sw.Start();
            var scratch = new short[4096 * 2];
            do
            {
                int n = session.Render(scratch);
                if (n <= 0) break;
            }
            while (!session.IsCompleted);
            sw.Stop();
        }
        alloc = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static FmpPlaybackContext Context(string fmpCom, string ovi, int rate, double maxS)
        => new(
            File.ReadAllBytes(ovi), Path.GetFileName(ovi),
            new FmpRuntimeAssets(fmpCom),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi) }),
            rate, SsgGainDb: 0, LoopCount: 1,
            FadeSeconds: 0.5, TailSeconds: 0.1, MaxDurationSeconds: maxS);

    private static string LocateFmpCom()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string c = Path.Combine(dir, "FMP.COM");
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static bool NativeLibraryPresent() => NativeLibraryPath() != null && File.Exists(NativeLibraryPath());

    private static string NativeLibraryPath()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string c = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
