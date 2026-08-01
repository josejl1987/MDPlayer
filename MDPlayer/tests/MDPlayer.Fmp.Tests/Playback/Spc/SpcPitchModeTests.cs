using System.Text;
using Fmp.Cli;
using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Visualization;
using Fmp.Core.Tests.Decoding.SnesDsp;
using Xunit;

// PR 10 tests for the --spc-pitch diagnostic option (§25.3): the CLI parses
// `estimate|relative` (default estimate) and the mode is plumbed through the
// generic PlaybackOptions object into SpcPlaybackBackend instrument building.
// In Relative mode the PR 9 BRR root estimator is NOT called: instruments keep
// PitchAccuracy "relative" and EstimatedRootHz stays null.
namespace MDPlayer.Fmp.Tests.Playback.Spc;

public sealed class SpcPitchModeTests
{
    // ---- CLI parsing: --spc-pitch flows through the generic visualize command ----

    [Fact]
    public void Cli_ParsesSpcPitchRelative()
    {
        var options = VgmVisualizeCommand.Parse(["track.spc", "--spc-pitch", "relative"]);
        Assert.NotNull(options);
        Assert.Equal(SpcPitchMode.Relative, options.SpcPitchMode);
    }

    [Fact]
    public void Cli_ParsesSpcPitchEstimateInline()
    {
        var options = VgmVisualizeCommand.Parse(["track.spc", "--spc-pitch=estimate"]);
        Assert.NotNull(options);
        Assert.Equal(SpcPitchMode.Estimate, options.SpcPitchMode);
    }

    [Fact]
    public void Cli_SpcPitchDefaultsToEstimate()
    {
        var options = VgmVisualizeCommand.Parse(["track.spc"]);
        Assert.NotNull(options);
        Assert.Equal(SpcPitchMode.Estimate, options.SpcPitchMode);
    }

    [Fact]
    public void Cli_RejectsUnknownSpcPitchValue_WithActionableMessage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => VgmVisualizeCommand.Parse(["track.spc", "--spc-pitch", "octave"]));
        Assert.Contains("estimate or relative", ex.Message, StringComparison.Ordinal);
    }

    // ---- Instrument build: relative skips root estimation (§25.3) ----

    [Fact]
    public void RelativeMode_InstrumentBuild_SkipsRootEstimation()
    {
        // Mirrors the backend wiring for SpcPitchMode.Relative
        // (SpcPlaybackBackend.BuildInstruments: enablePitchEstimation = false).
        var builder = new SpcInstrumentBuilder(pitchAccuracy: "relative", enablePitchEstimation: false);
        SpcInstrumentDefinition def = builder.Resolve(BuildSnapshot(), 0);

        Assert.Null(def.EstimatedRootHz);
        Assert.Equal(0, def.RootConfidence);
        Assert.Equal("relative", def.PitchAccuracy);
        Assert.False(string.IsNullOrEmpty(def.SampleHash));
    }

    [Fact]
    public void EstimateMode_InstrumentBuild_RunsRootEstimation()
    {
        // Mirrors the backend wiring for SpcPitchMode.Estimate (the default).
        var builder = new SpcInstrumentBuilder(pitchAccuracy: "relative", enablePitchEstimation: true);
        SpcInstrumentDefinition def = builder.Resolve(BuildSnapshot(), 0);

        // The 400 Hz sine loop is strongly periodic -> a real estimated root.
        Assert.True(def.EstimatedRootHz.HasValue, $"expected an estimated root, got null");
        Assert.InRange(def.RootConfidence, 0, 1);
        Assert.Equal("estimated", def.PitchAccuracy);
    }

    // ---- End-to-end plumbing through SpcPlaybackBackend.Open ----

    [Fact]
    public void Backend_RelativeMode_AllInstrumentsHaveNoEstimatedRoot()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcPitchModeTempFile.Write(BuildSineSpcFile());

        using SpcPlaybackSession session = (SpcPlaybackSession)new SpcPlaybackBackend().Open(
            new FileInfo(f.Path),
            new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 0.5, SpcPitchMode: SpcPitchMode.Relative),
            new Sink());

        Assert.NotEmpty(session.Instruments);
        Assert.All(session.Instruments, def => Assert.Null(def.EstimatedRootHz));
        Assert.All(session.Instruments, def => Assert.Equal("relative", def.PitchAccuracy));
    }

    [Fact]
    public void Backend_EstimateMode_AtLeastOneInstrumentHasEstimatedRoot()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcPitchModeTempFile.Write(BuildSineSpcFile());

        // No SpcPitchMode argument -> default SpcPitchMode.Estimate.
        using SpcPlaybackSession session = (SpcPlaybackSession)new SpcPlaybackBackend().Open(
            new FileInfo(f.Path),
            new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 0.5),
            new Sink());

        Assert.NotEmpty(session.Instruments);
        Assert.Contains(session.Instruments, def => def.EstimatedRootHz.HasValue);
    }

    // ---- Fixtures (no external files, no audio device) ----

    /// <summary>
    /// Snapshot whose voice 0 references a 400 Hz looping sine BRR chain, so
    /// root estimation deterministically yields an "estimated" root (per the
    /// PR 9 fixture convention in BrrPitchEstimatorTests).
    /// </summary>
    private static SpcSnapshot BuildSnapshot()
    {
        byte[] blocks = TestBrrEncoder.EncodeSineBlocks(
            TestBrrEncoder.GenerateSine(400, BrrDecoder.SampleRateHz, 20000, 288));
        var ram = new byte[0x10000];
        blocks.CopyTo(ram, 0x0100);
        ram[0x0200] = 0x00; // DIR entry srcn 0: start 0x0100
        ram[0x0201] = 0x01;
        ram[0x0202] = 0x00; // loop 0x0100
        ram[0x0203] = 0x01;

        var dsp = new byte[0x80];
        dsp[0x5D] = 0x02; // DIR register -> directory at 0x0200
        dsp[0x04] = 0x00; // voice 0 srcn
        dsp[0x05] = 0xFF; // voice 0 adsr0
        dsp[0x06] = 0xE0; // voice 0 adsr1

        return new SpcSnapshot(
            new SpcMetadata(),
            new SpcCpuRegisters(0, 0, 0, 0, 0, 0),
            ram,
            dsp,
            Array.Empty<string>());
    }

    /// <summary>
    /// Full in-memory .spc file (valid signature, GME-loadable, CPU hangs at
    /// 0x0200) whose voice 0 references the 400 Hz looping sine at RAM 0x0400.
    /// </summary>
    private static byte[] BuildSineSpcFile()
    {
        const int size = 0x10200;
        var spc = new byte[size];
        Encoding.ASCII.GetBytes(SpcSignature).CopyTo(spc, 0);
        spc[0x23] = 0x30; // format
        spc[0x24] = 1;    // version
        spc[0x25] = 0x00; // PC = 0x0200
        spc[0x26] = 0x02;
        spc[0x2B] = 0xFF; // SP

        int pc = 0x100 + 0x0200;
        spc[pc + 0] = 0x2F; spc[pc + 1] = 0xFE; // hang: bra hang

        // DIR entry for source 0 at 0x0300: start = loop = 0x0400.
        spc[0x100 + 0x0300] = 0x00; spc[0x100 + 0x0301] = 0x04;
        spc[0x100 + 0x0302] = 0x00; spc[0x100 + 0x0303] = 0x04;

        // BRR sine loop at 0x0400.
        byte[] blocks = TestBrrEncoder.EncodeSineBlocks(
            TestBrrEncoder.GenerateSine(400, BrrDecoder.SampleRateHz, 20000, 288));
        blocks.CopyTo(spc, 0x100 + 0x0400);

        // DSP registers (linear file layout at 0x10100).
        spc[0x10100 + 0x0C] = 0x7F; // mvoll
        spc[0x10100 + 0x1C] = 0x7F; // mvolr
        spc[0x10100 + 0x00] = 0x7F; // voice 0 voll
        spc[0x10100 + 0x01] = 0x7F; // voice 0 volr
        spc[0x10100 + 0x03] = 0x10; // voice 0 pitchh = 0x1000
        spc[0x10100 + 0x04] = 0x00; // voice 0 srcn
        spc[0x10100 + 0x05] = 0xFF; // voice 0 adsr0
        spc[0x10100 + 0x06] = 0xE0; // voice 0 adsr1
        spc[0x10100 + 0x4C] = 0x00; // kon: 0 (silent load is fine)
        spc[0x10100 + 0x5D] = 0x03; // dir
        spc[0x10100 + 0x6C] = 0x00; // flg
        return spc;
    }

    private const string SpcSignature = "SNES-SPC700 Sound File Data v0.30";

    // ---- Native library helpers (mirror SpcNativeSessionTests) ----

    private static string ResolveBuiltNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", SpcNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string RequireNativeLibrary()
    {
        string lib = ResolveBuiltNativeLibrary();
        Assert.True(lib != null && File.Exists(lib),
            "Native SPC library not built. Run: cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build && cmake --build native/MDPlayer.SpcNative/build");
        return lib;
    }

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(SpcNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(SpcNativeSession.NativeLibraryEnvVar, path);
        return new RestoreEnv(SpcNativeSession.NativeLibraryEnvVar, previous);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public RestoreEnv(string name, string previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class Sink : IPlaybackEventSink
    {
        public void OnDevice(in DeviceDescriptor d) { }
        public void OnChipWrite(in TimedChipWrite w) { }
        public void OnMidi(in TimedMidiMessage m) { }
        public void OnSampleAsset(in TimedSampleAssetEvent a) { }
        public void OnLoopBoundary(in TimedLoopBoundary l) { }
    }
}

/// <summary>Writes an SPC byte[] to a unique temp file for backend Open tests.</summary>
internal sealed class SpcPitchModeTempFile : IDisposable
{
    public string Path { get; }

    private SpcPitchModeTempFile(byte[] data)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spc-pitch-{Guid.NewGuid():N}.spc");
        File.WriteAllBytes(Path, data);
    }

    public static SpcPitchModeTempFile Write(byte[] data) => new(data);

    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { }
    }
}
