using System.Diagnostics;
using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.NativeAudioValidation;
using MDPlayer.Fmp.Tests.NativeAudioValidation;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Internal native-audio validation harness (Prompt 9R). Drives the real
/// two-pass pipeline — capture through the legacy MDSound session, replay
/// through the production <see cref="NativeAudioFmpPcmSession"/> with a
/// recording device wrapper — and emits backend-independent capture and replay
/// reports (JSON authoritative, Markdown summary). Uses the existing production
/// components; no production counters are introduced (the recording wrapper is
/// a validation seam). Nothing is written during ordinary rendering.
/// </summary>
internal static class NativeAudioValidationRunner
{
    private const uint CpuHz = 8_000_000;

    public static bool Available(out string reason)
    {
        if (NativeLibrary() == null) { reason = "native OPNA library not built"; return false; }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM"))) { reason = "FMP.COM not present"; return false; }
        var corpusMessages = new List<string>();
        var doc = ValidationCorpus.LoadManifest();
        foreach (var def in doc.Entries)
        {
            string p = ValidationCorpus.Resolve(def);
            if (p == null) corpusMessages.Add($"no fixture for '{def.Id}'");
        }
        reason = corpusMessages.Count == 0 ? null : string.Join("; ", corpusMessages);
        return true;
    }

    public static string? NativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name; private readonly string? _prev;
        public RestoreEnv(string name, string? prev) { _name = name; _prev = prev; }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _prev);
    }

    public static void UseNativeLibrary(string? lib)
        => Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, lib);

    public static IDisposable WithNativeLibrary()
    {
        string? lib = NativeLibrary();
        if (lib == null) return new RestoreEnv(OpnaNativeSession.NativeLibraryEnvVar, null);
        string? prev = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, lib);
        return new RestoreEnv(OpnaNativeSession.NativeLibraryEnvVar, prev);
    }

    public static FmpPlaybackContext Context(string ovi, int sampleRate, double maxSeconds = 4.0)
    {
        return new FmpPlaybackContext(
            File.ReadAllBytes(ovi), Path.GetFileName(ovi),
            new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi) }),
            sampleRate, SsgGainDb: 0, LoopCount: 1,
            FadeSeconds: 0.5, TailSeconds: 0.1, MaxDurationSeconds: maxSeconds);
    }

    /// <summary>
    /// Runs Pass 1 only (control capture) and returns the finalized capture
    /// with its canonical hash. Mirrors the production capture logic in
    /// <see cref="NativeAudioFmpPcmSession.Boot"/>.
    /// </summary>
    public static (FmpExecutionCapture capture, string hash, int opnaCount, int ppz8Count) CapturePass(
        string ovi, int sampleRate, double maxSeconds, int discardFrames = 4096)
    {
        var ctx = Context(ovi, sampleRate, maxSeconds);
        var builder = new FmpExecutionCaptureBuilder(sampleRate);
        using var legacy = new LegacyMdsoundFmpPcmSession(ctx, builder);
        legacy.CpuClockFrequencyHz = CpuHz;
        legacy.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi));
        legacy.Boot();
        var discard = new short[Math.Max(1, discardFrames) * 2];
        do
        {
            int n = legacy.Render(discard);
            if (n == 0) break;
        }
        while (!legacy.IsCompleted);

        long fadeLen = checked((long)Math.Ceiling(ctx.FadeSeconds * sampleRate));
        var term = legacy.TerminationState;
        bool fadeActive = term != null && term.FadeActive;
        long fadeStart = fadeActive ? term.FadeStartSample : 0;
        long fadeEnd = fadeActive ? checked(term.FadeStartSample + fadeLen) : 0;
        long tailEnd = term != null ? term.StopAtSample : legacy.TotalSamples;

        builder.SetFinalOpnaMasterClock(legacy.FinalOpnaMasterClock);
        var capture = builder.Finish(
            finalOutputFrame: legacy.TotalSamples,
            fadeStartFrame: fadeStart,
            fadeEndFrame: fadeEnd,
            tailEndFrame: tailEnd,
            loopCount: legacy.CurrentLoop,
            terminationReason: legacy.StopReason);

        int opna = 0, ppz8 = 0;
        foreach (var e in capture.Events)
        {
            if (e.Kind == CapturedEventKind.OpnaWrite) opna++;
            else if (e.Kind == CapturedEventKind.Ppz8Command) ppz8++;
        }
        return (capture, CaptureHasher.Sha256(capture), opna, ppz8);
    }

    /// <summary>
    /// Runs the production native-audio session over the given recording device
    /// wrapper and returns the replay report (PCM hash + sanity + zero-read
    /// guarantee via the wrapper counts). Replay event counts equal the capture
    /// event counts, because replay consumes the entire capture.
    /// </summary>
    public static (byte[] pcm, ReplayValidationReport report) ReplayPass(
        string ovi, int sampleRate, double maxSeconds, string captureHash,
        int opnaCount, int ppz8Count, byte muteMask = 0)
    {
        using var lib = WithNativeLibrary();

        var real = NativeOpnaDevice.Open(sampleRate);
        uint abi = real.AbiVersion;
        var recording = new RecordingClockedDevice(real);
        var ctx = Context(ovi, sampleRate, maxSeconds);
        using var session = new NativeAudioFmpPcmSession(ctx, recording, ownsDevice: false);
        session.ReplayMuteMask = muteMask;
        session.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi));
        session.Boot();

        var clip = new MemoryStream();
        var scratch = new short[4096 * 2];
        int guard = 0;
        var sw = Stopwatch.StartNew();
        while (!session.IsCompleted)
        {
            int n = session.Render(scratch);
            if (n <= 0) { if (++guard > 4) break; }
            else guard = 0;
            if (n <= 0) continue;
            var chunk = new byte[n * 4];
            Buffer.BlockCopy(scratch, 0, chunk, 0, chunk.Length);
            clip.Write(chunk);
        }
        sw.Stop();
        var pcm = clip.ToArray();

        var sanity = AudioSanityChecks.Compute(pcm);

        var report = new ReplayValidationReport
        {
            FixtureId = Path.GetFileNameWithoutExtension(ovi),
            OutputRate = sampleRate,
            CaptureHash = captureHash,
            NativeAbiVersion = abi,
            NativeOutputLatency = recording.OutputLatencyFrames,
            ReplayedOpnaEventCount = opnaCount,
            ReplayedPpz8EventCount = ppz8Count,
            NativeStatusReadCount = recording.StatusReadCount,
            NativeIrqReadCount = recording.IrqReadCount,
            OutputFrameCount = pcm.Length / 4,
            PcmSha256 = Sha256(pcm),
            PeakLeft = sanity.PeakLeft,
            PeakRight = sanity.PeakRight,
            RmsLeft = sanity.RmsLeft,
            RmsRight = sanity.RmsRight,
            DcMeanLeft = sanity.DcMeanLeft,
            DcMeanRight = sanity.DcMeanRight,
            ClippedSampleCount = sanity.ClippedSampleCount,
            FirstNonZeroFrame = sanity.FirstNonZeroFrame,
            LastNonZeroFrame = sanity.LastNonZeroFrame,
            RenderDurationSeconds = sw.Elapsed.TotalSeconds,
        };
        return (pcm, report);
    }

    /// <summary>
    /// Renders the production native-audio session with a replay mute mask and
    /// returns interleaved PCM bytes plus the framecount. Used by feature
    /// presence (Workstream H) to prove a component contributes nonzero output.
    /// </summary>
    public static (byte[] pcm, long frames) RenderWithMask(
        string ovi, int sampleRate, double maxSeconds, byte muteMask)
    {
        using var lib = WithNativeLibrary();
        var ctx = Context(ovi, sampleRate, maxSeconds);
        using var session = new NativeAudioFmpPcmSession(ctx);
        session.ReplayMuteMask = muteMask;
        session.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi));
        session.Boot();

        var clip = new MemoryStream();
        var scratch = new short[4096 * 2];
        int guard = 0;
        while (!session.IsCompleted)
        {
            int n = session.Render(scratch);
            if (n <= 0) { if (++guard > 4) break; }
            else guard = 0;
            if (n <= 0) continue;
            var chunk = new byte[n * 4];
            Buffer.BlockCopy(scratch, 0, chunk, 0, chunk.Length);
            clip.Write(chunk);
        }
        var pcm = clip.ToArray();
        return (pcm, pcm.Length / 4);
    }

    private static string Sha256(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
}
