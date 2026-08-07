using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Musical-time subsystem tests: constant-tempo fitting, phase, continuity,
/// tempo-change segmentation, jitter robustness, and the semantics that
/// "driver BPM known" never implies "beat grid known".
/// </summary>
public sealed class MusicalTimingTests
{
    private const int Sr = 44_100;

    private static BeatAnchor[] Anchors(double bpm, int count, long startSample = 0, int quarterStep = 4)
    {
        double spq = Sr * 60.0 / bpm;
        return Enumerable.Range(0, count)
            .Select(i => new BeatAnchor(startSample + (long)Math.Round(i * quarterStep * spq), i * quarterStep))
            .ToArray();
    }

    private static PiecewiseFit Fit(BeatAnchor[] anchors, double? fixedBpm = null, params TempoChangePoint[] changes)
        => BeatGridFitter.Fit(anchors, Sr, changes, fixedBpm);

    [Fact]
    public void Constant120Bpm_SampleZeroOnBeatZero()
    {
        // 120 BPM => 500 ms/quarter => 22050 samples/quarter.
        var anchors = Anchors(120, 9);
        PiecewiseFit fit = Fit(anchors);

        Assert.Single(fit.Segments);
        Assert.Equal(22050, fit.Segments[0].SamplesPerQuarter, precision: 3);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 3);
        Assert.Equal(0.0, fit.Diagnostics.SampleZeroQuarter!.Value, precision: 6);
        Assert.Equal(0.0, fit.Diagnostics.RmsResidualQuarters, precision: 6);
    }

    [Fact]
    public void NonZeroBeatPhase_PreservedAtSampleZero()
    {
        // Beats run every 4 quarters starting at quarter 2; sample zero is already
        // two quarters into a bar, so the fitted sample-zero quarter is +2.
        double spq = Sr * 60.0 / 120.0;
        var shifted = Enumerable.Range(0, 8)
            .Select(i => new BeatAnchor((long)Math.Round(i * 4 * spq), i * 4 + 2))
            .ToArray();
        PiecewiseFit fit = Fit(shifted);

        Assert.Equal(2.0, fit.Diagnostics.SampleZeroQuarter!.Value, precision: 6);
    }

    [Fact]
    public void OutlierAnchor_Rejected_GridStaysTrue()
    {
        // One anchor is a late callback (reported at the sample of the NEXT beat but
        // given the current beat number). Robust fitting rejects it as an outlier and
        // the surrounding grid remains correct.
        double spq = Sr * 60.0 / 120.0;
        var list = Anchors(120, 9).ToList();
        list[4] = new BeatAnchor((long)Math.Round(6 * spq), 5); // sample of beat 6, beat 5
        PiecewiseFit fit = Fit(list.ToArray());

        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(0.0, fit.Diagnostics.SampleZeroQuarter!.Value, precision: 3);
        Assert.True(fit.Diagnostics.MaxResidualQuarters < 0.25);
    }

    [Fact]
    public void DuplicateAnchor_Deduplicated()
    {
        double spq = Sr * 60.0 / 120.0;
        var list = Anchors(120, 6).ToList();
        list.Add(new BeatAnchor((long)Math.Round(1 * spq), 1)); // exact duplicate of index 1
        PiecewiseFit fit = Fit(list.ToArray());

        Assert.Equal(0, fit.Diagnostics.RejectedAnchorCount);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 3);
    }

    [Fact]
    public void JitteredAnchors_RobustFitSurvives()
    {
        var rand = new Random(42);
        double spq = Sr * 60.0 / 132.0;
        var jittered = Enumerable.Range(0, 40)
            .Select(i => new BeatAnchor(
                (long)Math.Round(i * spq) + rand.Next(-600, 600),
                i))
            .ToArray();
        PiecewiseFit fit = Fit(jittered);

        Assert.Equal(132.0, fit.Segments[0].BeatsPerMinute, precision: 1);
        Assert.True(fit.Diagnostics.RmsResidualQuarters < 0.05);
    }

    [Fact]
    public void SingleTempoChange_ProducesTwoSegments()
    {
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round(4 * spqA);
        var anchors = new List<BeatAnchor>();
        for (int i = 0; i < 4; i++)
            anchors.Add(new BeatAnchor((long)Math.Round(i * spqA), i));
        for (int i = 0; i < 4; i++)
            anchors.Add(new BeatAnchor(boundary + (long)Math.Round(i * spqB), 4 + i));

        PiecewiseFit fit = Fit(anchors.ToArray(), changes:
            new TempoChangePoint(boundary, 150, TimingSource.DriverValidatedTempo));

        Assert.Equal(2, fit.Segments.Length);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(150.0, fit.Segments[1].BeatsPerMinute, precision: 2);
    }

    [Fact]
    public void ContinuityHoldsAcrossTempoChange()
    {
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 180.0;
        long boundary = (long)Math.Round(4 * spqA);
        var anchors = new List<BeatAnchor>();
        for (int i = 0; i < 4; i++)
            anchors.Add(new BeatAnchor((long)Math.Round(i * spqA), i));
        for (int i = 0; i < 4; i++)
            anchors.Add(new BeatAnchor(boundary + (long)Math.Round(i * spqB), 4 + i));
        PiecewiseFit fit = Fit(anchors.ToArray(), changes:
            new TempoChangePoint(boundary, 180, TimingSource.DriverValidatedTempo));

        // Map construction enforces continuity: SampleToQuarterPosition is continuous.
        var segments = ConvertToTempoSegments(fit);
        var map = new MusicalTimeMap(Sr, 0, segments);
        double qAtBoundary = map.SampleToQuarterPosition(boundary);
        double qBefore = map.SampleToQuarterPosition(boundary - 1);
        double qAfter = map.SampleToQuarterPosition(boundary + 1);

        // Slope before boundary is the 120 BPM slope (~1/22050 quarters/sample).
        Assert.Equal(1.0 / spqA, qAtBoundary - qBefore, precision: 12);
        // Slope after boundary is the 180 BPM slope (~1/14700 quarters/sample).
        Assert.Equal(1.0 / spqB, qAfter - qAtBoundary, precision: 12);
        // No discontinuity at the seam.
        double extrapolatedBefore = map.SampleToQuarterPosition(boundary - 1) + 1.0 / spqA;
        Assert.Equal(extrapolatedBefore, qAtBoundary, precision: 12);
    }

    [Fact]
    public void NoCumulativeDrift_VeryLongPiece()
    {
        // 10 minutes at 120 BPM = 4800 quarters; musical tick stays exact.
        double bpm = 120;
        double spq = Sr * 60.0 / bpm;
        int quarters = 4800;
        var anchors = Enumerable.Range(0, 1200)
            .Where(i => i % 4 == 0)
            .Select(i => new BeatAnchor((long)Math.Round(i * spq), i))
            .ToArray();
        PiecewiseFit fit = Fit(anchors);
        var map = new MusicalTimeMap(Sr, 0, ConvertToTempoSegments(fit));

        const int ppq = 960;
        for (int q = 0; q <= quarters; q += 4)
        {
            long expectedTick = (long)Math.Round(q * (double)ppq);
            long actualTick = map.QuarterPositionToTick(q, ppq);
            Assert.Equal(expectedTick, actualTick);
        }
    }

    [Fact]
    public void FixedBpm_WithPhaseOffset()
    {
        // Fixed 120 BPM and a beat offset of one quarter => sample zero is at quarter -1.
        const double bpm = 120;
        double spq = Sr * 60.0 / bpm;
        long beatOffsetSamples = (long)Math.Round(spq);
        PiecewiseFit fit = BeatGridFitter.Fit(
            Array.Empty<BeatAnchor>(), Sr, fixedBpm: bpm,
            phaseQuarterAtSampleZero: beatOffsetSamples / spq);

        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 3);
        Assert.Equal(1.0, fit.Diagnostics.SampleZeroQuarter!.Value, precision: 6);
    }

    [Fact]
    public void FixedBpm_NoPhase_ReportsPhaseUnknown()
    {
        PiecewiseFit fit = BeatGridFitter.Fit(
            Array.Empty<BeatAnchor>(), Sr, fixedBpm: 120);

        Assert.True(fit.Diagnostics.PhaseUnknown);
        Assert.True(fit.Diagnostics.AnchorCount == 0);
    }

    private static TempoSegment[] ConvertToTempoSegments(PiecewiseFit fit)
    {
        var result = new TempoSegment[fit.Segments.Length];
        long start = 0;
        double quarterAtStart = fit.Segments[0].QuarterAtStart
            - fit.Segments[0].StartSample / fit.Segments[0].SamplesPerQuarter;
        for (int i = 0; i < fit.Segments.Length; i++)
        {
            SegmentFit s = fit.Segments[i];
            long end = i < fit.Segments.Length - 1
                ? fit.Segments[i + 1].StartSample
                : long.MaxValue;
            result[i] = new TempoSegment(start, end, quarterAtStart, s.SamplesPerQuarter,
                s.BeatsPerMinute, s.Source, s.Confidence);
            start = end;
            quarterAtStart = result[i].QuarterPositionAtEnd;
        }
        return result;
    }
}
