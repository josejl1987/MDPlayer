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

    [Fact]
    public void RateFromValidatedBpm_DoesNotDestroyAnchorPhase()
    {
        // Anchors establish sample 12000 = quarter 0. Adding a validated BPM supplies
        // only the rate; the fitted phase must be preserved (§10 combination rule).
        // Specifically the map must NOT be rebuilt "from BPM starting at quarter zero"
        // (which would put quarter 0 at sample zero instead of sample 12000).
        double spq = Sr * 60.0 / 120.0;
        var anchors = Enumerable.Range(0, 6)
            .Select(i => new BeatAnchor((long)Math.Round(12000 + i * spq), i))
            .ToArray();
        PiecewiseFit fit = BeatGridFitter.Fit(
            anchors, Sr,
            tempoChanges: new[] { new TempoChangePoint(12000, 120, TimingSource.DriverValidatedTempo) });

        // Phase preserved: sample 12000 stays at quarter 0 (a single validated segment).
        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 4);
        // Quarter at sample 12000 must be 0 — proving the phase was not shifted to
        // sample zero via the "validated BPM ⇒ quarter 0 at sample 0" rebuild.
        Assert.Equal(0.0,
            fit.Segments[0].QuarterAtStart + (12000 - fit.Segments[0].StartSample) / fit.Segments[0].SamplesPerQuarter,
            precision: 9);
    }

    [Fact]
    public void ConflictingAnchors_Reported_NotAveraged()
    {
        // §56: a sample claimed as two different beats. Never average; report.
        double spq = Sr * 60.0 / 120.0;
        long s1 = (long)Math.Round(spq);                       // "beat 1" sample
        var anchors = new List<BeatAnchor>
        {
            new(0, 0),
            new(s1, 1),
            new(s1, 1),     // exact duplicate: harmless, dedup
            new(s1, 2),     // conflicting: same sample, beat 2
            new((long)Math.Round(2 * spq), 2),
            new((long)Math.Round(3 * spq), 3),
        };
        PiecewiseFit fit = Fit(anchors.ToArray());

        Assert.True(fit.Diagnostics.HasConflictingAnchors);
        // The conflict is surfaced in the warning text, never silently averaged.
        Assert.Contains(fit.Diagnostics.Warnings, w => w.Contains("conflicting anchors"));
        Assert.Equal(0.0, fit.Diagnostics.SampleZeroQuarter!.Value, precision: 6);
    }

    [Fact]
    public void LoneConflictingValidatedObservation_DoesNotSplitAnchorRate()
    {
        // §10 anchors-first precedence: anchors prove a constant 120 BPM. A single
        // mid-source DriverTimingEvent reporting 150 BPM is an observation, not
        // evidence of a sustained transition (no second corroborating value), so it
        // must NOT split the grid or override the anchor-derived rate.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatAnchor((long)Math.Round(i * spq), i))
            .ToArray();
        long loneSample = (long)Math.Round(20 * spq);
        PiecewiseFit fit = Fit(beats, changes:
            new TempoChangePoint(loneSample, 150, TimingSource.DriverValidatedTempo));

        // One constant grid kept at the anchor-established 120 BPM — no pre/post split.
        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(0, fit.Segments[0].StartSample);
    }

    [Fact]
    public void LoneConflictingValidatedObservation_AtSampleZero_KeepsAnchorRate()
    {
        // §10 anchors-first precedence, single-segment case: anchors prove a constant
        // 120 BPM and ONE validated observation reports 150 BPM exactly at Sample=0.
        // Because the change sits at the domain start no "before" segment is emitted,
        // so the fit yields a single segment carrying the observation's BPM across the
        // whole domain. The lone conflicting reading must still not override the
        // authoritative anchor-derived rate: the map stays at 120 BPM, never 150.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatAnchor((long)Math.Round(i * spq), i))
            .ToArray();
        PiecewiseFit fit = Fit(beats, changes:
            new TempoChangePoint(0, 150, TimingSource.DriverValidatedTempo));

        // One constant grid kept at the anchor-established 120 BPM — the sample-0
        // observation does not split it and does not supply the rate.
        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(0L, fit.Segments[0].StartSample);
        Assert.Equal(long.MaxValue, fit.Segments[0].EndSample);
    }

    [Fact]
    public void LoneConflictingValidatedObservation_AtFirstBeatBoundary_KeepsAnchorRate()
    {
        // §10 anchors-first precedence, P1 finding: anchors prove a constant 120 BPM
        // and ONE validated observation reports 150 BPM exactly at sample 22050 — the
        // sample of the FIRST BEAT (the second anchor). The head region contains only
        // the single anchor at sample 0, so it can neither establish nor corroborate a
        // rate; the decision must come from ALL anchor evidence and the distinct-value
        // count, never from a segment whose head has too few anchors. The lone 150
        // value is an observation, not a transition, and must not override the
        // anchor-derived 120 BPM for the whole domain.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatAnchor((long)Math.Round(i * spq), i))
            .ToArray();
        long firstBeat = (long)Math.Round(spq); // 22050 samples/quarter at 120 BPM
        PiecewiseFit fit = Fit(beats, changes:
            new TempoChangePoint(firstBeat, 150, TimingSource.DriverValidatedTempo));

        // One constant grid kept at the anchor-established 120 BPM — the observation
        // at a first-beat boundary does not split it and does not supply the rate.
        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(0L, fit.Segments[0].StartSample);
        Assert.Equal(long.MaxValue, fit.Segments[0].EndSample);
    }

    [Fact]
    public void ValidatedTempoChange_Sustained_ProducesContinuousSegments_NotJitterSpam()
    {
        // §59: 120 BPM for 16 quarters, then 150 BPM. Exactly the required transition;
        // jitter around each tempo must NOT spawn extra segments.
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round(16 * spqA);
        var rand = new Random(7);

        var beats = new List<BeatAnchor>();
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatAnchor((long)Math.Round(i * spqA) + rand.Next(-2, 3), i));
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatAnchor(boundary + (long)Math.Round(i * spqB) + rand.Next(-2, 3), 16 + i));

        PiecewiseFit fit = Fit(beats.ToArray(), changes:
            new TempoChangePoint(boundary, 150, TimingSource.DriverValidatedTempo));

        // Exactly two segments (one per tempo), no per-beat fragmentation.
        Assert.Equal(2, fit.Segments.Length);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(150.0, fit.Segments[1].BeatsPerMinute, precision: 2);
        Assert.Equal(boundary, fit.Segments[1].StartSample);
    }

    [Fact]
    public void JitterAroundOneTempo_Suppressed_NoPerBeatSegments()
    {
        // §17/§18: many tiny validated BPM perturbations around one tempo must yield
        // ONE segment, not a segment per change.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatAnchor((long)Math.Round(i * spq), i))
            .ToArray();
        var jitterChanges = Enumerable.Range(0, 40)
            .Select(i => new TempoChangePoint((long)Math.Round(i * spq), 120, TimingSource.DriverValidatedTempo))
            .ToArray();
        PiecewiseFit fit = Fit(beats.ToArray(), changes: jitterChanges);

        // All identical µs/qn values collapse to one segment.
        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 3);
    }

    [Fact]
    public void NearIdenticalBpm_JitterCollapsesToOneSegment()
    {
        // §17: 120.0, 120.1, 120.05 are sampling/timer jitter around one tempo. Even
        // though the rounded µs/qn differ (120.0→500000, 120.1→499583, 120.05→499792),
        // the sustained-change filter must collapse them into ONE segment, not a
        // per-observation fragmentation.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 12)
            .Select(i => new BeatAnchor((long)Math.Round(i * spq), i))
            .ToArray();
        PiecewiseFit fit = BeatGridFitter.Fit(beats, Sr, tempoChanges: new[]
        {
            new TempoChangePoint(0, 120.0, TimingSource.DriverValidatedTempo),
            new TempoChangePoint((long)Math.Round(4 * spq), 120.1, TimingSource.DriverValidatedTempo),
            new TempoChangePoint((long)Math.Round(8 * spq), 120.05, TimingSource.DriverValidatedTempo),
        });

        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 3);
    }

    [Fact]
    public void ValidatedPath_AssignsResiduals_RmsReflectsOutlier()
    {
        // Findings 2/4: on the validated-tempo path residuals were computed but never
        // assigned, so RMS stayed 0 and IsTrustworthy stayed true regardless of a
        // severe outlier. A bad anchor must now push the reported residuals (and thus
        // IsTrustworthy) beyond the trust threshold.
        double spq = Sr * 60.0 / 120.0;
        var list = Anchors(120, 9).ToList();
        list[4] = new BeatAnchor((long)Math.Round(8 * spq), 4); // sample of beat 8, beat 4
        PiecewiseFit fit = Fit(list.ToArray(), changes:
            new TempoChangePoint(0, 120, TimingSource.DriverValidatedTempo));

        Assert.Single(fit.Segments);
        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 3);
        // The outlier was recorded as rejected on the validated path.
        Assert.Contains(fit.Diagnostics.RejectedAnchors, r => r.Sample == (long)Math.Round(8 * spq));
        // Residuals are now surfaced: RMS exceeds the 0.25 trust threshold and the
        // maximum residual reflects the outlier's ~4-quarter error.
        Assert.True(fit.Diagnostics.RmsResidualQuarters > 0.25);
        Assert.True(fit.Diagnostics.MaxResidualQuarters >= 3.0);
        Assert.False(fit.Diagnostics.IsTrustworthy);
    }

    [Fact]
    public void OutlierAnchor_RecordedInDiagnostics_WithReason()
    {
        double spq = Sr * 60.0 / 120.0;
        var list = Anchors(120, 9).ToList();
        list[4] = new BeatAnchor((long)Math.Round(6 * spq), 5); // late callback → outlier
        PiecewiseFit fit = Fit(list.ToArray());

        Assert.Equal(120.0, fit.Segments[0].BeatsPerMinute, precision: 2);
        Assert.True(fit.Diagnostics.RejectedAnchorCount >= 1);
        Assert.Contains(fit.Diagnostics.RejectedAnchors,
            r => r.Sample == list[4].Sample && !string.IsNullOrEmpty(r.Reason));
    }

    // ---- Producer boundary: BeatIndex quarter-note convention (S01/T1) ----
    // Locked convention (FR-009): BeatIndex increment-1 == one MIDI quarter note,
    // scaled by MusicalTimeMapOptions.QuartersPerBeat (default 1.0) exactly once in
    // MusicalTimeMapBuilder.BuildAnchors (quarter = BeatIndex * QuartersPerBeat).
    // These tests drive the same BeatEvent -> BuildAnchors -> BeatGridFitter ->
    // MusicalTimeMap path used by timing analysis; no MIDI-side conversion exists
    // and none is allowed.

    private static VisualizationTimeline BeatTimeline(double samplesPerBeatStep, int count)
        => new()
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr * 100,
            Beats = Enumerable.Range(0, count)
                .Select(i => new BeatEvent((long)Math.Round(i * samplesPerBeatStep), i))
                .ToArray(),
        };

    [Fact]
    public void ProducerBoundary_QpbOne_BeatIndexIncrementIsOneQuarterNote()
    {
        // BeatEvent i at sample i*22050 with BeatIndex i must land on quarter i at
        // the default QuartersPerBeat = 1.0 (increment-1 == one quarter note).
        double spq = Sr * 60.0 / 120.0; // 22050 samples/quarter at 44.1 kHz
        var timeline = BeatTimeline(spq, count: 9);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions()).Map;

        for (int i = 0; i < 9; i++)
            Assert.Equal(i, map.SampleToQuarterPosition((long)Math.Round(i * spq)), precision: 6);
    }

    [Fact]
    public void ProducerBoundary_QpbN_BeatIndexScalesToQuarter()
    {
        // QuartersPerBeat = 2.0 (one BeatIndex step == two quarter notes): beat i at
        // sample i*44100 (22050*2) maps to quarter i*2 — the FR-009 scale factor
        // is applied at the producer boundary, not during MIDI serialization.
        double spq = Sr * 60.0 / 120.0;
        const double qpb = 2.0;
        var timeline = BeatTimeline(spq * qpb, count: 5);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions { QuartersPerBeat = qpb }).Map;

        for (int i = 0; i < 5; i++)
            Assert.Equal(i * qpb, map.SampleToQuarterPosition((long)Math.Round(i * spq * qpb)), precision: 6);
    }

    [Fact]
    public void ProducerBoundary_NonIntegerBeatIndex_AllowedAndPreserved()
    {
        // Fractional BeatIndex (off-beat / half-step drivers) is legal: quarter
        // = BeatIndex * QuartersPerBeat, so 0.5, 1.5, 2.5 pass through unchanged.
        double spq = Sr * 60.0 / 120.0;
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr * 100,
            Beats = new[]
            {
                new BeatEvent(0, 0.5),
                new BeatEvent((long)Math.Round(spq), 1.5),
                new BeatEvent((long)Math.Round(2 * spq), 2.5),
            },
        };

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions()).Map;

        Assert.Equal(0.5, map.SampleToQuarterPosition(0), precision: 6);
        Assert.Equal(1.5, map.SampleToQuarterPosition((long)Math.Round(spq)), precision: 6);
        Assert.Equal(2.5, map.SampleToQuarterPosition((long)Math.Round(2 * spq)), precision: 6);
    }

    [Fact]
    public void ProducerBoundary_NonFiniteBeatIndex_FilteredAtBoundary()
    {
        // NaN/Infinity is not a legal producer value: BuildAnchors filters non-finite
        // BeatIndex at the boundary instead of crashing the fit or polluting the grid.
        double spq = Sr * 60.0 / 120.0;
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr * 100,
            Beats = new[]
            {
                new BeatEvent(0, 0.0),
                new BeatEvent((long)Math.Round(spq), double.NaN),
                new BeatEvent((long)Math.Round(2 * spq), 2.0),
            },
        };

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions()).Map;

        // The NaN anchor is dropped; the surviving pair still yields a valid grid.
        Assert.Equal(0.0, map.SampleToQuarterPosition(0), precision: 6);
        Assert.Equal(2.0, map.SampleToQuarterPosition((long)Math.Round(2 * spq)), precision: 6);
    }

    [Fact]
    public void ProducerBoundary_MergeReplay_SerializedBeatsPreserveConvention()
    {
        // Full runtime producer path: TimelineBuilder.AddBeat -> Build -> serialized
        // timeline JSON ({"sample": N, "beat": I}) -> Merge (NormalizeClock) ->
        // AddBeat. The clock may be converted, but BeatIndex passes through untouched
        // and the replayed timeline produces the same quarter grid.
        double spq = Sr * 60.0 / 120.0;
        var source = new TimelineBuilder(Sr);
        for (int i = 0; i < 6; i++)
            source.AddBeat(new BeatEvent((long)Math.Round(i * spq), i));
        VisualizationTimeline produced = source.Build(Sr * 100);

        // Serialized shape uses the JSON contract names "sample" and "beat".
        string json = VisualizationJsonWriter.Serialize(produced);
        Assert.Contains("\"beat\"", json);
        Assert.Contains("\"sample\"", json);

        var merged = new TimelineBuilder(Sr);
        merged.Merge(produced);
        VisualizationTimeline replayed = merged.Build(Sr * 100);

        Assert.Equal(produced.Beats.Length, replayed.Beats.Length);
        for (int i = 0; i < produced.Beats.Length; i++)
        {
            Assert.Equal(produced.Beats[i].SamplePosition, replayed.Beats[i].SamplePosition);
            Assert.Equal(produced.Beats[i].BeatIndex, replayed.Beats[i].BeatIndex);
        }

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(
            replayed, new MusicalTimeMapOptions()).Map;
        for (int i = 0; i < 6; i++)
            Assert.Equal(i, map.SampleToQuarterPosition((long)Math.Round(i * spq)), precision: 6);
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
