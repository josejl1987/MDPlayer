using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// WP02 invariants for <see cref="MusicalTimeMap"/> and <see cref="TempoSegment"/>:
/// absolute sample→quarter→tick conversion (incl. negative/fractional quarters),
/// exact-boundary and out-of-range extrapolation lookup, double-precision segment
/// continuity, and a ten-minute constant-tempo run with no cumulative drift.
/// </summary>
public sealed class MusicalTimeMapInvariantsTests
{
    private const int Sr = 48_000;
    private const double Bpm = 120.0;
    // 120 BPM => 0.5 s/quarter => 24000 samples/quarter at 48 kHz.
    private const double Spq = Sr * 60.0 / Bpm; // 24000

    private static TempoSegment Segment(long start, long end, double quarterAtStart, double spq = Spq)
        => new(start, end, quarterAtStart, spq, spq == Spq ? Bpm : Sr * 60.0 / spq, TimingSource.DriverBeatAnchors, 1.0);

    private static MusicalTimeMap SingleSegmentMap(double quarterAtStart, long end)
        => new(Sr, 0, new[] { Segment(0, end, quarterAtStart) });

    // ---- T006: absolute conversion incl. negative/fractional quarters ----

    [Fact]
    public void ConstantTempo_48k_120Bpm_SampleToTick_ExactGrid()
    {
        // One quarter = 24000 samples; PPQ 960 => 960 ticks/quarter.
        var map = SingleSegmentMap(quarterAtStart: 0, end: long.MaxValue);
        const int ppq = 960;

        Assert.Equal(0L, map.SampleToTick(0, ppq));
        Assert.Equal(960L, map.SampleToTick(24_000, ppq));
        Assert.Equal(1920L, map.SampleToTick(48_000, ppq));
        Assert.Equal(2880L, map.SampleToTick(72_000, ppq));
    }

    [Fact]
    public void NonZeroPhase_HalfBeatPickup_Preserved()
    {
        // Anchors: sample 12000 -> quarter 0, 36000 -> 1, 60000 -> 2 (SPQ 24000).
        // So sample 0 sits half a quarter before the first anchored beat => -0.5.
        var map = SingleSegmentMap(quarterAtStart: -0.5, end: long.MaxValue);

        Assert.Equal(-0.5, map.SampleToQuarterPosition(0), precision: 10);
        Assert.Equal(0.0, map.SampleToQuarterPosition(12_000), precision: 10);
        Assert.Equal(1.0, map.SampleToQuarterPosition(36_000), precision: 10);
        Assert.Equal(2.0, map.SampleToQuarterPosition(60_000), precision: 10);

        // Grid preserves the half-beat pickup in ticks (PPQ 960 => -480).
        Assert.Equal(-480L, map.SampleToTick(0, 960));
        Assert.Equal(0L, map.SampleToTick(12_000, 960));
    }

    [Fact]
    public void SampleToQuarterPosition_Exact_AcrossBroadRange()
    {
        // Absolute conversion must not accumulate error over a long span: every
        // quarter boundary lands exactly on its integer quarter position.
        var map = SingleSegmentMap(quarterAtStart: 0, end: long.MaxValue);

        for (int quarter = 0; quarter <= 1200; quarter++)
        {
            long sample = (long)Math.Round(quarter * Spq);
            Assert.Equal(quarter, map.SampleToQuarterPosition(sample), precision: 9);
        }
    }

    [Fact]
    public void NegativeAndFractionalQuarters_RoundTripThroughTicks()
    {
        var map = SingleSegmentMap(quarterAtStart: -2.25, end: long.MaxValue);
        const int ppq = 960;

        // sample 0 -> quarter -2.25 -> tick round(-2.25 * 960) = -2160.
        Assert.Equal(-2160L, map.SampleToTick(0, ppq));

        // A fractional quarter inside the segment stays fractional: sample 6000 is
        // 0.25 q after start, so absolute quarter = -2.25 + 0.25 = -2.0.
        Assert.Equal(-2.0, map.SampleToQuarterPosition(6000), precision: 10);

        // Inverse round-trip: tick -> quarter is exact.
        Assert.Equal(-2160.0 / 960, map.TickToQuarterPosition(-2160, ppq), precision: 12);
    }

    [Fact]
    public void ElapsedTickConversion_DoesNotDoubleRoundMusicalPhase()
    {
        const int sampleRate = 44_100;
        const double bpm = 188.0;
        const int ppq = 960;
        double samplesPerQuarter = sampleRate * 60.0 / bpm;
        var map = new MusicalTimeMap(
            sampleRate,
            0,
            new[]
            {
                new TempoSegment(
                    0,
                    1_000_000,
                    -0.31333333333333335,
                    samplesPerQuarter,
                    bpm,
                    TimingSource.SymbolicInference,
                    0.5),
            });

        // Rounding the absolute phased tick and subtracting the rounded phase
        // moves this timestamp from 32101 to 32102. MIDI export starts at tick
        // zero, so the elapsed conversion must retain the nearest tick.
        Assert.Equal(32101L, map.SampleToElapsedTick(470_636, ppq));
        Assert.Equal(32102L, map.SampleToTick(470_636, ppq) - map.SampleToTick(0, ppq));
    }

    // ---- T007: segment lookup at exact boundaries + out-of-range extrapolation ----

    private static MusicalTimeMap ThreeSegmentMap()
    {
        // [0, 24000) Q0, [24000, 48000) Q1, [48000, 72000) Q2 — contiguous, exact.
        return new MusicalTimeMap(Sr, 0, new[]
        {
            Segment(0, 24_000, 0.0),
            Segment(24_000, 48_000, 1.0),
            Segment(48_000, 72_000, 2.0),
        });
    }

    [Fact]
    public void Boundary_ExactlyAtStartSample_BelongsToNextSegment()
    {
        var map = ThreeSegmentMap();

        Assert.Equal(1.0, map.SampleToQuarterPosition(24_000), precision: 10);
        Assert.Equal(2.0, map.SampleToQuarterPosition(48_000), precision: 10);
    }

    [Fact]
    public void Boundary_AtEndSampleMinusOne_StaysInSegment()
    {
        var map = ThreeSegmentMap();

        // 23999 is the last sample of segment 0.
        Assert.Equal(23_999.0 / Spq, map.SampleToQuarterPosition(23_999), precision: 9);
    }

    [Fact]
    public void Boundary_AtEndSampleBelongsToNext_NoOffByOne()
    {
        var map = ThreeSegmentMap();

        // EndSample is exclusive: 24000 belongs to segment 1, not segment 0.
        double atEnd = map.SampleToQuarterPosition(24_000);
        double lastOfPrev = map.SampleToQuarterPosition(23_999);
        // Continuity: consecutive samples differ by exactly one SPQ step.
        Assert.Equal(atEnd - lastOfPrev, 1.0 / Spq, precision: 12);
        Assert.Equal(1.0, atEnd, precision: 10);
    }

    [Fact]
    public void BelowFirstSegmentStart_ExtrapolatesDeterministically()
    {
        // First segment starts at sample 12000 (quarter 0 there, quarter -0.5 at 0).
        var map = new MusicalTimeMap(Sr, 0, new[] { Segment(12_000, 36_000, 0.0) });

        // sample 5000 < first start => clamp to first QuarterPositionAtStart (quarter at 12000).
        Assert.Equal(0.0, map.SampleToQuarterPosition(5000), precision: 10);
        Assert.Equal(0.0, map.SampleToQuarterPosition(11_999), precision: 10);
    }

    [Fact]
    public void AboveLastSegmentEnd_ExtrapolatesDeterministically()
    {
        var map = ThreeSegmentMap();

        // sample >= EndSample (72000) => clamp to last QuarterPositionAtEnd = 3.0.
        Assert.Equal(3.0, map.SampleToQuarterPosition(80_000), precision: 10);
        Assert.Equal(3.0, map.SampleToQuarterPosition(72_000), precision: 10);

        // 71999 is the last sample inside segment 2 (EndSample exclusive): fractional.
        Assert.Equal(2.0 + 23_999.0 / Spq, map.SampleToQuarterPosition(71_999), precision: 9);
    }

    // ---- T008: double-precision segment continuity ----

    [Fact]
    public void AdjacentSegments_ContinuityWithinTolerance()
    {
        // Threaded origins: seg B's QuarterPositionAtStart must equal what seg A
        // produces at the shared boundary, within the documented 1e-6 tolerance.
        var segA = Segment(0, 48_000, 0.0, 24_000);        // A [0,48000) at 120 BPM
        var segB = Segment(48_000, 84_000, 2.0, 19_200);   // B [48000,84000) at 150 BPM, quarter 2 at start
        var map = new MusicalTimeMap(Sr, 0, new[] { segA, segB });

        // Genuine continuity: compute segment A's value AT the shared boundary
        // (48_000) from A's own slope (0 + 48000/24000 = 2.0) and require it to
        // equal segment B's origin. This is not tautological — it exercises A's
        // SamplesPerQuarter; a regression in A's slope would break it.
        double aAtBoundary = segA.QuarterPositionAt(48_000);
        Assert.Equal(2.0, aAtBoundary, precision: 9);
        Assert.Equal(segB.QuarterPositionAtStart, aAtBoundary, precision: 9);

        // Genuinely exercise the 150 BPM case: B's interior sample one full
        // quarter into B (48000 + 19200 = 67200) must map to quarter 3.0. This
        // only holds if B's slope is 48000*60/150 = 19200 samples/quarter, so an
        // inverted BPM helper (or wrong B slope) fails it.
        Assert.Equal(150.0, segB.BeatsPerMinute); // guards BPM-inversion directly
        var interior = map.SampleToQuarterPosition(67_200);
        Assert.Equal(3.0, interior, precision: 9);
    }

    [Fact]
    public void Continuity_RejectsRealDiscontinuity_AtConstruction()
    {
        // A genuinely disjoint origin (quarter 3 instead of threaded 2) must fail
        // the map's continuity validation rather than silently drift.
        var segments = new[]
        {
            Segment(0, 48_000, 0.0, 24_000),
            Segment(48_000, 84_000, 3.0, 19_200), // should be 2.0
        };

        Assert.Throws<ArgumentException>(() => new MusicalTimeMap(Sr, 0, segments));
    }

    // ---- T009: ten-minute constant-tempo no-drift ----

    [Fact]
    public void TenMinutes_ConstantTempo_NoCumulativeDrift()
    {
        // 120 BPM x 10 min = 1200 quarters at 48 kHz = 28,800,000 samples.
        const int ppq = 960;
        long totalSamples = (long)Math.Round(1200 * Spq);
        var map = SingleSegmentMap(quarterAtStart: 0, end: totalSamples);

        for (int quarter = 0; quarter <= 1200; quarter += 4)
        {
            long sample = (long)Math.Round(quarter * Spq);
            long expectedTick = (long)Math.Round(quarter * (double)ppq);
            long actualTick = map.SampleToTick(sample, ppq);
            // §71: every anchor accurate to ±1 tick.
            Assert.True(Math.Abs(actualTick - expectedTick) <= 1,
                $"quarter {quarter}: expected tick {expectedTick}, got {actualTick}");
            // Final beat is as accurate as the first — no error proportional to duration.
            if (quarter > 0)
                Assert.Equal(quarter * ppq, actualTick); // exact at every beat boundary
        }
    }
}
