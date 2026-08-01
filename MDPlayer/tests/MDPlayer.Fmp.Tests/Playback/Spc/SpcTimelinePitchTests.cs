using System.Text;
using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Tests.Decoding.SnesDsp;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Spc;

/// <summary>
/// §25.3 end-to-end: the SPC timeline must place notes at the SOUNDING pitch —
/// estimated BRR root + relative S-DSP semitones — not at raw relative values.
/// Regression tests for "SPC visualization doesn't match the music": notes
/// landed near MIDI 0 regardless of the actual sample root.
/// </summary>
public sealed class SpcTimelinePitchTests
{
    [Fact]
    public void ResolveSourceRoots_EstimatesEveryValidDirectorySource()
    {
        var builder = new SpcInstrumentBuilder(pitchAccuracy: "relative", enablePitchEstimation: true);

        IReadOnlyList<SpcSourceRootInfo> roots = builder.ResolveSourceRoots(BuildSnapshot());

        SpcSourceRootInfo root = Assert.Single(roots);
        Assert.Equal(0, root.SourceNumber);
        Assert.True(root.EstimatedRootHz.HasValue);
        Assert.Equal(400.0, root.EstimatedRootHz.Value, 0);
        Assert.Equal("estimated", root.Accuracy);
    }

    [Fact]
    public void EstimateMode_TimelineNote_SitsAtSoundingPitch()
    {
        // 400 Hz sine at unity pitch must land near midi 67.35 (400 Hz),
        // not at the A4 anchor and not near MIDI 0.
        var timeline = RenderTimeline(SpcPitchMode.Estimate);
        NoteEvent note = Assert.Single(timeline.Notes);
        double expectedMidi = 69.0 + 12.0 * Math.Log2(400.0 / 440.0);
        Assert.Equal(expectedMidi, note.InitialMidiNote, 1);
        Assert.Equal(400.0, note.InitialFrequencyHz, 0);
    }

    [Fact]
    public void RelativeMode_TimelineNote_KeepsA4Anchor()
    {
        var timeline = RenderTimeline(SpcPitchMode.Relative);
        NoteEvent note = Assert.Single(timeline.Notes);
        Assert.Equal(69.0, note.InitialMidiNote, 3);
        Assert.Equal(440.0, note.InitialFrequencyHz, 2);
    }

    private static VisualizationTimeline RenderTimeline(SpcPitchMode mode)
    {
        string lib = RequireNativeLibrary();
        string previous = Environment.GetEnvironmentVariable(SpcNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(SpcNativeSession.NativeLibraryEnvVar, lib);
        try
        {
            string path = Path.Combine(Path.GetTempPath(), $"spc-pitch-timeline-{Guid.NewGuid():N}.spc");
            File.WriteAllBytes(path, BuildSineSpcFile());
            try
            {
                var sink = new TimelineDecoderEventSink(SpcPlaybackBackend.NativeSampleRate);
                using var session = (SpcPlaybackSession)new SpcPlaybackBackend().Open(
                    new FileInfo(path),
                    new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 1.0, SpcPitchMode: mode),
                    sink);
                return sink.Complete(SpcPlaybackBackend.NativeSampleRate);
            }
            finally { try { File.Delete(path); } catch { } }
        }
        finally { Environment.SetEnvironmentVariable(SpcNativeSession.NativeLibraryEnvVar, previous); }
    }

    /// <summary>
    /// Snapshot whose directory source 0 references a 400 Hz looping sine;
    /// every other directory entry is zero-filled (absent).
    /// </summary>
    private static SpcSnapshot BuildSnapshot()
    {
        byte[] blocks = TestBrrEncoder.EncodeSineBlocks(
            TestBrrEncoder.GenerateSine(400, BrrDecoder.SampleRateHz, 12000, 288));
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
    /// Full in-memory .spc whose CPU keys on voice 0 (400 Hz sine, unity
    /// pitch) at startup and then hangs.
    /// </summary>
    private static byte[] BuildSineSpcFile()
    {
        const int size = 0x10200;
        var spc = new byte[size];
        Encoding.ASCII.GetBytes("SNES-SPC700 Sound File Data v0.30").CopyTo(spc, 0);
        spc[0x23] = 0x30; // format
        spc[0x24] = 1;    // version
        spc[0x25] = 0x00; // PC = 0x0200
        spc[0x26] = 0x02;
        spc[0x2B] = 0xFF; // SP

        // CPU at 0x0200: write KON=1 via $F2/$F3, then hang.
        int pc = 0x100 + 0x0200;
        spc[pc + 0] = 0x8F; spc[pc + 1] = 0x4C; spc[pc + 2] = 0xF2;
        spc[pc + 3] = 0x8F; spc[pc + 4] = 0x01; spc[pc + 5] = 0xF3;
        spc[pc + 6] = 0x2F; spc[pc + 7] = 0xFE;

        // DIR entry for source 0 at 0x0300: start = loop = 0x0400.
        spc[0x100 + 0x0300] = 0x00; spc[0x100 + 0x0301] = 0x04;
        spc[0x100 + 0x0302] = 0x00; spc[0x100 + 0x0303] = 0x04;

        // 400 Hz looping sine BRR at 0x0400.
        byte[] blocks = TestBrrEncoder.EncodeSineBlocks(
            TestBrrEncoder.GenerateSine(400, BrrDecoder.SampleRateHz, 12000, 288));
        blocks.CopyTo(spc, 0x100 + 0x0400);

        spc[0x10100 + 0x0C] = 0x7F; // mvoll
        spc[0x10100 + 0x1C] = 0x7F; // mvolr
        spc[0x10100 + 0x00] = 0x7F; // voice 0 voll
        spc[0x10100 + 0x01] = 0x7F; // voice 0 volr
        spc[0x10100 + 0x03] = 0x10; // voice 0 pitchh = 0x1000
        spc[0x10100 + 0x04] = 0x00; // voice 0 srcn
        spc[0x10100 + 0x05] = 0xFF; // voice 0 adsr0
        spc[0x10100 + 0x06] = 0xE0; // voice 0 adsr1
        spc[0x10100 + 0x4C] = 0x00; // kon: CPU-driven
        spc[0x10100 + 0x5D] = 0x03; // dir
        spc[0x10100 + 0x6C] = 0x00; // flg
        return spc;
    }

    private static string RequireNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", SpcNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        Assert.Fail("Native SPC library not built.");
        return null;
    }
}
