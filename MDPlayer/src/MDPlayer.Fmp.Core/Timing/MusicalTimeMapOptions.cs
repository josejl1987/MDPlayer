#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// Tunables for <see cref="MusicalTimeMapBuilder"/>. Everything is optional;
/// sensible defaults resolve to "use the strongest available evidence".
/// </summary>
internal sealed class MusicalTimeMapOptions
{
    /// <summary>How many quarter notes one <c>BeatIndex</c> step represents. The locked
    /// producer-boundary convention (FR-009) is that a BeatIndex increment of 1.0
    /// equals exactly one MIDI quarter note; this scale factor adapts that when the
    /// driver beat unit differs. It is applied exactly once, at the producer
    /// boundary, in <c>MusicalTimeMapBuilder.BuildAnchors</c>
    /// (quarter = BeatIndex * QuartersPerBeat). Default 1.0 (beat == quarter note).</summary>
    public double QuartersPerBeat { get; init; } = 1.0;

    /// <summary>Fixed tempo override (BPM). When set alongside anchors it fixes the
    /// segment tempo; otherwise establishes a constant-tempo grid.</summary>
    public double? FixedBpm { get; init; }

    /// <summary>Absolute quarter position of sample zero (beat phase override).</summary>
    public double? BeatOffsetQuarter { get; init; }

    /// <summary>Beat offset expressed in samples (converted to quarters internally).
    /// The offset is SIGNED and any set value (including 0) is an explicit phase
    /// (D005/T2): negative lands a pickup before quarter 0, +0 pins quarter 0 at
    /// sample 0, positive pushes quarter 0 ahead of sample 0. Only null (unset)
    /// means "no explicit phase".</summary>
    public long? BeatOffsetSamples { get; init; }

    /// <summary>User-provided meter (time signature).</summary>
    public Meter? Meter { get; init; }

    /// <summary>Explicit downbeat (sample of the first bar start).</summary>
    public long? FirstDownbeatSample { get; init; }

    /// <summary>Force a specific timing source; null lets the builder auto-select.</summary>
    public TimingSource? Source { get; init; }

    /// <summary>Allow anchor-based tempo-change segmentation (Batch 2).</summary>
    public bool DetectTempoChanges { get; init; }

    /// <summary>When true, throw instead of guessing phase/tempo on insufficient evidence.</summary>
    public bool StrictTiming { get; init; }

    /// <summary>
    /// Runs the repeated-content structural grid pass after symbolic timing. MIDI
    /// export already has Ellis/DBN timing and can disable this quadratic analysis
    /// for large timelines without changing source-time or pitch semantics.
    /// </summary>
    public bool EnableStructuralGridSelection { get; init; } = true;

    /// <summary>
    /// Keeps the pre-Ellis symbolic hierarchy available for compatibility tests
    /// and diagnostics. Production musical export uses the bounded Ellis/DBN
    /// path instead; the legacy hierarchy has a quadratic candidate scorer.
    /// </summary>
    public bool EnableLegacyHierarchyInference { get; init; }
}
