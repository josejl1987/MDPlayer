using Fmp.Cli;
using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Tests for the --ssg-gain-db audio-mix flag: CLI parsing/validation, the
/// decibel-to-MDSound-unit conversion, and the synthesis-level effect on SSG
/// output (with FM left unchanged). The flag changes audio balance via MDSound's
/// group volume API, not Corrscope waveform amplification.
/// </summary>
public class SsgGainTests
{
    // ---- CLI parsing -------------------------------------------------------

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-6", -6)]
    [InlineData("3.5", 3.5)]
    public void SsgGainDb_IsParsed(string raw, double expected)
    {
        var options = VisualizeCommand.ParseArgs(["track.ovi", "--ssg-gain-db", raw]);
        Assert.NotNull(options);
        Assert.Equal(expected, options.SsgGainDb);
    }

    [Fact]
    public void SsgGainDb_DefaultsToZero()
    {
        var options = VisualizeCommand.ParseArgs(["track.ovi"]);
        Assert.NotNull(options);
        Assert.Equal(0, options.SsgGainDb);
    }

    [Theory]
    [InlineData("--ssg-gain-db", "-61")]   // below -60
    [InlineData("--ssg-gain-db", "13")]    // above +12
    public void SsgGainDb_OutOfRange_IsRejected(string flag, string value)
    {
        // ParseArgs prints to stderr and returns null on ArgumentException.
        Assert.Null(VisualizeCommand.ParseArgs(["track.ovi", flag, value]));
    }

    // ---- unit conversion ---------------------------------------------------

    /// <summary>
    /// MDSound (fmgen) uses 0.5-dB units: volume = scale * 10^(vol/40).
    /// One decibel therefore equals two MDSound units.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-6, -12)]
    [InlineData(3.5, 7)]
    [InlineData(12, 24)]
    [InlineData(-60, -120)]
    public void ToMdsoundVolume_ConvertsDecibelsToHalfDbUnits(double gainDb, int expected)
    {
        Assert.Equal(expected, MdsoundFmpChipSink.ToMdsoundVolume(gainDb));
    }

    // ---- synthesis-level effect -------------------------------------------

    /// <summary>
    /// Render a steady SSG square tone with a fixed amplitude-register value
    /// and return the stereo RMS (averaged over both channels). The amplitude
    /// register (0x08) is used directly so the only variable between renders
    /// is the SSG group volume set by --ssg-gain-db.
    /// </summary>
    private static double RenderSsgRms(double ssgGainDb)
    {
        using var sink = new MdsoundFmpChipSink(sampleRate: 44100, ssgGainDb: ssgGainDb);
        sink.Start();

        // SSG channel 0: square wave, period = 0x0100 (coarse=1, fine=0),
        // tone enabled, amplitude = 15 (max, fixed — no envelope).
        // reg 0x07 mixer: clear bit 0 to enable tone on ch0, set the others.
        sink.WriteYm2608(0, 0, 0x07, 0b111110, 0); // tone on ch0 only
        sink.WriteYm2608(0, 0, 0x00, 0x00, 0);     // ch0 fine period
        sink.WriteYm2608(0, 0, 0x01, 0x01, 0);     // ch0 coarse period
        sink.WriteYm2608(0, 0, 0x08, 0x0F, 0);     // ch0 amplitude = 15 (fixed)

        // Warm up so the tone is steady, then measure.
        var warm = new int[2][] { new int[2048], new int[2048] };
        sink.Render(warm, 2048);

        const int N = 8192;
        var outputs = new int[2][] { new int[N], new int[N] };
        sink.Render(outputs, N);

        double sumSq = 0;
        for (int i = 0; i < N; i++)
        {
            double l = outputs[0][i];
            double r = outputs[1][i];
            sumSq += (l * l + r * r);
        }
        return Math.Sqrt(sumSq / (N * 2));
    }

    [Fact]
    public void SsgGainDb_Negative6Db_HalvesSsgAmplitude()
    {
        double rms0 = RenderSsgRms(0);
        double rms6 = RenderSsgRms(-6);

        // -6 dB should be an amplitude ratio of ~0.5. Allow tolerance for
        // quantization and the fixed-point PSG emit table.
        Assert.True(rms0 > 0, "SSG output at 0 dB must be non-silent");
        double ratio = rms6 / rms0;
        Assert.InRange(ratio, 0.45, 0.55);
    }

    [Fact]
    public void SsgGainDb_DoesNotAffectFmOutput()
    {
        double fmRms0 = RenderFmRms(ssgGainDb: 0);
        double fmRms6 = RenderFmRms(ssgGainDb: -6);

        Assert.True(fmRms0 > 0, "FM output must be non-silent");
        // FM RMS must be essentially identical regardless of SSG group volume.
        double relDelta = Math.Abs(fmRms6 - fmRms0) / fmRms0;
        Assert.True(relDelta < 0.02, $"FM RMS changed by {relDelta:P} (0dB={fmRms0}, -6dB={fmRms6})");
    }

    /// <summary>
    /// Render a steady FM tone on channel 0 and return its RMS. The SSG gain is
    /// applied via the constructor but must not touch the FM group. Uses a
    /// simple all-operators-summed (algorithm 7) patch with max levels and a
    /// slow release so the tone sustains audibly after key-on.
    /// </summary>
    private static double RenderFmRms(double ssgGainDb)
    {
        using var sink = new MdsoundFmpChipSink(sampleRate: 44100, ssgGainDb: ssgGainDb);
        sink.Start();

        // Key off channel 0 first.
        sink.WriteYm2608(0, 0, 0x28, 0x00, 0);

        // Algorithm 7 (all four operators summed → bright, simple output),
        // feedback 0. Register 0xB0 = channel 0 feedback/algorithm.
        sink.WriteYm2608(0, 0, 0xB0, 0x07, 0);

        // Operators 1-4 (offsets +0..+3 for channel 0):
        //   0x30: DT=0, MULT=1     0x40: TL=0 (loudest)
        //   0x50: RS=0, AR=31      0x60: AM=0, DR=0
        //   0x70: SR=0             0x80: SL=0, RR=15
        // No SSG-EG; the attack ramps up and the tone sustains.
        for (int op = 0; op < 4; op++)
        {
            sink.WriteYm2608(0, 0, 0x30 + op, 0x01, 0); // DT/MULT
            sink.WriteYm2608(0, 0, 0x40 + op, 0x00, 0); // TL = 0 (max level)
            sink.WriteYm2608(0, 0, 0x50 + op, 0x1F, 0); // RS/AR = max
            sink.WriteYm2608(0, 0, 0x60 + op, 0x00, 0); // AM/DR = 0
            sink.WriteYm2608(0, 0, 0x70 + op, 0x00, 0); // SR = 0
            sink.WriteYm2608(0, 0, 0x80 + op, 0x0F, 0); // SL=0, RR=15
        }

        // Frequency: block 3, fnum 0x400 → a mid audible pitch.
        // 0xA4 = (block << 3) | (fnum >> 8); 0xA0 = fnum & 0xFF.
        int fnum = 0x400;
        int block = 3;
        sink.WriteYm2608(0, 0, 0xA4, (block << 3) | ((fnum >> 8) & 0x07), 0);
        sink.WriteYm2608(0, 0, 0xA0, fnum & 0xFF, 0);

        // Key on channel 0: reg 0x28 low 3 bits select the channel (0 here),
        // the high nibble (data >> 4) is the per-operator key-on mask. 0xF0
        // keys on all four operators of channel 0.
        sink.WriteYm2608(0, 0, 0x28, 0xF0, 0);

        // Generous warm-up so the attack envelope fully opens.
        var warm = new int[2][] { new int[8192], new int[8192] };
        sink.Render(warm, 8192);

        const int N = 16384;
        var outputs = new int[2][] { new int[N], new int[N] };
        sink.Render(outputs, N);

        double sumSq = 0;
        for (int i = 0; i < N; i++)
        {
            double l = outputs[0][i];
            double r = outputs[1][i];
            sumSq += (l * l + r * r);
        }
        return Math.Sqrt(sumSq / (N * 2));
    }

    // ---- ScopeRenderer integration ----------------------------------------

    /// <summary>
    /// The scope renderer must construct every stem sink (master and SSG stems)
    /// with the configured SSG gain. FMP.COM is unavailable in this environment,
    /// but sink construction happens before runtime.Initialize(), so a missing
    /// FMP.COM proves the gain was threaded through every stem sink without
    /// throwing: the exception surfaces from asset loading, not from the
    /// ssgGainDb wiring. We assert the thrown exception is the asset one and
    /// references FMP.COM, not a NullReferenceException or gain-conversion error.
    /// </summary>
    [Fact]
    public void ScopeRenderer_AcceptsSsgGainDb_WithoutThrowing()
    {
        var assets = new FmpRuntimeAssets("/nonexistent/FMP.COM");
        var renderer = new ScopeRenderer(assets, sampleRate: 44100, ssgGainDb: -6);
        Assert.NotNull(renderer);

        var ex = Assert.ThrowsAny<SystemException>(() => renderer.Render(
            new byte[] { 0, 1, 2, 3 },
            "test.ovi",
            Path.Combine(Path.GetTempPath(), "ssggain-" + Guid.NewGuid().ToString("N")),
            skipSilentStems: true));
        // The failure must be the missing-FMP.COM asset error (which occurs
        // after every stem sink was already constructed with the gain), not a
        // gain-related exception.
        Assert.Contains("FMP.COM", ex.Message);
    }
}
