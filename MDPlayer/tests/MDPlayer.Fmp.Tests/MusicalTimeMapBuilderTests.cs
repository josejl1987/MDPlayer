#nullable enable

using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Builder-level regressions (MusicalTimeMapBuilder.Build → MusicalTimeMap):
/// BeatIndex→quarter normalization, nonzero-starting BeatIndex, source precedence,
/// phase-preserving validated BPM, strict-mode failures, tempo-segment continuity,
/// jitter suppression, and µs/quarter 24-bit representation validation.
/// </summary>
public sealed class MusicalTimeMapBuilderTests
{
    private const int Sr = 44_100;

    private static NoteEvent NewNote(string voice, long start, long end, int midi)
        => new(
            ChannelId: voice,
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 440,
            InitialMidiNote: midi,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(
        BeatEvent[] beats,
        DriverTimingEvent[]? timing = null,
        long endSample = 44_100 * 100,
        NoteEvent[]? notes = null,
        long startSample = 0)
        => new()
        {
            SampleRate = Sr,
            StartSample = startSample,
            EndSample = endSample,
            Beats = beats,
            Timing = timing ?? Array.Empty<DriverTimingEvent>(),
            Notes = notes ?? Array.Empty<NoteEvent>(),
        };

    // ---- T011: BeatIndex → quarter normalization once in the builder ----

    [Fact]
    public void QuartersPerBeat_Scale_ProducesCorrectQuarterGrid()
    {
        // 4 driver beats per quarter (QuartersPerBeat=0.25): beat index i maps to
        // quarter i*0.25. 120 BPM ⇒ 22050 samples/quarter ⇒ 5512.5 samples per beat.
        double spq = Sr * 60.0 / 120.0;
        var timeline = Timeline(
            Enumerable.Range(0, 40)
                .Select(i => new BeatEvent((long)Math.Round(i * spq * 0.25), i))
                .ToArray());

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { QuartersPerBeat = 0.25, DetectTempoChanges = true }).Map;

        // Beat 0 → quarter 0; beat 4 → quarter 1; beat 8 → quarter 2.
        // (Tiny residuals come from sample rounding in the beat positions; the grid
        // itself is the correct 0.25-quart-per-beat scale.)
        Assert.Equal(0.0, map.SampleToQuarterPosition(0), precision: 3);
        Assert.Equal(1.0, map.SampleToQuarterPosition((long)Math.Round(4 * spq * 0.25)), precision: 3);
        Assert.Equal(2.0, map.SampleToQuarterPosition((long)Math.Round(8 * spq * 0.25)), precision: 3);
    }

    [Fact]
    public void NonZeroStartingBeatIndex_PreservesRelativeTimeline()
    {
        // §53: sample 0 → beat 128, sample 24000 → beat 129, sample 48000 → beat 130.
        // The grid must match beat*QuartersPerBeat; the first BeatIndex is NOT
        // treated as zero. Intervals and phase are preserved (a later origin shift
        // may translate ticks but not change relationships).
        var timeline = Timeline(new[]
        {
            new BeatEvent(0, 128),
            new BeatEvent(24_000, 129),
            new BeatEvent(48_000, 130),
        }, endSample: 96_000);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        Assert.Equal(128.0, map.SampleToQuarterPosition(0), precision: 9);
        Assert.Equal(129.0, map.SampleToQuarterPosition(24_000), precision: 9);
        Assert.Equal(130.0, map.SampleToQuarterPosition(48_000), precision: 9);
        // The interval between two beats stays one quarter regardless of the
        // nonzero origin (identical to "beat 0 at sample 0" spacing).
        Assert.Equal(1.0, map.SampleToQuarterPosition(24_000) - map.SampleToQuarterPosition(0), precision: 9);
        Assert.Equal(1.0, map.SampleToQuarterPosition(48_000) - map.SampleToQuarterPosition(24_000), precision: 9);
    }

    // ---- T012: duplicate/conflict anchors via the builder ----

    [Fact]
    public void DuplicateAnchor_Harmless_NoDistortion()
    {
        double spq = Sr * 60.0 / 120.0;
        long s1 = (long)Math.Round(spq);
        var timeline = Timeline(new[]
        {
            new BeatEvent(0, 0),
            new BeatEvent(s1, 1),
            new BeatEvent(s1, 1),  // exact duplicate
            new BeatEvent((long)Math.Round(2 * spq), 2),
            new BeatEvent((long)Math.Round(3 * spq), 3),
        });

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true });

        Assert.Equal(0, build.Diagnostics.RejectedAnchorCount);
        Assert.Equal(1.0, build.Map.SampleToQuarterPosition(s1), precision: 9);
    }

    [Fact]
    public void ConflictingAnchor_StrictMode_Throws_AndNeverAverages()
    {
        double spq = Sr * 60.0 / 120.0;
        long s1 = (long)Math.Round(spq);
        var timeline = Timeline(new[]
        {
            new BeatEvent(0, 0),
            new BeatEvent(s1, 1),
            new BeatEvent(s1, 2),  // conflicting
        });

        // Non-strict: conflict reported, export proceeds with the conflict flagged.
        MusicalTimeMapBuildResult nonStrict = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true });
        Assert.True(nonStrict.Diagnostics.HasConflictingAnchors);

        // Strict: fundamental ambiguity must fail, never silently averaged.
        var ex = Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(timeline,
                new MusicalTimeMapOptions { DetectTempoChanges = true, StrictTiming = true }));
        Assert.Contains("conflict", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- T014: rate from validated BPM without destroying phase ----

    [Fact]
    public void ValidatedBpm_KeepsAnchorFittedPhase()
{
        // Anchors establish sample 12000 = quarter 0 (phase). A validated BPM of 120
        // supplies only the rate; the map must NOT put quarter 0 at sample zero.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 8)
            .Select(i => new BeatEvent((long)Math.Round(12_000 + i * spq), i))
            .ToArray();
        var timeline = Timeline(beats,
            timing: new[] { new DriverTimingEvent(0, 0, 120.0) });

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        // Quarter 0 stays at sample 12000; sample zero is before it (negative).
        Assert.Equal(0.0, map.SampleToQuarterPosition(12_000), precision: 9);
        Assert.Equal(-12000.0 / spq, map.SampleToQuarterPosition(0), precision: 6);
    }

    // ---- D005: tempo source decoupled from beat-phase override ----

    [Fact]
    public void PhaseOverride_KeepsDriverBeatAnchorsTempo_ReportsPhaseSourceSeparately()
    {
        // D005: an explicit beat-phase override must select the PHASE dimension ONLY.
        // It must never convert authoritative driver beat-anchor tempo into
        // TimingSource.UserOverride. The driver encodes 120 BPM purely via its beat
        // anchors (no validated tempo events); an explicit phase override applied on
        // top must keep TempoSource = DriverBeatAnchors with the identical recovered
        // BPM, while PhaseSource reports the user override separately. Pre-fix, ANY
        // phase override forced ResolveSource() to UserOverride, discarding the
        // anchor-tempo evidence and mislabeling the grid as a user tempo.
        double spq = Sr * 60.0 / 120.0; // 22050 samples/quarter → 120 BPM
        // Beat i sits at i*spq and maps to quarter i+1 (one-quarter-leading index), so
        // sample 0 holds quarter 1 and the anchors are exactly consistent with the
        // declared phase override below (no rejected/outlier anchors).
        var beats = Enumerable.Range(0, 12)
            .Select(i => new BeatEvent((long)Math.Round(i * spq), i + 1))
            .ToArray();
        var options = new MusicalTimeMapOptions
        {
            DetectTempoChanges = true,
            // Explicit phase override: quarter 1 at sample 0 (an offset, not null).
            BeatOffsetQuarter = 1.0,
        };

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(
            Timeline(beats, endSample: (long)Math.Round(13 * spq)), options);

        // Tempo source stays DRIVER (anchors), NOT demoted to the phase override.
        Assert.Equal(TimingSource.DriverBeatAnchors, build.Diagnostics.TempoSource);
        // The phase dimension is the user override, reported independently.
        Assert.Equal(TimingSource.UserOverride, build.Diagnostics.PhaseSource);
        // The driver tempo survives: one continuous segment recovered at 120 BPM.
        Assert.Single(build.Map.Segments);
        Assert.Equal(120.0, build.Map.Segments[0].BeatsPerMinute, precision: 2);
        // The phase override is honoured: quarter 1 at sample 0.
        Assert.Equal(1.0, build.Map.SampleToQuarterPosition(0), precision: 6);
        // No anchors rejected: the grid is clean and trustworthy.
        Assert.Equal(0, build.Diagnostics.RejectedAnchorCount);
        Assert.True(build.Diagnostics.IsTrustworthy);
    }

    // ---- T2: signed BeatOffsetSamples (negative/zero/positive) as explicit phase ----

    [Fact]
    public void SignedOffset_Negative_LandsPickupBeforeQuarterZero()
    {
        // T2: BeatOffsetSamples is SIGNED — negative means a pickup before quarter 0.
        // With the ADD-to-sample convention, quarter(s) = (s+offset)/spq, so quarter 0
        // is reached at sample -offset = +N; sample 0 therefore sits N samples shy of
        // quarter 0 (an anacrusis / pickup), not inside positive quarters.
        double spq = Sr * 60.0 / 120.0; // 22050 samples/quarter at 120 BPM
        long n = (long)Math.Round(spq);
        var timeline = Timeline(Array.Empty<BeatEvent>(), endSample: 8 * n);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { FixedBpm = 120, BeatOffsetSamples = -n }).Map;

        Assert.True(map.SampleToQuarterPosition(0) < 0,
            "negative offset must place sample 0 before quarter 0 (pickup)");
        Assert.Equal(-1.0, map.SampleToQuarterPosition(0), precision: 6);
        Assert.Equal(0.0, map.SampleToQuarterPosition(n), precision: 6); // quarter 0 at +N
    }

    [Fact]
    public void SignedOffset_Zero_IsExplicitQuarterZeroAtSampleZero()
    {
        // T2: a zero sample offset is an EXPLICIT phase, not "no phase". Quarter 0
        // must land exactly at sample 0 and the grid must NOT be PhaseUnknown — only
        // null (unset) means "no explicit phase".
        var timeline = Timeline(Array.Empty<BeatEvent>());

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { FixedBpm = 120, BeatOffsetSamples = 0 });

        Assert.Equal(0.0, build.Map.SampleToQuarterPosition(0), precision: 9);
        Assert.False(build.Diagnostics.PhaseUnknown,
            "explicit zero offset pins quarter 0 at sample 0; phase is known");
        Assert.Equal(TimingSource.UserOverride, build.Diagnostics.PhaseSource);
        Assert.Equal(TimingSource.UserOverride, build.Diagnostics.TempoSource);
    }

    [Fact]
    public void SignedOffset_Positive_QuarterZeroAheadOfSampleZero()
    {
        // T2: positive offset pushes quarter 0 ahead of sample 0 (quarter 0 at sample
        // -N); sample 0 maps to a positive quarter position and each quarter stays one
        // sample-period apart.
        double spq = Sr * 60.0 / 120.0;
        long n = (long)Math.Round(spq);
        var timeline = Timeline(Array.Empty<BeatEvent>(), endSample: 8 * n);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { FixedBpm = 120, BeatOffsetSamples = n }).Map;

        Assert.Equal(1.0, map.SampleToQuarterPosition(0), precision: 6);
        Assert.Equal(2.0, map.SampleToQuarterPosition(n), precision: 6);
    }

    [Fact]
    public void SignedOffset_DriverTempoSurvivesExplicitPhase()
    {
        // T2/D005: a signed sample offset overrides ONLY the phase dimension. The
        // driver-validated BPM rate must survive with the identical recovered BPM and
        // all tempo segments preserved, while PhaseSource reports the user override.
        double spq = Sr * 60.0 / 120.0;
        long n = (long)Math.Round(spq);
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            timing: new[] { new DriverTimingEvent(0, 0, 120.0) },
            endSample: 8 * n);

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { BeatOffsetSamples = -n });

        Assert.Equal(TimingSource.DriverValidatedTempo, build.Diagnostics.TempoSource);
        Assert.Equal(TimingSource.UserOverride, build.Diagnostics.PhaseSource);
        Assert.Single(build.Map.Segments);
        Assert.Equal(120.0, build.Map.Segments[0].BeatsPerMinute, precision: 2);
        Assert.True(build.Map.SampleToQuarterPosition(0) < 0);
    }

    // ---- T3: exact downbeat (no bar snapping) + MEM009 tempo-less offset throw ----

    [Fact]
    public void FirstDownbeatSample_ExactQuarter_NoBarSnapping()
    {
        // §15: FirstDownbeatSample must become an EXACT downbeat with NO bar
        // snapping. A downbeat landing at quarter 2.37 must stay 2.37 (bars are
        // relative to it) instead of being snapped to the nearest meter multiple
        // (which would yield 4.0 in a 4/4 bar).
        double bpm = 120;
        double spq = Sr * 60.0 / bpm; // 22050 samples/quarter at 120 BPM
        long downbeatSample = 52_259; // quarter ≈ 2.37 on the 120-BPM grid
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            endSample: downbeatSample + (long)spq);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = bpm,
            Meter = new Meter(4, 4),
            BeatOffsetSamples = 0,            // explicit phase: quarter 0 at sample 0
            FirstDownbeatSample = downbeatSample,
        }).Map;

        Assert.NotNull(map.FirstDownbeatQuarter);
        double expected = downbeatSample / spq;                 // 2.37002...
        double snappedToBar = Math.Round(expected / 4.0) * 4.0; // old behavior: 4.0
        // Exact downbeat: stays at its computed quarter position (not snapped).
        Assert.Equal(expected, map.FirstDownbeatQuarter!.Value, precision: 6);
        // Explicitly NOT snapped to the nearest 4-quarter bar boundary.
        Assert.NotEqual(snappedToBar, map.FirstDownbeatQuarter.Value, precision: 6);
    }

    [Fact]
    public void BeatOffsetSamples_NoDerivableTempo_ThrowsActionableMusicalTimingException()
    {
        // MEM009: a signed sample offset cannot be converted to quarters without a
        // derivable tempo. Previously the phase override silently no-opped (the grid
        // ignored the offset); now it must throw an actionable MusicalTimingException
        // telling the caller how to supply the tempo.
        var timeline = Timeline(Array.Empty<BeatEvent>());

        var ex = Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(timeline,
                new MusicalTimeMapOptions { BeatOffsetSamples = -12_345 }));

        Assert.Contains("derivable tempo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--bpm", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sample offset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- T015: strict-mode failures ----

    [Fact]
    public void ValidatedTempoChanges_BeforeTimelineStart_AreClippedToDomain()
    {
        // A valid map whose validated tempo transitions occur entirely before the
        // exported timeline's start must not produce a segment whose end precedes
        // its start (MusicalTimeMap.ValidateSegments rejects that). The changes are
        // clipped to the timeline domain (BeatGridFitter FitValidatedTempoSegments).
        const long startSample = 1000;
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            timing: new[]
            {
                new DriverTimingEvent(0, 0, 120.0),     // before timeline start
                new DriverTimingEvent(500, 0, 150.0),   // before timeline start
            },
            startSample: startSample);

        // Must not throw ('segment end precedes start'); the two pre-start changes
        // should be clipped to a single well-formed map in the [1000, ∞) domain.
        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { Source = TimingSource.DriverValidatedTempo }).Map;

        Assert.Equal(startSample, map.StartSample);
        // A sample after the timeline start still maps (map is well-formed).
        Assert.True(map.SampleToQuarterPosition(startSample + 11_025) > 0,
            "post-start sample should map to a positive quarter position");
    }

    [Fact]
    public void ValidatedTempoChanges_PartialClip_PreservesHeadTempo()
    {
        // timeline starts at 1000; validated changes 0=120 (before), 500=150 (before),
        // 2000=180 (inside). The active tempo at the timeline start is 150 (last
        // pre-domain change), so samples [1000, 2000) must map at 150 BPM, NOT 180.
        const long startSample = 1000;
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            timing: new[]
            {
                new DriverTimingEvent(0, 0, 120.0),
                new DriverTimingEvent(500, 0, 150.0),
                new DriverTimingEvent(2000, 0, 180.0),
            },
            startSample: startSample);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { Source = TimingSource.DriverValidatedTempo }).Map;

        // The head region (before the first retained change at 2000) must use the
        // validated tempo in effect at the timeline start (150 BPM, the last
        // pre-domain change), not the first retained change (180) and not the first
        // change (120).
        var head = map.Segments[0];
        Assert.True(Math.Abs(head.BeatsPerMinute - 150.0) < 0.5,
            $"head BPM should be 150, got {head.BeatsPerMinute}");
        // The retained transition at 2000 is honored as a real 180 BPM segment.
        Assert.True(map.Segments.Any(s => Math.Abs(s.BeatsPerMinute - 180.0) < 0.5),
            "map should contain the 180 BPM segment from the retained change");
    }

    // ---- T015: strict-mode failures ----

    [Fact]
    public void StrictMode_NoPhase_Throws()
    {
        // Fixed BPM but no anchors and no phase override ⇒ phase unknown ⇒ strict fail.
        var timeline = Timeline(Array.Empty<BeatEvent>());
        var ex = Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(timeline,
                new MusicalTimeMapOptions { FixedBpm = 120, StrictTiming = true }));
        Assert.Contains("phase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- T016/T017/T018: tempo transitions, continuity, jitter suppression ----

    [Fact]
    public void StrictMode_AnchorsFirstOverride_DoesNotRejectValidGrid()
    {
        // Valid constant 120-BPM anchors + ONE conflicting 150-BPM validated
        // observation. §10 anchors-first: the map must use the anchor-derived 120
        // grid, and the stale residual/rejection diagnostics from the discarded
        // validated fit must NOT make strict mode reject the corrected grid.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 6)
            .Select(i => new BeatEvent((long)Math.Round(i * spq), i))
            .ToArray();
        var timeline = Timeline(beats,
            timing: new[] { new DriverTimingEvent(0, 0, 150.0) });

        MusicalTimeMapBuildResult result = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { StrictTiming = true, Source = TimingSource.DriverValidatedTempo });


        // Anchor grid preserved (one constant 120-BPM segment), and strict did NOT throw.
        Assert.True(result.Diagnostics.IsTrustworthy,
            "anchors-first override must leave IsTrustworthy true (stale residuals cleared)");
        Assert.Single(result.Map.Segments);
        Assert.True(Math.Abs(result.Map.Segments[0].BeatsPerMinute - 120.0) < 0.5,
            $"expected 120 BPM anchor grid, got {result.Map.Segments[0].BeatsPerMinute}");
    }

    [Fact]
    public void ValidatedTempo_OneAnchor_DerivesPhaseForStrict()
    {
        // One authoritative beat anchor supplies phase; validated BPM supplies the
        // rate (§10). The builder must NOT force PhaseUnknown (or strict-fail) just
        // because there is only one anchor when the validated path derived a phase.
        double spq = Sr * 60.0 / 120.0;
        var timeline = Timeline(
            new[] { new BeatEvent(12_000L, 0) },
            timing: new[] { new DriverTimingEvent(0, 0, 120.0) });

        MusicalTimeMapBuildResult result = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { StrictTiming = true, Source = TimingSource.DriverValidatedTempo });

        Assert.False(result.Diagnostics.PhaseUnknown,
            "single anchor + validated BPM derives a phase and must not be PhaseUnknown");
        Assert.True(result.Diagnostics.IsTrustworthy,
            "validated one-anchor fit with derived phase must be trustworthy in strict mode");
        // The anchor at sample 12000 is quarter 0 (phase preserved).
        Assert.Equal(0.0, result.Map.SampleToQuarterPosition(12_000), precision: 6);
    }

    private static VisualizationTimeline TwoTempoTimeline()
    {
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round(16 * spqA);
        var beats = new List<BeatEvent>();
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent((long)Math.Round(i * spqA), i));
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent(boundary + (long)Math.Round(i * spqB), 16 + i));
        return Timeline(beats.ToArray(),
            timing: new[] { new DriverTimingEvent(boundary, 0, 150.0) },
            endSample: boundary + (long)Math.Round(16 * spqB) + 10_000);
    }

    [Fact]
    public void ValidatedTempoChange_ProducesContinuousSegments()
    {
        // §59: 120 BPM for 16 quarters → 150 BPM. Exactly one transition; the new
        // segment begins at the boundary sample and musical position is continuous.
        MusicalTimeMap map = MusicalTimeMapBuilder.Build(TwoTempoTimeline(),
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        Assert.Equal(2, map.Segments.Count);
        Assert.Equal(120.0, map.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(150.0, map.Segments[1].BeatsPerMinute, precision: 2);
        long boundary = map.Segments[1].StartSample;

        // Continuity: quarter position at the boundary from both sides is identical.
        Assert.Equal(map.Segments[0].QuarterPositionAt(boundary),
            map.Segments[1].QuarterPositionAtStart, precision: 9);
        // Tempo at the boundary is the NEW tempo (150 BPM), per §35.
        Assert.Equal(150.0, map.Segments[1].BeatsPerMinute, precision: 2);
    }

    [Fact]
    public void TempoChange_PreservesFittedPhase_BothSides()
    {
        // §20/§59: the fitted phase (sample 12000 = quarter 0) must carry unchanged
        // across the tempo transition; it must not reset to sample 0 = quarter 0.
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round((16) * spqA);
        var beats = new List<BeatEvent>();
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent(12_000 + (long)Math.Round(i * spqA), i));
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent(12_000 + boundary + (long)Math.Round(i * spqB), 16 + i));
        var timeline = Timeline(beats.ToArray(),
            timing: new[] { new DriverTimingEvent(12_000 + boundary, 0, 150.0) },
            endSample: 12_000 + boundary + (long)Math.Round(16 * spqB) + 10_000);

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        // Phase before and after the transition is anchored to sample 12000 = quarter 0.
        Assert.Equal(0.0, map.SampleToQuarterPosition(12_000), precision: 6);
        Assert.Equal(2, map.Segments.Count);
        long segStart = map.Segments[1].StartSample;
        // Quarter at the transition's start sample, computed via the previous segment:
        double expected = map.Segments[0].QuarterPositionAt(segStart);
        Assert.Equal(expected, map.Segments[1].QuarterPositionAtStart, precision: 9);
        Assert.Equal(150.0, map.Segments[1].BeatsPerMinute, precision: 2);
    }

    [Fact]
    public void JitterDrivenMicroVariations_DoNotSpawnSegments()
    {
        // Many validated BPM events all resolving to the same µs/qn (120 BPM) must
        // collapse to ONE segment, never a per-change fragment (§17, §19).
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 40)
            .Select(i => new BeatEvent((long)Math.Round(i * spq), i))
            .ToArray();
        var timing = Enumerable.Range(0, 40)
            .Select(i => new DriverTimingEvent((long)Math.Round(i * spq), 0, 120.0))
            .ToArray();
        MusicalTimeMap map = MusicalTimeMapBuilder.Build(Timeline(beats, timing),
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        Assert.Single(map.Segments);
        Assert.Equal(120.0, map.Segments[0].BeatsPerMinute, precision: 3);
    }

    [Fact]
    public void NearIdenticalValidatedBpm_CollapsesToOneSegment()
    {
        // Finding 1: 120.0 / 120.1 / 120.05 BPM jitter yields different rounded
        // µs/qn but must still collapse to ONE segment per §17 sustained filtering.
        double spq = Sr * 60.0 / 120.0;
        var timing = new[]
        {
            new DriverTimingEvent(0, 0, 120.0),
            new DriverTimingEvent((long)Math.Round(20 * spq), 0, 120.1),
            new DriverTimingEvent((long)Math.Round(40 * spq), 0, 120.05),
        };
        MusicalTimeMap chart = MusicalTimeMapBuilder.Build(Timeline(Array.Empty<BeatEvent>(), timing),
            new MusicalTimeMapOptions()).Map;

        Assert.Single(chart.Segments);
        Assert.Equal(120.0, chart.Segments[0].BeatsPerMinute, precision: 3);
    }

    [Fact]
    public void LoneValidatedBpm_IsNotATransition_SingleGrid()
    {
        // Finding 3: a single validated BPM event at a nonzero sample is an
        // observation, not a tempo transition. It must NOT fabricate pre/post
        // segments; the map stays ONE constant grid.
        double spq = Sr * 60.0 / 120.0;
        var timing = new[] { new DriverTimingEvent((long)Math.Round(10 * spq), 0, 120.0) };
        MusicalTimeMap chart = MusicalTimeMapBuilder.Build(Timeline(Array.Empty<BeatEvent>(), timing),
            new MusicalTimeMapOptions()).Map;

        Assert.Single(chart.Segments);
        Assert.Equal(120.0, chart.Segments[0].BeatsPerMinute, precision: 3);
        Assert.Equal(0, chart.Segments[0].StartSample);
    }

    [Fact]
    public void NoAnchorsValidatedTempo_WarnsPhaseUnknown()
    {
        // Finding P2: driver-validated tempo with no beat anchors knows the tempo
        // but not the beat phase. The phase-unknown warning must surface in
        // Diagnostics.Warnings so the CLI / timing report do not silently proceed
        // with an arbitrary sample-zero phase.
        double spq = Sr * 60.0 / 120.0;
        var timing = new[] { new DriverTimingEvent((long)Math.Round(10 * spq), 0, 120.0) };
        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(
            Timeline(Array.Empty<BeatEvent>(), timing), new MusicalTimeMapOptions());

        Assert.Single(build.Map.Segments);
        Assert.True(build.Diagnostics.PhaseUnknown);
        Assert.Contains(build.Diagnostics.Warnings,
            w => w.Contains("beat phase", StringComparison.OrdinalIgnoreCase)
                && w.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatedPath_SevereOutlier_StrictMode_Throws()
    {
        // Findings 2/4: on the validated-tempo path residuals were never assigned, so
        // a severe anchor outlier left RMS at 0 and strict mode accepted it. Now the
        // residuals must surface and strict mode must fail on the excessive residual.
        double spq = Sr * 60.0 / 120.0;
        // A beat reported 0.8 quarter late (between beats 4 and 5) is a residual
        // outlier, not a same-sample conflict: monotonic order is preserved, so it
        // survives validation and only the residual check can catch it.
        var beats = Enumerable.Range(0, 9)
            .Select(i => new BeatEvent((long)Math.Round(i * spq), i))
            .ToArray();
        beats[4] = new BeatEvent((long)Math.Round(4.8 * spq), 4);
        var timing = new[] { new DriverTimingEvent(0, 0, 120.0) };

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(
            Timeline(beats, timing), new MusicalTimeMapOptions { Source = TimingSource.DriverValidatedTempo });

        Assert.False(build.Diagnostics.HasConflictingAnchors);
        Assert.Contains(build.Diagnostics.RejectedAnchors,
            r => r.Sample == (long)Math.Round(4.8 * spq));
        Assert.True(build.Diagnostics.RmsResidualQuarters > 0.25);
        Assert.False(build.Diagnostics.IsTrustworthy);

        var ex = Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(Timeline(beats, timing),
                new MusicalTimeMapOptions { Source = TimingSource.DriverValidatedTempo, StrictTiming = true }));
        Assert.Contains("residual", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- T018: continuity across adjacent segments ----

    [Fact]
    public void AdjacentSegments_AreContinuous_InDoublePrecision()
    {
        MusicalTimeMap map = MusicalTimeMapBuilder.Build(TwoTempoTimeline(),
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;
        for (int i = 1; i < map.Segments.Count; i++)
        {
            TempoSegment next = map.Segments[i];
            TempoSegment previous = map.Segments[i - 1];
            Assert.Equal(previous.QuarterPositionAt(next.StartSample),
                next.QuarterPositionAtStart, precision: 12);
        }
    }

    // ---- T020: µs/quarter 24-bit representation validation ----

    [Fact]
    public void Segment_MicrosecondsPerQuarter_IsRounded60MOverBpm()
    {
        MusicalTimeMap map = MusicalTimeMapBuilder.Build(TwoTempoTimeline(),
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        Assert.Equal((int)Math.Round(60_000_000.0 / 120.0), map.Segments[0].MicrosecondsPerQuarter);
        Assert.Equal((int)Math.Round(60_000_000.0 / 150.0), map.Segments[1].MicrosecondsPerQuarter);
    }

    [Fact]
    public void UnrepresentableTempo_FailsWithActionableError_NotClamped()
    {
        // A tempo so slow that µs/quarter overflows MIDI's 24-bit Set Tempo range.
        // Must be rejected with a clear error rather than silently clamped.
        double spq = Sr * 60.0 / 3.0; // 3 BPM ⇒ 20_000_000 µs/qn > 0xFFFFFF
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            timing: new[] { new DriverTimingEvent(0, 0, 3.0) });

        var ex = Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions()));
        Assert.Contains("represent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- T019 helper: note/pitch crossing a tempo boundary ----

    [Fact]
    public void ValidatedTempoTransition_WithoutAnchors_ProducesContinuousSegments()
    {
        // A driver that only emits validated BPM transitions (no beat anchors) must
        // still produce a continuous 120→150 segment pair at the transition sample.
        double spqA = Sr * 60.0 / 120.0;
        long boundary = (long)Math.Round(16 * spqA);
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            timing: new[]
            {
                new DriverTimingEvent(0, 0, 120.0),
                new DriverTimingEvent(boundary, 0, 150.0),
            },
            endSample: boundary + (long)Math.Round(16 * (Sr * 60.0 / 150.0)));

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        Assert.Equal(2, map.Segments.Count);
        Assert.Equal(120.0, map.Segments[0].BeatsPerMinute, precision: 2);
        Assert.Equal(150.0, map.Segments[1].BeatsPerMinute, precision: 2);
        Assert.Equal(boundary, map.Segments[1].StartSample);
        Assert.Equal(map.Segments[0].QuarterPositionAt(boundary),
            map.Segments[1].QuarterPositionAtStart, precision: 9);
    }

    [Fact]
    public void NoteCrossingTempoChange_EndsDeriveFromSourceSamples()
    {
        // §60: note start = quarter 15.5, end = quarter 16.5, tempo change at 16.
        // Start and end ticks derive independently from their own source samples,
        // NOT endTick = startTick + fixed-duration.
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round(16 * spqA);
        long noteStart = (long)Math.Round(15.5 * spqA);   // before transition
        long noteEnd = boundary + (long)Math.Round(0.5 * spqB); // after transition
        var timeline = Timeline(
            beats: Enumerable.Range(0, 12)
                .Select(i => new BeatEvent((long)Math.Round(i * spqA), i))
                .Concat(Enumerable.Range(0, 12)
                    .Select(i => new BeatEvent(boundary + (long)Math.Round(i * spqB), 16 + i)))
                .ToArray(),
            timing: new[] { new DriverTimingEvent(boundary, 0, 150.0) },
            endSample: boundary + (long)Math.Round(12 * spqB),
            notes: new[] { NewNote("v", noteStart, noteEnd, 60) });

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;

        // Each endpoint derives from its own sample across the tempo boundary.
        // Note spans quarter 15.5 → 16.5 = exactly one quarter = 960 ticks at PPQ.
        long startTick = map.SampleToTick(noteStart, 960);
        long endTick = map.SampleToTick(noteEnd, 960);
        Assert.Equal(960, endTick - startTick);
        // The durations on each side of the boundary differ (0.5 quarter at 120 BPM
        // before, 0.5 quarter at 150 BPM after), so a startTick+fixedDuration bug
        // would give a different duration.
        double halfBeforeQuarters = map.SampleToQuarterPosition(boundary) - map.SampleToQuarterPosition(noteStart);
        double halfAfterQuarters = map.SampleToQuarterPosition(noteEnd) - map.SampleToQuarterPosition(boundary);
        Assert.Equal(0.5, halfBeforeQuarters, precision: 6);
        Assert.Equal(0.5, halfAfterQuarters, precision: 6);
        // If duration were computed as ticks at a single fixed tempo, the total
        // tick span would be ~960 at 120 BPM for the sample gap, so verify the map
        // split at the boundary rather than assuming one tempo for both halves.
        Assert.Equal(2, map.Segments.Count);
    }

    // ---- Patch 3: MusicalTimeMap confidence/ambiguity surface ----

    [Fact]
    public void SymbolicInference_MapExposesConfidenceAndAmbiguity()
    {
        double spq = Sr * 60.0 / 120.0;
        NoteEvent[] notes = Enumerable.Range(0, 32)
            .Select(i => NewNote("v", (long)Math.Round(i * spq), (long)Math.Round(i * spq + 0.5 * spq), 60))
            .ToArray();
        var timeline = Timeline(Array.Empty<BeatEvent>(), notes: notes,
            endSample: (long)Math.Round(32 * spq));

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });

        Assert.NotNull(build.Diagnostics.TempoConfidence);
        Assert.Equal(build.Diagnostics.TempoConfidence!.Value, build.Map.Confidence, precision: 12);
        Assert.Equal(build.Diagnostics.AlternativeBpm, build.Map.AlternateBpm);
        Assert.Equal(build.Diagnostics.TempoAmbiguous, build.Map.IsTempoAmbiguous);
    }

    [Fact]
    public void FixedTempo_MapConfidenceIsOne_WithoutAmbiguity()
    {
        var timeline = Timeline(Array.Empty<BeatEvent>(),
            endSample: (long)Math.Round(Sr * 60.0 / 120.0 * 8));

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
            new MusicalTimeMapOptions { FixedBpm = 120 });

        Assert.Equal(1.0, build.Map.Confidence, precision: 12);
        Assert.Null(build.Map.AlternateBpm);
        Assert.False(build.Map.IsTempoAmbiguous);
    }
}