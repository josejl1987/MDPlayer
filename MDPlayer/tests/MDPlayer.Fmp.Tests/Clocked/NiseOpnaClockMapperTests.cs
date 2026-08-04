using Fmp.Core.Nise98;
using Xunit;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Tests for <see cref="NiseOpnaClockMapper"/>: exact rational CPU→OPNA
/// clock conversion that is monotonic, deterministic, chunk-independent,
/// drift-free, and regresses loudly.
/// </summary>
public sealed class NiseOpnaClockMapperTests
{
    private const ulong MasterHz = NiseOpnaClockMapper.Ym2608MasterClockHz;

    /// <summary>floor(totalCpuCycles * masterHz / cpuHz) via UInt128.</summary>
    private static ulong Expected(ulong totalCpuCycles, uint cpuHz, ulong masterHz = MasterHz)
    {
        return (ulong)((UInt128)totalCpuCycles * masterHz / cpuHz);
    }

    [Fact]
    public void Map_MatchesClosedFormFloor()
    {
        const uint cpuHz = 8_000_000;
        var mapper = new NiseOpnaClockMapper(cpuHz);

        foreach (ulong cycles in new ulong[] { 0, 1, 2, 3, 8_000_000, 8_000_001, 123_456_789, 4_000_000_000 })
        {
            mapper.Reset();
            Assert.Equal(Expected(cycles, cpuHz), mapper.Map(cycles));
        }
    }

    [Fact]
    public void Map_IsMonotonicNonDecreasing()
    {
        const uint cpuHz = 10_000_000;
        var mapper = new NiseOpnaClockMapper(cpuHz);

        ulong previous = 0;
        for (ulong cycles = 0; cycles <= 2_000_000; cycles += 37)
        {
            ulong current = mapper.Map(cycles);
            Assert.True(current >= previous, $"opna clock regressed at cpu cycle {cycles}");
            previous = current;
        }
    }

    /// <summary>
    /// The result depends only on the absolute input, not on how the CPU
    /// cycles were fed in (per-call chunks are irrelevant).
    /// </summary>
    [Fact]
    public void Map_IsChunkIndependent()
    {
        const uint cpuHz = 8_000_000;
        ulong[] chunkSizes = { 1, 2, 7, 100, 1_000, 9_999, 123_457 };
        ulong target = 10_000_000;

        var oneShot = new NiseOpnaClockMapper(cpuHz);
        ulong expected = oneShot.Map(target);

        var chunked = new NiseOpnaClockMapper(cpuHz);
        ulong fed = 0;
        foreach (ulong chunk in chunkSizes)
        {
            fed += chunk;
            if (fed > target) break;
            chunked.Map(fed);
        }
        if (fed < target)
            chunked.Map(target);

        Assert.Equal(expected, chunked.Map(target));
    }

    /// <summary>
    /// Repeated incremental feeding never drifts from the closed form: the
    /// fractional remainder carries exactly.
    /// </summary>
    [Fact]
    public void Map_IsDriftFreeOverLongRuns()
    {
        const uint cpuHz = 8_000_000;
        var mapper = new NiseOpnaClockMapper(cpuHz);

        ulong total = 0;
        for (ulong i = 0; i < 100_000; i++)
        {
            total += 71;
            ulong actual = mapper.Map(total);
            Assert.Equal(Expected(total, cpuHz), actual);
        }
    }

    [Fact]
    public void Map_SameInput_SameOutput_NoStateChange()
    {
        const uint cpuHz = 8_000_000;
        var mapper = new NiseOpnaClockMapper(cpuHz);

        mapper.Map(5_000_000);
        ulong first = mapper.Map(9_000_000);
        ulong second = mapper.Map(9_000_000);
        Assert.Equal(first, second);
        // Repeating the input must not advance the clock further.
        Assert.Equal(first, mapper.Map(9_000_000));
    }

    [Fact]
    public void Map_Regression_ThrowsArgumentOutOfRange()
    {
        const uint cpuHz = 8_000_000;
        var mapper = new NiseOpnaClockMapper(cpuHz);

        mapper.Map(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map(99));
    }

    [Fact]
    public void Ctor_RejectsZeroFrequencies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NiseOpnaClockMapper(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NiseOpnaClockMapper(8_000_000, 0));
    }

    [Fact]
    public void Reset_ReturnsToOrigin()
    {
        const uint cpuHz = 8_000_000;
        var mapper = new NiseOpnaClockMapper(cpuHz);

        mapper.Map(10_000_000);
        mapper.Reset();
        Assert.Equal(0ul, mapper.Map(0));
        Assert.Equal(Expected(123, cpuHz), mapper.Map(123));
    }

    [Theory]
    [InlineData(8_000_000u)]
    [InlineData(10_000_000u)]
    [InlineData(7_987_200u)]
    public void Map_UsesYm2608MasterClockByDefault(uint cpuHz)
    {
        var mapper = new NiseOpnaClockMapper(cpuHz);
        Assert.Equal(Expected(1_000_000, cpuHz), mapper.Map(1_000_000));
    }
}
