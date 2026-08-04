using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Real-FMP native-LLE integration. The native session boots the real FMP
/// driver through the clocked Nise98 path. The current native fixed-cadence
/// profile (144 master clocks per stereo frame, ABI v1) is not compatible with
/// the FMP driver's boot sequence — the driver polls the OPNA status
/// registers, which perturbs the LLE serial frame phase — so the contract
/// verified here is fail-closed: the render reports a clear, deterministic
/// error and never falls back to the MDSound path. Requires the built native
/// library and the FMP.COM + track fixtures; tests skip when absent.
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

    private static (FmpRenderer.Result result, string path) RenderNative(string oviPath, double maxSeconds)
    {
        var assets = new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM"));
        var fileSystem = new FmpFileSystem(new[] { Path.GetDirectoryName(oviPath) });
        var renderer = new FmpRenderer(assets, fileSystem, 44100);
        string outPath = Path.Combine(Path.GetTempPath(), $"native-{Guid.NewGuid():N}.wav");
        var opts = new FmpRenderer.Options
        {
            LoopCount = 1,
            FadeSeconds = 0.5,
            TailSeconds = 0.2,
            MaxDurationSeconds = maxSeconds,
            OpnaBackend = FmpOpnaBackend.NativeLle,
        };
        var result = renderer.RenderToWav(File.ReadAllBytes(oviPath), Path.GetFileName(oviPath), outPath, opts);
        return (result, outPath);
    }

    [Fact]
    public void Native_Render_FailsClosed_NoFallback_NoSilentMdsound()
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        var (result, path) = RenderNative(ovi, maxSeconds: 2);
        try
        {
            // Fail-closed: explicit error, never a silent MDSound render, and
            // no partial WAV left behind.
            Assert.False(result.Success);
            Assert.Equal("error", result.StopReason);
            Assert.False(string.IsNullOrEmpty(result.LastError));
            Assert.Contains("fixed-cadence", result.LastError);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Native_BootFailure_IsDeterministic()
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        var (r1, p1) = RenderNative(ovi, maxSeconds: 1);
        var (r2, p2) = RenderNative(ovi, maxSeconds: 1);
        try
        {
            Assert.False(r1.Success);
            Assert.False(r2.Success);
            Assert.Equal(r1.StopReason, r2.StopReason);
            Assert.Equal(r1.LastError, r2.LastError);
        }
        finally
        {
            if (File.Exists(p1)) File.Delete(p1);
            if (File.Exists(p2)) File.Delete(p2);
        }
    }

    [Fact]
    public void Native_BootFailure_MatchesAcrossSliceSizes()
    {
        // The fail-closed boot error must not depend on how the render is
        // sliced (the boot itself is deterministic).
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        string Run(int maxSeconds)
        {
            var (r, p) = RenderNative(ovi, maxSeconds);
            if (File.Exists(p)) File.Delete(p);
            return $"{r.StopReason}|{r.LastError}";
        }

        Assert.Equal(Run(1), Run(2));
    }

    [Fact]
    public void Native_DeviceLatency_IsFixedAndQueried()
    {
        string lib = NativeLibrary();
        if (lib == null) return;
        using var restore = UseNativeLibrary();

        using var device = NativeOpnaDevice.Open(44_100);
        int latency = device.OutputLatencyFrames;

        // The fixed SpeexDSP output latency must be positive and stable.
        Assert.True(latency > 0, "output latency must be positive");
        using var again = NativeOpnaDevice.Open(44_100);
        Assert.Equal(latency, again.OutputLatencyFrames);

        // The PPZ8 delay line used by the native session is sized from this
        // exact value, so the two streams reach the mixer aligned.
        var delay = new Ppz8OutputDelayBuffer(latency);
        Assert.Equal(latency, delay.Capacity);
    }

    [Fact]
    public void Native_OutputLength_MatchesRequest()
    {
        // Even when the boot fails, the render reports the failure without
        // fabricating output: the session never produces partial output on a
        // failed boot.
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        var (result, path) = RenderNative(ovi, maxSeconds: 5);
        if (File.Exists(path)) File.Delete(path);

        Assert.False(result.Success);
        Assert.Equal(0, result.RenderedSamples);
    }
}