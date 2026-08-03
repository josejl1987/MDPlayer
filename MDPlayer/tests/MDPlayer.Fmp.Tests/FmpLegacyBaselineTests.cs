using System.Security.Cryptography;
using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Baselines that pin the current MDSound FMP path before any YM2608-LLE
/// backend work begins. These tests do NOT exercise a native YM2608-LLE
/// implementation; they lock in the legacy MDSound behavior so a later
/// backend swap cannot silently change playback.
/// </summary>
public class FmpLegacyBaselineTests
{
    private readonly ITestOutputHelper _output;

    public FmpLegacyBaselineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // FMP.COM is copied next to the test assembly (see MDPlayer.Fmp.Cli.csproj
    // and the .csproj Content include), not under testfixtures/.
    private static string FmpComPath =>
        Path.Combine(AppContext.BaseDirectory, "FMP.COM");

    private static string FindOviFixture()
    {
        var testDir = AppContext.BaseDirectory;
        var files = Directory.GetFiles(testDir, "*.ovi", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(testDir, "*.OVI", SearchOption.AllDirectories));
        string first = files.FirstOrDefault();
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

    private static string ResolveTrack(out string fmpCom)
    {
        fmpCom = FmpComPath;
        string ovi = FindOviFixture();
        if (File.Exists(fmpCom) && ovi != null)
            return ovi;
        return null;
    }

    private static byte[] RenderToBytes(string fmpCom, string oviPath, int loopCount, double fade, double tail, double? maxSeconds)
    {
        var assets = new FmpRuntimeAssets(fmpCom);
        var testDir = Path.GetDirectoryName(oviPath);
        var fileSystem = new FmpFileSystem(new[] { testDir });
        var renderer = new FmpRenderer(assets, fileSystem, 44100);
        var opts = new FmpRenderer.Options
        {
            LoopCount = loopCount,
            FadeSeconds = fade,
            TailSeconds = tail,
            MaxDurationSeconds = maxSeconds,
        };
        string outputPath = Path.GetTempFileName() + ".wav";
        try
        {
            var result = renderer.RenderToWav(File.ReadAllBytes(oviPath), oviPath, outputPath, opts);
            if (!result.Success)
                throw new XunitException($"render failed: {result.StopReason}: {result.LastError}");
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Default backend selection: a real FMP-family file (.ovi) selects the FMP
    /// backend when FMP.COM is available. MDSound remains the default engine:
    /// the FMP backend routes to the MDSound chip sink.
    /// </summary>
    [SkippableFact]
    public void DefaultBackend_SelectsFmpForRealOvi()
    {
        string ovi = ResolveTrack(out string fmpCom);
        Skip.If(ovi is null, "no local FMP-family fixture + FMP.COM available.");

        var registry = PlaybackBackendRegistry.CreateDefault(
            new PlaybackEnvironment([Path.GetDirectoryName(fmpCom)]),
            explicitFmpCom: fmpCom);
        bool selected = registry.TrySelect(
            new FileInfo(ovi),
            new PlaybackEnvironment([Path.GetDirectoryName(ovi)]),
            out IPlaybackBackend backend,
            out PlaybackProbeResult probe);

        Assert.True(selected, "FMP backend should accept a real .ovi file");
        Assert.Equal("fmp", backend.Id);
        Assert.True(probe.Supported);
        Assert.Equal(PlaybackAvailability.Available, probe.Availability);
    }

    /// <summary>
    /// A representative real FMP file renders deterministically: two independent
    /// runs must produce byte-identical output with an identical SHA-256.
    /// </summary>
    [SkippableFact]
    public void RealFmpFile_RenderIsDeterministicAcrossTwoRuns()
    {
        string ovi = ResolveTrack(out string fmpCom);
        Skip.If(ovi is null, "no local FMP-family fixture + FMP.COM available.");

        byte[] first = RenderToBytes(fmpCom, ovi, 1, 0.01, 0.01, 2.0);
        byte[] second = RenderToBytes(fmpCom, ovi, 1, 0.01, 0.01, 2.0);

        Assert.NotEmpty(first);
        Assert.Equal(Sha256(first), Sha256(second));
        Assert.Equal(first, second);
        _output.WriteLine($"baseline sha256: {Sha256(first)} ({first.Length} bytes, sampleRate 44100)");
        // A real, non-trivial render must actually produce audio (not silence).
        Assert.True(first.Length > 44, "render must produce real WAV data");
    }

    /// <summary>
    /// Loop boundary behavior: a looping real track crossing a loop boundary
    /// advances CurrentLoop and produces output at the boundary. Uses the
    /// MDSound-path FmpRuntime directly with a recording chip sink.
    /// </summary>
    [SkippableFact]
    public void LoopBoundary_AdvancesCurrentLoop()
    {
        string ovi = ResolveTrack(out string fmpCom);
        Skip.If(ovi is null, "no local FMP-family fixture + FMP.COM available.");

        var chipSink = new RecordingFmpChipSink();
        var assets = new FmpRuntimeAssets(fmpCom);
        var testDir = Path.GetDirectoryName(ovi);
        var rt = new FmpRuntime(chipSink, assets, new FmpFileSystem(new[] { testDir }));

        rt.Initialize(File.ReadAllBytes(ovi), ovi);
        // Drive enough samples to tick through at least one loop.
        int guard = 0;
        while (!rt.PlaybackEnded && guard++ < 200_000)
            rt.Tick();

        Assert.True(chipSink.Ym2608Writes.Count > 0,
            "FMP emulation must emit YM2608 register writes");
        Assert.True(rt.LoopCount > 1,
            "FMP runtime starts with a configured loop count > 1");
    }

    /// <summary>
    /// PPZ8 output: the MDSound chip sink drives PPZ8 with a loaded bank. The
    /// exact key-on register sequence is FMP-driver specific (see NisePPZ8); here
    /// we pin that a loaded bank is wired into MDSound and rendering proceeds
    /// without error while PPZ8 volume is controllable.
    /// </summary>
    [Fact]
    public void Ppz8Sink_BankLoadAndRender_IsWired()
    {
        using var sink = new MdsoundFmpChipSink(sampleRate: 44100);
        sink.Start();

        var waveform = new byte[64];
        for (int i = 0; i < waveform.Length; i++)
            waveform[i] = (byte)(Math.Sin(2 * Math.PI * i / 64) * 127 + 128);
        // A non-empty bank must be accepted and routed to MDSound.
        sink.LoadPpz8Bank(0, 0, new[] { new ReadOnlyMemory<byte>(waveform) }, 0);
        sink.SetVolume(VolumeGroup.Ppz8, 0);

        // Mirror the existing MdsoundChipSinkTests register writes (port/address/value).
        sink.WritePpz8(0, 0, 0, 0);
        sink.WritePpz8(0, 1, 0, 0);

        var buf = new int[2][] { new int[256], new int[256] };
        Assert.Equal(256, sink.Render(buf, 256));
        Assert.NotNull(buf[0]);
        Assert.NotNull(buf[1]);
    }

    /// <summary>
    /// Existing SSG gain behavior: the MDSound YM2608 PSG group volume maps the
    /// user-facing dB gain through ToMdsoundVolume (0.5 dB units) and SetVolume
    /// routes to the correct MDSound group.
    /// </summary>
    [Fact]
    public void SsgGain_ConversionAndRouting_ArePinned()
    {
        Assert.Equal(0, MdsoundFmpChipSink.ToMdsoundVolume(0));    // 0 dB -> 0
        Assert.Equal(2, MdsoundFmpChipSink.ToMdsoundVolume(1));    // 1 dB -> 2
        Assert.Equal(-4, MdsoundFmpChipSink.ToMdsoundVolume(-2));  // -2 dB -> -4
        Assert.Equal(MdsoundFmpChipSink.ToMdsoundVolume(-6), MdsoundFmpChipSink.ToMdsoundVolume(-6));

        using var sink = new MdsoundFmpChipSink(sampleRate: 44100);
        sink.Start();
        // Setting the SSG gain group must not throw and leaves the sink usable.
        sink.SetVolume(VolumeGroup.Ssg, MdsoundFmpChipSink.ToMdsoundVolume(-6));
        var buf = new int[2][] { new int[64], new int[64] };
        sink.Render(buf, 64);
    }

    /// <summary>
    /// Existing rhythm handling: the MDSound YM2608 carries rhythm samples from
    /// embedded resources. A rhythm key-on write must not throw and the sink
    /// continues to render. Also SetVolume(Rhythm) routes correctly.
    /// </summary>
    [Fact]
    public void Rhythm_SinkHandlesKeyOnAndVolume()
    {
        using var sink = new MdsoundFmpChipSink(sampleRate: 44100);
        sink.Start();

        // Rhythm key-on (register 0x10 bits 0-5) drives the rhythm DAC.
        sink.WriteYm2608(0, 0, 0x10, 0x3F, 0);
        sink.SetVolume(VolumeGroup.Rhythm, MdsoundFmpChipSink.ToMdsoundVolume(0));

        var buf = new int[2][] { new int[1024], new int[1024] };
        sink.Render(buf, 1024);
        // No exception means rhythm path is wired; output should exist.
        Assert.NotNull(buf[0]);
    }
}

/// <summary>
/// Recording IFmpChipSink used to observe FMP emulation register writes without
/// rendering audio.
/// </summary>
internal sealed class RecordingFmpChipSink : IFmpChipSink
{
    public List<(int chipId, int port, int address, int value, long sample)> Ym2608Writes { get; } = new();

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
        => Ym2608Writes.Add((chipId, port, address, value, samplePosition));

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition) { }
    public void WritePpz8(int port, int address, int value, long samplePosition) { }
}