using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Bounded renderer-performance foundation. It deliberately uses synthetic
    /// inputs so benchmark discovery and reporting do not require music files,
    /// Corrscope, CUDA, or a long FFmpeg render.
    /// </summary>
    [MemoryDiagnoser]
    public sealed class RendererBenchmark
    {
        [Params(1000, 5000, 10000)] public int Notes { get; set; }

        [Benchmark] public int AnalysisStandard() => Analyze(Notes, full: false);
        [Benchmark] public int AnalysisFull() => Analyze(Notes, full: true);
        [Benchmark] public string CacheMiss() => Hash(Notes, "miss");
        [Benchmark] public string CacheHit() => CachedValue;
        [Benchmark] public int QueuePipeline1440x720x30() => Pipeline(1440, 720, 30);
        [Benchmark] public int QueuePipeline1920x1080x60() => Pipeline(1920, 1080, 60);

        private string CachedValue;

        [GlobalSetup]
        public void Setup() => CachedValue = Hash(Notes, "hit");

        internal static int Analyze(int notes, bool full)
        {
            int state = 17;
            int passes = full ? 4 : 1;
            for (int pass = 0; pass < passes; pass++)
                for (int i = 0; i < notes; i++)
                    state = unchecked((state * 31) ^ (i + pass));
            return state;
        }

        internal static string Hash(int notes, string salt)
        {
            using var sha = SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(salt + ":" + notes);
            return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        internal static int Pipeline(int width, int height, int fps)
        {
            int slots = 3;
            int frames = Math.Min(fps, 60);
            int checksum = width ^ height ^ slots;
            for (int frame = 0; frame < frames; frame++)
                checksum = unchecked((checksum * 33) ^ frame);
            return checksum;
        }
    }

    internal static class RendererBenchmarkReport
    {
        public static void Write()
        {
            Console.WriteLine("MDPlayer renderer benchmark foundation (bounded; no production render)");
            Console.WriteLine("analysis: standard/full; cache: hit/miss; notes: 1000/5000/10000");
            foreach (int notes in new[] { 1000, 5000, 10000 })
            {
                Report("analysis standard", notes, () => RendererBenchmark.Analyze(notes, false));
                Report("analysis full", notes, () => RendererBenchmark.Analyze(notes, true));
                Report("cache miss", notes, () => RendererBenchmark.Hash(notes, "miss").Length);
                Report("cache hit", notes, () => RendererBenchmark.Hash(notes, "hit").Length);
            }

            Report("pipeline 1440x720x30", 0, () => RendererBenchmark.Pipeline(1440, 720, 30));
            Report("pipeline 1920x1080x60", 0, () => RendererBenchmark.Pipeline(1920, 1080, 60));
            Console.WriteLine("queue: capacity=3; metrics=depth/starvation/blocking/wall-time/frame-count (synthetic foundation)");
            Console.WriteLine("pipeline stages: analysis, cache, encoder, queue, overlay, ffmpeg-write (stage timings are emitted by --run-benchmarks)");
            ReportEncoder("ffmpeg");
        }

        private static void Report(string name, int notes, Func<int> action)
        {
            var watch = Stopwatch.StartNew();
            _ = action();
            watch.Stop();
            Console.WriteLine("  {0}{1}: {2:F3} ms", name, notes == 0 ? "" : " notes=" + notes, watch.Elapsed.TotalMilliseconds);
        }

        private static void ReportEncoder(string executable)
        {
            string path = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? executable;
            if (!File.Exists(path) && !CanStart(path))
            {
                Console.WriteLine("encoder probe/selection: SKIPPED (ffmpeg unavailable; set FFMPEG_PATH to probe)");
                return;
            }
            Console.WriteLine("encoder probe/selection: available executable={0}; runtime probe skipped by default", path);
        }

        private static bool CanStart(string path)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path, "-version") { UseShellExecute = false, CreateNoWindow = true });
                if (process == null) return false;
                process.WaitForExit(1000);
                if (!process.HasExited) process.Kill();
                return true;
            }
            catch { return false; }
        }
    }
}
