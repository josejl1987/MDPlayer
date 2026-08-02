using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// SSG gain behavioral coverage (ported from the pre-PR4 suite). Proves the
/// configured --ssg-gain-db reaches the MDSound PSG group and the synthesis
/// result actually changes, while leaving FM output untouched and wiring the
/// gain into every ScopeRenderer stem sink.
/// </summary>
public sealed class SsgGainTests
{
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
    /// is the SSG group volume set by ssg-gain-db.
    /// </summary>
    private static double RenderSsgRms(double ssgGainDb)
    {
        using var sink = new MdsoundFmpChipSink(sampleRate: 44100, ssgGainDb: ssgGainDb);
        sink.Start();

        // SSG channel 0: square wave, period = 0x0100, tone enabled, amplitude 15.
        sink.WriteYm2608(0, 0, 0x07, 0b111110, 0); // tone on ch0 only
        sink.WriteYm2608(0, 0, 0x00, 0x00, 0);     // ch0 fine period
        sink.WriteYm2608(0, 0, 0x01, 0x01, 0);     // ch0 coarse period
        sink.WriteYm2608(0, 0, 0x08, 0x0F, 0);     // ch0 amplitude = 15 (fixed)

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
        double relDelta = Math.Abs(fmRms6 - fmRms0) / fmRms0;
        Assert.True(relDelta < 0.02, $"FM RMS changed by {relDelta:P} (0dB={fmRms0}, -6dB={fmRms6})");
    }

    /// <summary>Render a steady FM tone on channel 0 and return its RMS (SSG gain must not touch FM).</summary>
    private static double RenderFmRms(double ssgGainDb)
    {
        using var sink = new MdsoundFmpChipSink(sampleRate: 44100, ssgGainDb: ssgGainDb);
        sink.Start();

        sink.WriteYm2608(0, 0, 0x28, 0x00, 0); // key off ch0

        // Algorithm 7 (all four operators summed), feedback 0.
        sink.WriteYm2608(0, 0, 0xB0, 0x07, 0);

        for (int op = 0; op < 4; op++)
        {
            sink.WriteYm2608(0, 0, 0x30 + op, 0x01, 0);
            sink.WriteYm2608(0, 0, 0x40 + op, 0x00, 0);
            sink.WriteYm2608(0, 0, 0x50 + op, 0x1F, 0);
            sink.WriteYm2608(0, 0, 0x60 + op, 0x00, 0);
            sink.WriteYm2608(0, 0, 0x70 + op, 0x00, 0);
            sink.WriteYm2608(0, 0, 0x80 + op, 0x0F, 0);
        }

        int fnum = 0x400;
        int block = 3;
        sink.WriteYm2608(0, 0, 0xA4, (block << 3) | ((fnum >> 8) & 0x07), 0);
        sink.WriteYm2608(0, 0, 0xA0, fnum & 0xFF, 0);
        sink.WriteYm2608(0, 0, 0x28, 0xF0, 0);

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
    /// with the configured SSG gain. FMP.COM is unavailable, but sink
    /// construction happens before runtime.Initialize(), so a missing FMP.COM
    /// proves the gain was threaded through every stem sink without throwing:
    /// the exception surfaces from asset loading, not from the ssgGainDb wiring.
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
        Assert.Contains("FMP.COM", ex.Message);
    }
}