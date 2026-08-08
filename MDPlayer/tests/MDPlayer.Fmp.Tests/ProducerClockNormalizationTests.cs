using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Regression tests locking the producer sample-clock normalization boundary
/// (spec §4.1, plan IC-01, decision 01KZGN2DH6C26YQ7NTXGCN4MT7):
///
///  * an unambiguous clock mismatch converts every timed event family exactly
///    once at the producer boundary;
///  * an ambiguous clock (unknown source/destination rate) is rejected with an
///    actionable <see cref="MusicalTimingException"/>;
///  * a matching clock passes through unchanged;
///  * source ordering is preserved;
///  * <see cref="BeatEvent.BeatIndex"/> unit semantics are only normalized by
///    the explicit <c>QuartersPerBeat</c> scale in map construction, never by the
///    MIDI writer.
/// </summary>
public sealed class ProducerClockNormalizationTests
{
    private const int SourceRate = 22_050;
    private const int DestinationRate = 44_100;

    // ---- T004: clock normalization at the producer boundary ----

    [Fact]
    public void UnambiguousClockMismatch_ConvertsExactlyOnce()
    {
        // A 22050 Hz producer event at sample 1000 lands at sample 2000 on the
        // 44100 Hz destination playback clock.
        long converted = ProducerClockNormalization.ConvertSamplePosition(
            "test-producer", sourceSample: 1_000, SourceRate, DestinationRate);
        Assert.Equal(2_000, converted);
    }

    [Fact]
    public void AmbiguousClock_UnknownSourceRate_ThrowsActionable()
    {
        var ex = Assert.Throws<MusicalTimingException>(() =>
            ProducerClockNormalization.ConvertSamplePosition(
                "ym2608-timer-b", sourceSample: 100, sourceRate: 0, DestinationRate));
        Assert.Contains("source rate unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ym2608-timer-b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousClock_UnknownDestinationRate_ThrowsActionable()
    {
        var ex = Assert.Throws<MusicalTimingException>(() =>
            ProducerClockNormalization.ConvertSamplePosition(
                "serialized-timeline", sourceSample: 100, SourceRate, destinationRate: 0));
        Assert.Contains("destination rate unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchingClock_PassesThroughUnchanged()
    {
        long converted = ProducerClockNormalization.ConvertSamplePosition(
            "test-producer", sourceSample: 123_456, 44_100, 44_100);
        Assert.Equal(123_456, converted);
    }

    [Theory]
    [InlineData(ProducerSampleRounding.Round)]
    [InlineData(ProducerSampleRounding.Floor)]
    [InlineData(ProducerSampleRounding.Ceiling)]
    public void ConversionPreservesSourceOrdering(ProducerSampleRounding rounding)
    {
        long[] sources = [0, 1, 750, 1_100, 2_000, 9_999];
        long[] converted = sources
            .Select(sample => ProducerClockNormalization.ConvertSamplePosition(
                "test", sample, SourceRate, DestinationRate, rounding))
            .ToArray();
        for (int index = 1; index < converted.Length; index++)
            Assert.True(converted[index - 1] <= converted[index],
                $"ordering broken with {rounding}: {converted[index - 1]} > {converted[index]}");
    }

    [Fact]
    public void Downscale_IsDeterministicAndIdempotent()
    {
        // 44100 -> 22050 halves every position exactly.
        Assert.Equal(500, ProducerClockNormalization.ConvertSamplePosition(
            "test", 1_000, 44_100, 22_050));
    }

    // ---- T004/T005: Merge normalizes EVERY timed family at the boundary ----

    [Fact]
    public void Merge_DifferentExplicitClock_NormalizesAllTimedFamiliesOnce()
    {
        var source = new VisualizationTimeline
        {
            SampleRate = SourceRate,
            StartSample = 0,
            EndSample = 40_000,
            Timing =
            [
                new DriverTimingEvent(1_000, 0x42, 120.0),
            ],
            Beats =
            [
                new BeatEvent(1_000, 1.0),
            ],
            Notes =
            [
                new NoteEvent(
                    "v", 1_000, 2_000, 440, 69, "i", VisualizationNoteMode.Fm, false,
                    [new PitchChange(1_500, 440, 70)]),
            ],
            Rhythm =
            [
                new RhythmEvent("bd", "ym2608.0.rhythm.bd", 1_000, 1.0f, 0, "ym2608.0.rhythm"),
            ],
        };

        var builder = new TimelineBuilder(DestinationRate);
        builder.Merge(source);
        VisualizationTimeline merged = builder.Build(20_000, "test");

        // Every timed family was converted 22050 -> 44100 exactly once.
        Assert.Equal(2_000, Assert.Single(merged.Timing).SamplePosition);
        Assert.Equal(2_000, Assert.Single(merged.Beats).SamplePosition);
        NoteEvent note = Assert.Single(merged.Notes);
        Assert.Equal(2_000, note.StartSample);
        Assert.Equal(4_000, note.EndSample);
        Assert.Equal(3_000, Assert.Single(note.Pitch).SamplePosition);
        Assert.Equal(2_000, Assert.Single(merged.Rhythm).SamplePosition);
    }

    [Fact]
    public void Merge_UnknownSourceClock_RejectsTimelineAtBoundary()
    {
        // A serialized timeline that lost its SampleRate metadata must be
        // rejected, not silently treated as matching the destination clock.
        var source = new VisualizationTimeline
        {
            SampleRate = 0,
            StartSample = 0,
            EndSample = 1_000,
            Timing = [new DriverTimingEvent(100, 0x42, 120.0)],
        };

        var builder = new TimelineBuilder(DestinationRate);
        var ex = Assert.Throws<MusicalTimingException>(() => builder.Merge(source));
        Assert.Contains("source rate unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Merge_MatchingClock_PassesEventsUnchanged()
    {
        var source = new VisualizationTimeline
        {
            SampleRate = DestinationRate,
            StartSample = 0,
            EndSample = 20_000,
            Timing = [new DriverTimingEvent(1_000, 0x42, 120.0)],
            Beats = [new BeatEvent(1_000, 1.0)],
            Notes =
            [
                new NoteEvent("v", 1_000, 2_000, 440, 69, "i", VisualizationNoteMode.Fm, false, []),
            ],
        };

        var builder = new TimelineBuilder(DestinationRate);
        builder.Merge(source);
        VisualizationTimeline merged = builder.Build(20_000, "test");

        Assert.Equal(1_000, Assert.Single(merged.Timing).SamplePosition);
        Assert.Equal(1_000, Assert.Single(merged.Beats).SamplePosition);
        Assert.Equal(1_000, Assert.Single(merged.Notes).StartSample);
    }

    // ---- T002: BeatIndex unit semantics ----

    [Fact]
    public void BeatIndex_IsScaledByQuartersPerBeat_InMapConstructionOnly()
    {
        // The audit (T004) could not find a runtime decoder that emits BeatEvents:
        // the YM2608 Timer-B producer emits DriverTimingEvent, and timeline beats
        // are an input contract sourced from a merged/decoded timeline. What IS
        // locked here — through the REAL production pathway, not a reimplementation
        // of BuildAnchors — is that a driver beat is NOT assumed to equal a MIDI
        // quarter note: the explicit QuartersPerBeat scale converts the BeatIndex
        // to quarter positions in MusicalTimeMapBuilder, and the MIDI writer never
        // performs its own beat/quarter conversion.
        //
        // Locked via MusicalTimeMapBuilder.Build + MusicalTimeMap so the assertion
        // exercises the identical anchor construction the production path uses
        // (BuildAnchors → BeatGridFitter → AssembleMap).
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44_100,
            StartSample = 0,
            EndSample = 44_100 * 2,
            Beats =
            [
                new BeatEvent(0, 0),
                new BeatEvent(22_050, 1),
                new BeatEvent(44_100, 2),
            ],
        };

        // Default: driver beat == quarter note (no scale).
        MusicalTimeMap identity = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;
        Assert.Equal(0d, identity.SampleToQuarterPosition(0), precision: 9);
        Assert.Equal(1d, identity.SampleToQuarterPosition(22_050), precision: 9);
        Assert.Equal(2d, identity.SampleToQuarterPosition(44_100), precision: 9);

        // Explicitly declared non-quarter driver beat: 4 driver beats per MIDI
        // quarter (i.e. one driver beat == a 16th note).
        MusicalTimeMap scaled = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions { QuartersPerBeat = 0.25, DetectTempoChanges = true }).Map;
        Assert.Equal(0d, scaled.SampleToQuarterPosition(0), precision: 9);
        Assert.Equal(0.25, scaled.SampleToQuarterPosition(22_050), precision: 9);
        Assert.Equal(0.5, scaled.SampleToQuarterPosition(44_100), precision: 9);
    }

    [Fact]
    public void BeatIndex_NonZeroFirstAndFractionalValues_ArePreserved()
    {
        // Fractional and non-zero-first BeatIndex values are valid input; the
        // boundary must not reject or round them (they are normalized, if at
        // all, only by the explicit QuartersPerBeat scale). Same real pathway as
        // the identity test: the map's sample→quarter mapping reflects the
        // BeatIndex values verbatim.
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44_100,
            StartSample = 0,
            EndSample = 44_100 * 3,
            Beats =
            [
                new BeatEvent(0, 2.5),
                new BeatEvent(22_050, 3.25),
                new BeatEvent(44_100, 4),
            ],
        };

        MusicalTimeMap map = MusicalTimeMapBuilder.Build(
            timeline, new MusicalTimeMapOptions { DetectTempoChanges = true }).Map;
        Assert.Equal(2.5, map.SampleToQuarterPosition(0), precision: 9);
        Assert.Equal(3.25, map.SampleToQuarterPosition(22_050), precision: 9);
        Assert.Equal(4.0, map.SampleToQuarterPosition(44_100), precision: 9);
    }

    // ---- T003: ValidatedBpm activation semantics ----

    [Fact]
    public void ValidatedBpm_EventSampleIsWhereNewValueBecomesEffective()
    {
        // YM2608 Timer-B register writes produce DriverTimingEvent records whose
        // SamplePosition is the register-write sample. The adopted convention
        // (documented in Ym2608TimelineDecoder.ComputeTimerBpm): the sample is
        // "new tempo begins here" — the timer value becomes the effective
        // playback tempo from that sample onward. On the default 7 987 200 Hz
        // clock no Timer-B value maps into [20,400] BPM, so a slower master
        // clock is used here purely to drive a finite, positive ValidatedBpm
        // (value 0x42, ticks = (0x100-0x42)<<4 = 3040, ~120 BPM).
        var decoder = new Ym2608TimelineDecoder(masterClock: 875_520);
        decoder.ApplyYm2608(0, 0, 0x26, 0x42, 123);

        DriverTimingEvent timing = Assert.Single(decoder.Complete(200, 44_100, "test").Timing);
        Assert.Equal(123, timing.SamplePosition); // activation sample preserved
        Assert.NotNull(timing.ValidatedBpm);
        Assert.InRange(timing.ValidatedBpm.Value, 119, 121);
        Assert.True(timing.ValidatedBpm.Value > 0 && double.IsFinite(timing.ValidatedBpm.Value),
            "ValidatedBpm is usable only when finite and positive");
    }

    [Fact]
    public void ValidatedBpm_NonPhysicalTimer_IsNullNotReverseEngineered()
    {
        // When the timer value maps to a non-physical (zero/very fast) period,
        // ValidatedBpm is null; consumers fall back. MIDI code must not
        // reverse-engineer BPM from TimerBValue.
        var decoder = new Ym2608TimelineDecoder(masterClock: 7_987_200);
        decoder.ApplyYm2608(0, 0, 0x26, 0xFF, 10);

        DriverTimingEvent timing = Assert.Single(decoder.Complete(100, 44_100, "test").Timing);
        Assert.Null(timing.ValidatedBpm);
        Assert.Equal(0xFF, timing.TimerBValue);
    }

    [Fact]
    public void ComputeTimerBpm_LocksOneInterruptEqualsOneQuarter()
    {
        // Locks the YM2608 Timer-B clock semantics that feed the musical time grid:
        // the OPN timer period is ((0x100 - value) << 4) / (masterClock / 144),
        // and ONE timer interrupt is treated as ONE quarter note, so the validated
        // BPM = 60 * (timer-interrupt Hz) where each interrupt is a quarter.
        //
        // masterClock 875 520 → masterClock/144 = 6080 Hz timer base.
        // value 0x42 → ticks = (0x100 - 0x42) << 4 = 190 << 4 = 3040.
        // interruptHz = 6080 / 3040 = 2.0 Hz → at one quarter per interrupt,
        // BPM = 60 * 2.0 = exactly 120.
        var decoder = new Ym2608TimelineDecoder(masterClock: 875_520);
        decoder.ApplyYm2608(0, 0, 0x26, 0x42, 0);

        DriverTimingEvent timing = Assert.Single(decoder.Complete(100, 44_100, "test").Timing);
        Assert.Equal(120.0, timing.ValidatedBpm!.Value, precision: 9);
    }
}