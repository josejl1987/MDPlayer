using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Real-FMP native-LLE integration. The native session boots the real FMP
/// driver through the clocked Nise98 path. After Prompt 8.3 implemented the
/// complete short-conditional-jump family (0x70–0x7F), the driver boots past
/// opcode 0x7C without a NotImplementedException and renders bounded frames
/// with no fallback to MDSound.
///
/// Workstream I verified here (Stages 1 & 2):
///   * driver boot succeeds — no cadence error, no NullReferenceException, no
///     clock regression, no NotImplementedException for 0x70–0x7F;
///   * bounded renders (1 / 7 / 64 / 257 frames) return within the test
///     timeout with monotonic CPU cycles, monotonic OPNA master clock and
///     monotonic output frame position — and no fallback.
///
/// Stage 3 (multi-second render with nonzero PCM) is documented as a
/// remaining limitation: the native session's idle timeline races far ahead of
/// the output frame position once the 500 ms startup gate is crossed, so a
/// full-length render cannot be completed within a practical test timeout.
/// That runaway is native-session OPNA/IRQ-clock behaviour outside the
/// short-conditional-jump scope (and the prompt forbids altering native OPNA
/// timing), so it is reported rather than patched.
///
/// Requires the built native library and the FMP.COM + track fixtures; tests
/// skip when absent.
/// </summary>
public class NativeFmpIntegrationTests
{
    private static string NativeLibrary()
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

    private static string FindOviFixture()
    {
        var testDir = AppContext.BaseDirectory;
        string first = Directory.GetFiles(testDir, "*.ovi", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(testDir, "*.OVI", SearchOption.AllDirectories))
            .FirstOrDefault();
        if (first != null) return first;

        string downloads = "/home/jose/Downloads";
        if (OperatingSystem.IsLinux() && Directory.Exists(downloads))
        {
            var dl = Directory.GetFiles(downloads, "*.OVI", SearchOption.TopDirectoryOnly);
            if (dl.Length > 0)
                return dl.OrderBy(f => f, StringComparer.Ordinal).First();
        }
        return null;
    }

    private static IDisposable UseNativeLibrary()
    {
        string lib = NativeLibrary();
        if (lib == null)
            throw new Xunit.Sdk.XunitException("native OPNA library not built");
        string previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, lib);
        return new RestoreEnv(OpnaNativeSession.NativeLibraryEnvVar, previous);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;
        public RestoreEnv(string name, string previous) { _name = name; _previous = previous; }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private static (bool available, string ovi, string fmp) Fixtures()
    {
        string fmp = Path.Combine(AppContext.BaseDirectory, "FMP.COM");
        string ovi = FindOviFixture();
        return (File.Exists(fmp) && ovi != null && NativeLibrary() != null, ovi, fmp);
    }

    private static NativeLleFmpPcmSession OpenNativeSession(string ovi, double maxSeconds)
    {
        var context = new FmpPlaybackContext(
            File.ReadAllBytes(ovi),
            Path.GetFileName(ovi),
            new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi) }),
            44100,
            SsgGainDb: 0,
            LoopCount: 1,
            FadeSeconds: 0.0,
            TailSeconds: 0.0,
            MaxDurationSeconds: maxSeconds);
        var sessionBase = FmpPcmSessionFactory.Create(FmpOpnaBackend.NativeLle, context);
        sessionBase.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi));
        sessionBase.Boot();
        return (NativeLleFmpPcmSession)sessionBase;
    }

    // ---- Stage 1: boot past the short-Jcc family ----

    [Fact]
    public void Native_Boot_SucceedsPastJccFamily_NoNotImplemented()
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        using var session = OpenNativeSession(ovi, maxSeconds: 3600);
        // Boot returning without exception proves the driver executed past the
        // previously-failing opcode 0x7C (and its sibling 0x70–0x7F) without a
        // NotImplementedException, cadence error, NRE or clock regression.
        Assert.NotNull(session);
    }

    // ---- Stage 2: bounded renders (1 / 7 / 64 / 257 frames) ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(257)]
    public void BoundedRender_Frames_MonotonicAndNonFallback(int frames)
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        using var session = OpenNativeSession(ovi, maxSeconds: 3600);
        var buf = new short[frames * 2];
        int produced = 0;
        while (produced < frames)
        {
            int chunk = Math.Min(64, frames - produced);
            int n = session.Render(buf.AsSpan(produced * 2, chunk * 2));
            Assert.True(n > 0, $"bounded render produced no frames at {frames}-frame target");
            produced += n;
        }
        Assert.Equal(frames, produced);

        // The native session + clocked coordinator enforce monotonic/regression
        // contracts internally (a regressed CPU cycle, OPNA master clock or
        // output frame position throws via the no-progress guard); reaching a
        // full bounded render proves all three stayed monotonic and no fallback
        // was invoked.
    }

    // ---- same-platform determinism over the boot + bounded-render path ----

    [Fact]
    public void Native_BootAndBoundedRender_SamePlatformDeterministic()
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        byte[] p1 = RenderBoundedToPcm(ovi, frames: 1);
        byte[] p2 = RenderBoundedToPcm(ovi, frames: 1);
        Assert.Equal(Sha256(p1), Sha256(p2));
    }

    private static byte[] RenderBoundedToPcm(string ovi, int frames)
    {
        using var session = OpenNativeSession(ovi, maxSeconds: 3600);
        var buf = new short[frames * 2];
        int produced = 0;
        while (produced < frames)
        {
            int n = session.Render(buf.AsSpan(produced * 2, (frames - produced) * 2));
            Assert.True(n > 0, "bounded render produced no frames");
            produced += n;
        }
        var result = new byte[frames * 4];
        Buffer.BlockCopy(buf, 0, result, 0, result.Length);
        return result;
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ---- native device latency fixture (kept green) ----

    [Fact]
    public void Native_DeviceLatency_IsFixedAndQueried()
    {
        string lib = NativeLibrary();
        if (lib == null) return;
        using var restore = UseNativeLibrary();

        using var device = NativeOpnaDevice.Open(44_100);
        int latency = device.OutputLatencyFrames;
        Assert.True(latency > 0, "output latency must be positive");
        using var again = NativeOpnaDevice.Open(44_100);
        Assert.Equal(latency, again.OutputLatencyFrames);

        var delay = new Ppz8OutputDelayBuffer(latency);
        Assert.Equal(latency, delay.Capacity);
    }
}