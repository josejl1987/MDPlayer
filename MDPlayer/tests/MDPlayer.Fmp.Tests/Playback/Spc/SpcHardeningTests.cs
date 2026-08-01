using System.Text;
using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Visualization;
using Xunit;

// PR 10 malformed-input hardening tests (spec §31.3). Every failure must be
// bounded and actionable: assert the exception type AND that the message
// contains the expected diagnostic text.
//
// References to existing coverage:
//   - Event overflow: SpcVoiceStateTests.EventOverflow_IsReportedWithoutFailure
//     (PR 3); re-asserted below as a regression guard.
//   - Native library missing: SpcNativeSessionTests.Open_NativeLibraryMissing_ThrowsActionableError
//     (PR 2); re-asserted at the wrapper level below.
//   - Truncated file (managed parse): PR 1 probe tests; the native-open path is
//     added below.
namespace MDPlayer.Fmp.Tests.Playback.Spc;

public sealed class SpcHardeningTests
{
    // ---- Truncated file (§31.3.1) ----

    [Fact]
    public void ManagedParse_TruncatedFile_ThrowsActionable()
    {
        byte[] truncated = ValidSpc().Take(0x10000).ToArray(); // below 0x10180 minimum

        var ex = Assert.Throws<SpcFormatException>(() => SpcSnapshot.Parse(truncated));
        Assert.Contains("too small", ex.Message, StringComparison.Ordinal);
        Assert.Contains("minimum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeOpen_TruncatedFile_ThrowsBoundedError()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        byte[] truncated = ValidSpc().Take(0x10000).ToArray();

        var ex = Assert.Throws<SpcFormatException>(() =>
            SpcNativeSession.Open(truncated, SpcNativeSession.OpenOptions.Default));
        Assert.Contains("SPC native open failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("minimum size", ex.Message, StringComparison.Ordinal);
    }

    // ---- Invalid signature (§31.3.2) ----

    [Fact]
    public void ManagedParse_InvalidSignature_ThrowsActionable()
    {
        byte[] bad = (byte[])ValidSpc().Clone();
        bad[0] = (byte)'X';

        var ex = Assert.Throws<SpcFormatException>(() => SpcSnapshot.Parse(bad));
        Assert.Contains("signature mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeOpen_InvalidSignature_ThrowsBoundedError()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        byte[] bad = (byte[])ValidSpc().Clone();
        bad[0] = (byte)'X';

        var ex = Assert.Throws<SpcFormatException>(() =>
            SpcNativeSession.Open(bad, SpcNativeSession.OpenOptions.Default));
        Assert.Contains("SPC native open failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("signature", ex.Message, StringComparison.Ordinal);
    }

    // ---- Oversized tag data: junk after the DSP register block (§31.3.3) ----

    [Fact]
    public void ManagedParse_OversizedTrailingJunk_IsIgnored()
    {
        byte[] oversized = ValidSpc().Concat(Enumerable.Repeat((byte)0xFF, 0x1000)).ToArray();

        SpcSnapshot snapshot = SpcSnapshot.Parse(oversized); // must not throw
        Assert.Equal(0x10000, snapshot.Ram.Length);
        Assert.Equal(0x80, snapshot.DspRegisters.Length);
    }

    [Fact]
    public void NativeOpen_OversizedTrailingJunk_IsAcceptedAndRendersBounded()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        byte[] oversized = ValidSpc().Concat(Enumerable.Repeat((byte)0xFF, 0x1000)).ToArray();

        using SpcNativeSession native = SpcNativeSession.Open(oversized, SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];
        int frames = native.Render(stereo, SpcNativeSession.DefaultBlockFrames);
        Assert.True(frames > 0, "expected the native core to render the valid prefix");
    }

    // ---- Invalid RAM/sample directory: DIR pointing out of bounds (§31.3.4) ----

    [Fact]
    public void BrrSampleReader_OutOfBoundsStartAddress_ReturnsValidFalse_NoThrow()
    {
        var ram = new byte[0x10000];

        // Start near the end of RAM: the first block wraps past 0xFFFF (16-bit
        // wrap is allowed per §14.2) or hits the cycle/limits guard; the chain
        // terminates safely with Valid=false and never throws.
        BrrSample sample = BrrSampleReader.Read(ram, 0xFFFE, 0);
        Assert.False(sample.Valid);
    }

    [Fact]
    public void InstrumentBuilder_InvalidDirectoryOutOfBounds_NoThrow()
    {
        byte[] ram = new byte[0x10000];
        var dsp = new byte[0x80];
        dsp[0x5D] = 0xFF; // DIR -> directory base 0xFF00
        dsp[0x04] = 0xFF; // srcn 255 -> entry offset 0x102FC crosses 0xFFFF
        var snapshot = new SpcSnapshot(
            new SpcMetadata(),
            new SpcCpuRegisters(0, 0, 0, 0, 0, 0),
            ram,
            dsp,
            Array.Empty<string>());

        var builder = new SpcInstrumentBuilder();
        SpcInstrumentDefinition def = builder.Resolve(snapshot, 0); // must not throw
        Assert.Null(def.EstimatedRootHz);
        Assert.Equal(64, def.SampleHash.Length); // deterministic hash of the empty chain
    }

    // ---- Nonterminating BRR chain: bounded via BrrSampleReader limits (§31.3.5) ----

    [Fact]
    public void BrrSampleReader_NonTerminatingChain_BoundedNoThrow()
    {
        // Direct reader coverage (mirrors BrrSampleReaderTests.LongChainWithoutEndFlag_StopsBounded):
        // a chain with no end flag must stop at MaxBlocks / MaxBytes, never loop forever.
        var ram = new byte[0x10000];
        for (int i = 0; i < BrrSampleReader.MaxBlocks; i++)
        {
            int address = i * BrrSampleReader.BlockSize;
            ram[address] = 0; // no end flag, no loop flag
            for (int j = 1; j < BrrSampleReader.BlockSize; j++)
                ram[address + j] = (byte)j;
        }

        BrrSample sample = BrrSampleReader.Read(ram, 0, 0);
        Assert.False(sample.Valid);
        Assert.Equal(BrrSampleReader.MaxBlocks, sample.EncodedBlocks.Length / BrrSampleReader.BlockSize);
        Assert.True(sample.EncodedBlocks.Length <= BrrSampleReader.MaxBytes);
    }

    [Fact]
    public void InstrumentBuilder_NonTerminatingChain_BoundedNoThrow()
    {
        // Builder path: a DIR entry referencing an endless chain must resolve to
        // a bounded, invalid instrument without throwing.
        var ram = new byte[0x10000];
        for (int i = 0; i < BrrSampleReader.MaxBlocks; i++)
        {
            int address = 0x0100 + i * BrrSampleReader.BlockSize;
            ram[address] = 0; // no end flag
            for (int j = 1; j < BrrSampleReader.BlockSize; j++)
                ram[address + j] = (byte)j;
        }
        ram[0x0200] = 0x00; // DIR entry srcn 0: start 0x0100
        ram[0x0201] = 0x01;
        ram[0x0202] = 0x00;
        ram[0x0203] = 0x01;
        var dsp = new byte[0x80];
        dsp[0x5D] = 0x02;
        dsp[0x04] = 0x00;
        var snapshot = new SpcSnapshot(
            new SpcMetadata(),
            new SpcCpuRegisters(0, 0, 0, 0, 0, 0),
            ram,
            dsp,
            Array.Empty<string>());

        var builder = new SpcInstrumentBuilder();
        SpcInstrumentDefinition def = builder.Resolve(snapshot, 0); // must not throw
        Assert.Equal(64, def.SampleHash.Length);
        Assert.Null(def.EstimatedRootHz);
    }

    // ---- Event overflow (§31.3.6; reference PR 3 SpcVoiceStateTests) ----

    [Fact]
    public void EventOverflow_ReportedBoundedWithoutThrow()
    {
        // Reference: SpcVoiceStateTests.EventOverflow_IsReportedWithoutFailure.
        // The fixture key-ons voices 0 and 1 in block 0 -> capacity 1 overflows,
        // and the result reports it instead of throwing or truncating silently.
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using SpcNativeSession native = SpcNativeSession.Open(ValidSpc(), SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];
        var one = new SpcNativeSession.SpcEvent[1];
        SpcNativeSession.SpcRenderResult r =
            native.RenderAndCapture(stereo, SpcNativeSession.DefaultBlockFrames, one);

        Assert.Equal(1, r.EventsWritten);
        Assert.Equal(1, r.EventOverflow);
    }

    // ---- Native ABI mismatch / boundary validation (§31.3.7) ----

    [Fact]
    public void NativeAbi_UndersizedBuffer_ThrowsBounded()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using SpcNativeSession native = SpcNativeSession.Open(ValidSpc(), SpcNativeSession.OpenOptions.Default);
        var tooSmall = new short[100]; // < 256 frames * 2 channels

        var ex = Assert.Throws<ArgumentException>(() => native.Render(tooSmall, 256));
        Assert.Contains("stereo buffer too small", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbi_OutOfRangeBlockFrames_ThrowsBounded()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using SpcNativeSession native = SpcNativeSession.Open(ValidSpc(), SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => native.Render(stereo, 100));
        Assert.Contains("block must be 256..4096", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbi_DisposedSession_ThrowsBounded()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        SpcNativeSession native = SpcNativeSession.Open(ValidSpc(), SpcNativeSession.OpenOptions.Default);
        native.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            native.Render(new short[SpcNativeSession.MinBlockFrames * 2], SpcNativeSession.MinBlockFrames));
    }

    // ---- Native library missing (§31.3.8; reference PR 2 test) ----

    [Fact]
    public void NativeLibraryMissing_ThrowsActionableError()
    {
        // Reference: SpcNativeSessionTests.Open_NativeLibraryMissing_ThrowsActionableError.
        // A set override pointing at a nonexistent path is treated as missing.
        string missing = Path.Combine(
            Path.GetTempPath(), "does-not-exist", SpcNativeSession.NativeLibraryFileName);
        using var restore = SetNativeLibrary(missing);

        var ex = Assert.Throws<SpcFormatException>(() =>
            SpcNativeSession.Open(ValidSpc(), SpcNativeSession.OpenOptions.Default));
        Assert.Contains(SpcNativeSession.NativeLibraryFileName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("runtimes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MDPLAYER_SPC_NATIVE", ex.Message, StringComparison.Ordinal);
    }

    // ---- Fixture: valid, GME-loadable SPC that key-ons voices 0 and 1 ----

    /// <summary>
    /// Synthetic SPC whose SPC700 keys on voices 0 and 1 via $F2/$F3 at frame 0,
    /// then hangs (mirrors SpcVoiceStateTests.VoiceFixture). A looping BRR square
    /// wave lives at 0x0400. DSP regs at file offset 0x10100, linear layout.
    /// </summary>
    private static byte[] ValidSpc()
    {
        const int size = 0x10200;
        var spc = new byte[size];
        Encoding.ASCII.GetBytes(SpcSignature).CopyTo(spc, 0);
        spc[0x23] = 0x30; // format
        spc[0x24] = 1;    // version
        spc[0x25] = 0x00; // pcl -> PC = 0x0200
        spc[0x26] = 0x02; // pch
        spc[0x2B] = 0xFF; // sp

        // CPU at 0x0200: KON write via $F2/$F3, then self-branch.
        int pc = 0x100 + 0x0200;
        spc[pc + 0] = 0x8F; spc[pc + 1] = 0x4C; spc[pc + 2] = 0xF2;
        spc[pc + 3] = 0x8F; spc[pc + 4] = 0x03; spc[pc + 5] = 0xF3;
        spc[pc + 6] = 0x2F; spc[pc + 7] = 0xFE;

        // DIR entry for source 0 at 0x0300: start = loop = 0x0400.
        spc[0x100 + 0x0300] = 0x00;
        spc[0x100 + 0x0301] = 0x04;
        spc[0x100 + 0x0302] = 0x00;
        spc[0x100 + 0x0303] = 0x04;

        // BRR block at 0x0400: header 0xA3 (scale 10, filter 0, end+loop),
        // data 0xF0 -> alternating +max/0 samples (looping square).
        spc[0x100 + 0x0400] = 0xA3;
        for (int i = 0; i < 8; i++)
            spc[0x100 + 0x0401 + i] = 0xF0;

        // DSP registers: volumes, pitch, ADSR, DIR set; KON starts at 0 so the
        // key-on is CPU-driven (observable as a transition).
        for (int v = 0; v < 2; v++)
        {
            int b = v * 0x10;
            spc[0x10100 + b + 0x00] = 0x7F; // voll
            spc[0x10100 + b + 0x01] = 0x7F; // volr
            spc[0x10100 + b + 0x02] = 0x00; // pitchl
            spc[0x10100 + b + 0x03] = 0x10; // pitchh = 0x1000
            spc[0x10100 + b + 0x04] = 0x00; // srcn
            spc[0x10100 + b + 0x05] = 0xFF; // adsr0: ADSR
            spc[0x10100 + b + 0x06] = 0xE0; // adsr1
        }
        spc[0x10100 + 0x0C] = 0x7F; // mvoll
        spc[0x10100 + 0x1C] = 0x7F; // mvolr
        spc[0x10100 + 0x4C] = 0x00; // kon: 0 -> CPU-driven key-on
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
