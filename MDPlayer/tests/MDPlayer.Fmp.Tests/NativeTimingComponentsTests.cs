using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 native-path timing components: the exact rational PPZ8 command
/// mapper (verified against the clock mapper's own closed form), the fixed
/// output-latency delay line, and the pure-integer final mixer.
/// </summary>
public class NativeTimingComponentsTests
{
    private const uint CpuHz = 8_000_000;
    private const int SampleRate = 44100;

    [Fact]
    public void Ppz8CommandMapper_MatchesClockMapper_ExactOracle()
    {
        var mapper = new NisePpz8CommandMapper(CpuHz, SampleRate);
        var clock = new NiseOpnaClockMapper(CpuHz);

        // Sweep across a full second of CPU time plus boundary values.
        ulong[] cycles = { 0, 1, 2, 3, 7, 8, 15, 16, 31, 100, 1000, 7997, 12345, 44100, 8_000_000 - 1, 8_000_000, 8_000_001, 1_000_000_000 };
        foreach (ulong c in cycles)
        {
            Assert.Equal(clock.Map(c), mapper.MapMasterClock(c));
        }

        // A dense sweep must also agree with the closed form floor(c * master / cpu).
        var rng = new Random(42);
        for (int i = 0; i < 10_000; i++)
        {
            ulong c = (ulong)rng.NextInt64(0, 200_000_000);
            ulong expected = (ulong)((UInt128)c * NiseOpnaClockMapper.Ym2608MasterClockHz / CpuHz);
            Assert.Equal(expected, mapper.MapMasterClock(c));
        }
    }

    [Fact]
    public void Ppz8CommandMapper_MapSample_MatchesMathematicalOracle()
    {
        var mapper = new NisePpz8CommandMapper(CpuHz, SampleRate);

        // sample = floor(floor(c * master / cpu) * rate / master), UInt128 exact.
        var rng = new Random(7);
        for (int i = 0; i < 10_000; i++)
        {
            ulong c = (ulong)rng.NextInt64(0, 500_000_000);
            ulong masterClock = (ulong)((UInt128)c * NiseOpnaClockMapper.Ym2608MasterClockHz / CpuHz);
            long expected = (long)((UInt128)masterClock * (ulong)SampleRate / NiseOpnaClockMapper.Ym2608MasterClockHz);
            Assert.Equal(expected, mapper.MapSample(c));
        }
    }

    [Fact]
    public void Ppz8CommandMapper_IsMonotonicAndDeterministic()
    {
        var a = new NisePpz8CommandMapper(CpuHz, SampleRate);
        var b = new NisePpz8CommandMapper(CpuHz, SampleRate);

        long previous = -1;
        for (ulong c = 0; c < 3_000_000; c += 97)
        {
            long sa = a.MapSample(c);
            long sb = b.MapSample(c);
            Assert.Equal(sa, sb); // deterministic: fresh mapper gives same result
            Assert.True(sa >= previous, $"sample regressed at cycle {c}");
            previous = sa;
        }
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

    [Fact]
    public void NisePpz8CommandMapper_RejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NisePpz8CommandMapper(0, 44100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NisePpz8CommandMapper(CpuHz, 0));
    }
}