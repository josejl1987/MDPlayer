// -----------------------------------------------------------------------------
// Producer audit (WP01 T001) — enumeration of AddTiming/AddBeat producers
// -----------------------------------------------------------------------------
// This module is the SINGLE producer-boundary normalization adapter for the
// timeline's musical sample clock (spec §4.1, plan IC-01, decision
// 01KZGN2DH6C26YQ7NTXGCN4MT7). It is wired into TimelineBuilder.Merge so that a
// producer on a clock that differs from the final playback/output sample clock
// is normalized exactly once, BEFORE MusicalTimeMapBuilder sees any anchor or
// timing event. A producer whose clock relationship cannot be established is
// rejected with an actionable MusicalTimingException (never silently assumed
// equal, never compensated in the MIDI layer).
//
// Literal audit results (MDPlayer/src, 2026-08-08):
//
//   Producer                       Invoked by                          Sample clock         Fields set
//   -----------------------------  ----------------------------------  -------------------  ---------
//   Ym2608TimelineDecoder          Ym2608TimelineDecoderAdapter        session/playback      DriverTimingEvent(SamplePosition,
//     ApplyYm2608 (port0 reg 0x26)  -> TimelineDecoderEventSink          clock                   TimerBValue, ValidatedBpm)
//   TimelineBuilder.AddTiming       TimelineDecoderEventSink.Complete  algorithm             appends DriverTimingEvent
//     (ingestion)                   / Merge / direct callers           capture clock
//   TimelineBuilder.AddBeat         Merge (forwards timeline.Beats)    algorithm             appends BeatEvent
//     (ingestion)                   / direct test setup                capture clock
//   TimelineBuilder.Merge           Ym2608TimelineDecoderAdapter.      same clock            forwards Timing[]/Beats[]
//     (clock/scale forwarding)      Complete(); TimelineDecoderEvent   as the timeline        and all timed families
//                                   Sink.Complete()
//
//   No runtime `new BeatEvent(...)` constructor exists under
//   MDPlayer/src/MDPlayer.Fmp.Core except through Merge/test setup:
//   timeline-provided beats are an input contract audited at this boundary.
//
// Clock-equivalence conclusion (T004):
//   * Within a single capture, decoders receive `samplePosition` values from
//     the playback session, and TimelineBuilder is constructed with that same
//     rate, so all timed event families (timing, beats, notes, pitch, rhythm,
//     DAC, markers) already share the timeline/playback clock.
//   * The one provable drift risk is an external serialized timeline whose
//     SampleRate metadata is stale/missing, or a producer that declares an
//     explicit different clock. This adapter is the guard that catches both:
//     it converts an explicit, unambiguous mismatch exactly once and rejects
//     an ambiguous/unknown clock instead of hiding it.
//
//   No MIDI component compensates for a clock mismatch downstream. The raw
//   MidiTranscriber consumes the already normalized timeline and applies only its
//   fixed sample-to-tick conversion. Do not add sample-rate/BPM compensation downstream.
// -----------------------------------------------------------------------------

using Fmp.Core.Timing;

namespace Fmp.Core.Visualization;

/// <summary>
/// Deterministic sample-position rounding policy used when a producer's explicit
/// source clock differs from the final timeline/playback clock.
/// <see cref="Round"/> is monotonic, so converting a set of samples preserves
/// source ordering (for samples <c>a &lt; b</c>, <c>round(a·r) ≤ round(b·r)</c>).
/// </summary>
public enum ProducerSampleRounding
{
    /// <summary>Round to nearest sample (<see cref="MidpointRounding.AwayFromZero"/>).</summary>
    Round,
    /// <summary>Floor to the containing sample (never rounds a position up).</summary>
    Floor,
    /// <summary>Ceiling to the next sample (never rounds a position down).</summary>
    Ceiling,
}

/// <summary>
/// The single producer-boundary normalization adapter for the timeline's musical
/// sample clock. It converts an event's sample position from a producer's
/// declared source clock to the final timeline/playback sample clock exactly
/// once. A producer whose source clock is unknown/ambiguous, or a destination
/// clock that cannot be established, is rejected with
/// <see cref="MusicalTimingException"/> carrying an actionable diagnostic.
///
/// This adapter does NOT interpret <see cref="BeatEvent.BeatIndex"/> units or
/// <see cref="DriverTimingEvent.ValidatedBpm"/> semantics — those are documented
/// and locked by regression tests separately (see ProducerClockNormalizationTests,
/// Ym2608TimelineDecoderTests). It only normalizes the sample clock.
/// </summary>
internal static class ProducerClockNormalization
{
    /// <summary>
    /// Converts <paramref name="sourceSample"/> from <paramref name="sourceRate"/>
    /// (the producer's raw/backend clock) to <paramref name="destinationRate"/>
    /// (the final timeline/playback clock) using a deterministic rounding policy.
    /// </summary>
    /// <param name="producer">
    /// Stable producer identity used only to make the rejection diagnostic
    /// actionable (e.g. "serialized-timeline").
    /// </param>
    /// <param name="sourceSample">Sample position emitted by the producer.</param>
    /// <param name="sourceRate">Producer source clock in Hz; must be positive and known.</param>
    /// <param name="destinationRate">Final timeline/playback clock in Hz; must be positive and known.</param>
    /// <param name="rounding">Deterministic rounding policy; default <see cref="ProducerSampleRounding.Round"/>.</param>
    /// <exception cref="MusicalTimingException">
    /// Thrown when <paramref name="sourceRate"/> or <paramref name="destinationRate"/> is
    /// not a positive, known rate, i.e. the producer's clock relationship is ambiguous.
    /// </exception>
    public static long ConvertSamplePosition(
        string producer,
        long sourceSample,
        int sourceRate,
        int destinationRate,
        ProducerSampleRounding rounding = ProducerSampleRounding.Round)
    {
        if (sourceRate <= 0)
            throw new MusicalTimingException(
                $"Cannot establish timing sample clock for producer '{producer}': source rate unknown ({sourceRate}).");
        if (destinationRate <= 0)
            throw new MusicalTimingException(
                $"Cannot establish timing sample clock for producer '{producer}': destination rate unknown ({destinationRate}).");

        // Source and destination clocks agree: the relationship is explicit and
        // unambiguous, so the event passes through unchanged (spec §4.1).
        if (sourceRate == destinationRate)
            return sourceSample;

        // Explicit, unambiguous mismatch: convert exactly once at this boundary.
        // The scale is deterministic and strictly monotonic, so converting an
        // ordered event stream preserves source ordering.
        double scale = destinationRate / (double)sourceRate;
        double exact = sourceSample * scale;
        return rounding switch
        {
            ProducerSampleRounding.Floor => (long)Math.Floor(exact),
            ProducerSampleRounding.Ceiling => (long)Math.Ceiling(exact),
            _ => (long)Math.Round(exact, MidpointRounding.AwayFromZero),
        };
    }
}