using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Real-FMP native-audio integration. The <see cref="NativeAudioFmpPcmSession"/>
/// runs the existing legacy MDSound (FMP) path to completion as Pass 1, then
/// replays the captured register trace through the native YM2608 device and the
/// shared PPZ8 renderer (Pass 2). These tests prove the two-pass backend boots,
/// renders to completion with nonzero deterministic PCM, and that the native
/// device output-latency fixture remains correct.
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

    private static byte[] RenderAllToPcm(string ovi, int sampleRate = 44_100)
    {
        var context = new FmpPlaybackContext(
            File.ReadAllBytes(ovi),
            Path.GetFileName(ovi),
            new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi) }),
            sampleRate,
            SsgGainDb: 0,
            LoopCount: 1,
            FadeSeconds: 0.0,
            TailSeconds: 0.0,
            MaxDurationSeconds: 8.0);

        using var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.NativeAudio, context);
        session.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi));
        session.Boot();

        var buffer = new List<byte>();
        var scratch = new short[4096 * 2];
        long totalFrames = 0;
        int guard = 0;
        int chunkFrames = scratch.Length / 2;
        while (!session.IsCompleted)
        {
            int n = session.Render(scratch);
            if (n <= 0)
            {
                if (++guard > 4) throw new InvalidOperationException("native-audio render made no progress");
                break;
            }
            guard = 0;
            totalFrames += n;
            var chunk = new byte[n * 4];
            Buffer.BlockCopy(scratch, 0, chunk, 0, chunk.Length);
            buffer.AddRange(chunk);
            if (totalFrames > 8_000_000) break; // safety
        }
        return buffer.ToArray();
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void Native_Boot_RunsCaptureAndBuildsReplayState()
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        var context = new FmpPlaybackContext(
            File.ReadAllBytes(ovi), Path.GetFileName(ovi),
            new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi) }),
            44100, SsgGainDb: 0, LoopCount: 1, FadeSeconds: 0.0, TailSeconds: 0.0,
            MaxDurationSeconds: 4.0);
        using var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.NativeAudio, context);
        session.LoadTrack(File.ReadAllBytes(ovi), Path.GetFileName(ovi));
        session.Boot();
        Assert.Equal(44100, session.OutputSampleRate);
    }

    [Fact]
    public void Native_FullRender_NonzeroDeterministicPcm()
    {
        var (available, ovi, _) = Fixtures();
        if (!available) return;
        using var lib = UseNativeLibrary();

        byte[] pcm = RenderAllToPcm(ovi);
        Assert.NotEmpty(pcm);

        // Nonzero: a real FMP track must produce actual audio, not silence.
        bool nonZero = false;
        for (int i = 0; i < pcm.Length; i += 2)
        {
            if (pcm[i] != 0 || pcm[i + 1] != 0) { nonZero = true; break; }
        }
        Assert.True(nonZero, "native-audio render produced silent PCM");

        // Same-platform determinism: two independent replay sessions agree.
        byte[] again = RenderAllToPcm(ovi);
        Assert.Equal(Sha256(pcm), Sha256(again));
    }

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
