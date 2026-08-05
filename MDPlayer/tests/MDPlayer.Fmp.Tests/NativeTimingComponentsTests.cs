using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 native-path timing components: the exact rational master-clock→
/// output-frame mapper (verified against an independent UInt128 oracle), the
/// fixed output-latency delay line, and the pure-integer final mixer.
/// </summary>
public class NativeTimingComponentsTests
{
    private const ulong MasterHz = OpnaMasterClock.Hz;
    private const int SampleRate = 44100;

    private static long OracleOutputFrame(ulong masterClock) =>
        (long)((UInt128)masterClock * (ulong)SampleRate / MasterHz);

    private static ulong OracleFrameToMasterCeiling(long frame) =>
        (ulong)(((UInt128)(ulong)frame * MasterHz + (ulong)SampleRate - 1) / (ulong)SampleRate);

    [Fact]
    public void OpnaFrameMapper_MapOutputFrame_MatchesMathematicalOracle()
    {
        var mapper = new OpnaMasterClockFrameMapper(SampleRate);

        // A dense sweep must agree with floor(masterClock * sr / masterHz).
        var rng = new Random(7);
        for (int i = 0; i < 10_000; i++)
        {
            ulong c = (ulong)rng.NextInt64(0, 500_000_000);
            Assert.Equal(OracleOutputFrame(c), mapper.MapOutputFrame(c));
        }

        // Boundary values across a full second of real time.
        foreach (ulong c in new ulong[]
            { 0, 1, 2, 3, 100, 1000, SampleRate - 1, SampleRate, MasterHz - 1, MasterHz, MasterHz + 1 })
        {
            Assert.Equal(OracleOutputFrame(c), mapper.MapOutputFrame(c));
        }
    }

    [Fact]
    public void OpnaFrameMapper_MapFrameToMasterCeiling_MatchesOracle()
    {
        var mapper = new OpnaMasterClockFrameMapper(SampleRate);
        foreach (long f in new long[] { 0, 1, 44100, 100000, 1_000_000 })
            Assert.Equal(OracleFrameToMasterCeiling(f), mapper.MapFrameToMasterCeiling(f));
    }

    [Fact]
    public void OpnaFrameMapper_IsMonotonicAndDeterministic()
    {
        var a = new OpnaMasterClockFrameMapper(SampleRate);
        var b = new OpnaMasterClockFrameMapper(SampleRate);

        long previous = -1;
        for (ulong c = 0; c < 3_000_000; c += 97)
        {
            long sa = a.MapOutputFrame(c);
            long sb = b.MapOutputFrame(c);
            Assert.Equal(sa, sb); // deterministic: fresh mapper gives same result
            Assert.True(sa >= previous, $"output frame regressed at master clock {c}");
            previous = sa;
        }
    }

    [Fact]
    public void OpnaFrameMapper_RejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpnaMasterClockFrameMapper(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpnaMasterClockFrameMapper(-1));
        var mapper = new OpnaMasterClockFrameMapper(SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.MapFrameToMasterCeiling(-1));
    }

    [Fact]
    public void Ppz8OutputDelayBuffer_DelaysByExactlyLatencyFrames()
    {
        const int latency = 48;
        var delay = new Ppz8OutputDelayBuffer(latency);

        // Push frames 1..N; the first `latency` outputs must be silence and
        // the pushed frames must exit in order exactly `latency` frames later.
        for (int i = 1; i <= latency; i++)
        {
            delay.Push((short)i, (short)(-i), out short l, out short r);
            Assert.Equal(0, l);
            Assert.Equal(0, r);
        }

        for (int i = 1; i <= latency; i++)
        {
            delay.Push((short)(i + latency), (short)(-(i + latency)), out short l, out short r);
            Assert.Equal((short)i, l);
            Assert.Equal((short)(-i), r);
        }
    }

    [Fact]
    public void Ppz8OutputDelayBuffer_Reset_RestoresSilenceFill()
    {
        const int latency = 8;
        var delay = new Ppz8OutputDelayBuffer(latency);
        for (int i = 0; i < latency; i++)
            delay.Push((short)100, (short)100, out _, out _);

        delay.Reset();

        for (int i = 0; i < latency; i++)
        {
            delay.Push((short)i, (short)i, out short l, out short r);
            Assert.Equal(0, l);
            Assert.Equal(0, r);
        }
    }

    [Fact]
    public void Ppz8OutputDelayBuffer_RejectsNegativeLatency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ppz8OutputDelayBuffer(-1));
    }

    [Fact]
    public void OpnaPpz8IntegerMixer_ExactArithmetic_UnityGain()
    {
        var mixer = new OpnaPpz8IntegerMixer();

        // opna + ppz8, pure sum with saturation; PPZ8 gain 65536 => scale 1.
        mixer.Mix(1000, -1000, 2000, 3000, out short l, out short r);
        Assert.Equal(3000, l);
        Assert.Equal(2000, r);

        // Rounding: (ppz8 * 65536 + 0x8000) >> 16 == ppz8 for ppz8 >= 0 and
        // ppz8 - 1 + 1 for negatives — pinned here.
        mixer.Mix(0, 0, 32767, -32768, out l, out r);
        Assert.Equal(32767, l);
        Assert.Equal(-32768, r);
    }

    [Fact]
    public void OpnaPpz8IntegerMixer_ExactArithmetic_CustomGain()
    {
        // Gain 32768 (half): value * 32768 + 0x8000 >> 16 = round-half-up(value/2).
        var mixer = new OpnaPpz8IntegerMixer(32768);

        mixer.Mix(0, 0, 1000, -1000, out short l, out short r);
        Assert.Equal(500, l);
        Assert.Equal(-500, r);

        // Odd negative: (-999 * 32768 + 0x8000) >> 16 = -499 (the exact
        // arithmetic-shift result; round-half-up bias toward +inf for negatives).
        mixer.Mix(0, 0, -999, 999, out l, out r);
        Assert.Equal(-499, l);
        Assert.Equal(500, r); // +999 rounds half up to 500

        // -1000 * 32768 + 0x8000 >> 16 = -499.5 -> -500 (arithmetic shift
        // truncates toward -inf for the exact .5 case).
        mixer.Mix(0, 0, -1000, 0, out l, out _);
        Assert.Equal(-500, l);
    }

    [Fact]
    public void OpnaPpz8IntegerMixer_Saturates_AtInt16Bounds()
    {
        var mixer = new OpnaPpz8IntegerMixer();

        mixer.Mix(30000, -30000, 30000, -30000, out short l, out short r);
        Assert.Equal(32767, l);
        Assert.Equal(-32768, r);
    }

    [Fact]
    public void OpnaPpz8IntegerMixer_RejectsNegativeGain()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpnaPpz8IntegerMixer(-1));
    }
}
