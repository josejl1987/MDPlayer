#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>
/// Configurable thresholds for the pitch-normalization stage. Instrumentation-first
/// (FR-6): these are CONSERVATIVE calibration placeholders — every value is
/// settable and meant to be recalibrated from real --pitch-report corpus data,
/// never treated as final. The deadband values sit deliberately close to the FNUM
/// noise floor (~0.87c at fNumber ≈ 2000) so hysteresis is mandatory (FR-3).
/// </summary>
internal sealed class PitchNormalizationThresholds
{
    public static readonly PitchNormalizationThresholds Default = new();

    /// <summary>Pass 1 exact-dedup grid step, in cents. A change is dropped when its
    /// pitch quantized to this grid equals the previous retained change's quantized
    /// pitch. Pitch units ONLY — never FNUM units (FR-2).</summary>
    public double DedupGridCents { get; init; } = 0.5;

    /// <summary>Pass 2 deadband: while suppressing, a change is dropped when its
    /// deviation from the last retained center is at most EnterCents. The worst-case
    /// suppressed drift is bounded by this value (documented contract, D5).</summary>
    public double DeadbandEnterCents { get; init; } = 0.75;

    /// <summary>Pass 2 hysteresis: after a retained change, a value within ExitCents
    /// re-centers the band and returns to suppression. Enter must stay greater than
    /// Exit so a value at the boundary never ties (D5).</summary>
    public double DeadbandExitCents { get; init; } = 0.5;

    /// <summary>Pass 3 stable-region classification: a note is stable when NO pitch
    /// change deviates from its initial pitch by more than this (excludes portamento,
    /// vibrato, attack transients and intentional bends BY MAGNITUDE).</summary>
    public double StableDeviationMaxCents { get; init; } = 5.0;

    /// <summary>Pass 3 stable-region classification: no pair of consecutive changes
    /// may move by more than this, or the note counts as expressive movement.</summary>
    public double MaxInterChangeCents { get; init; } = 2.5;

    /// <summary>Pass 3: a note is a candidate stable region only when it lasts at
    /// least this many samples.</summary>
    public long MinStableDurationSamples { get; init; } = 2000;

    /// <summary>Pass 3: attack-transient guard — pitch changes inside this window
    /// after the note start are not counted as instability (they fold into the
    /// initial pitch at the decoder level).</summary>
    public long FoldWindowSamples { get; init; } = 128;

    /// <summary>Pass 3 clustering: residuals are greedily merged while the cluster
    /// span stays within this many cents (±ClusterSpanCents / 2 around the mode).</summary>
    public double ClusterSpanCents { get; init; } = 4.0;

    /// <summary>Pass 3 acceptance: the residual cluster must cover at least this
    /// fraction of the domain's stable note attacks.</summary>
    public double CoverageMin { get; init; } = 0.60;

    /// <summary>Pass 3 acceptance: the cluster must span at least this many distinct
    /// MIDI note numbers (a 2–3 note synthetic fixture can never pass).</summary>
    public int MinDistinctNoteNumbers { get; init; } = 4;

    /// <summary>Pass 3 acceptance: mean absolute deviation of the cluster residuals
    /// from the cluster mode must not exceed this many cents.</summary>
    public double MadMaxCents { get; init; } = 2.0;

    /// <summary>Pass 3 acceptance: the last PersistenceAttacks residuals must all
    /// land in the cluster (persistence), with a floor of PersistenceMinAttacks
    /// attacks before the check is meaningful.</summary>
    public int PersistenceAttacks { get; init; } = 64;

    /// <summary>Pass 3 acceptance: minimum attack count before persistence applies.</summary>
    public int PersistenceMinAttacks { get; init; } = 8;

    /// <summary>Pass 3: accepted |bias| cap — beyond this a coarse RPN 0x0001 path
    /// is required instead of fine-only tuning.</summary>
    public double DomainBiasCapCents { get; init; } = 100.0;

    /// <summary>Pass 3 per-chip cap: SNES DSP is typically source-pitched, so a bias
    /// beyond this is rejected (Smash Up cannot emit an audibly wrong tuning).</summary>
    public double SnesDspBiasCapCents { get; init; } = 15.0;

    /// <summary>DAW-friendly mode: bias is snapped to equal temperament only when
    /// |bias| is at most this; a larger bias emits no tuning and is left raw.</summary>
    public double DawFriendlySnapMaxCents { get; init; } = 50.0;

    /// <summary>Pass 3: |bias| beyond this is rejected outright (would require a
    /// tuning state no MIDI channel should carry).</summary>
    public double CoarseBiasCapCents { get; init; } = 600.0;
}

/// <summary>A normalized pitch change: same sample, pitch expressed in the normalized
/// domain (raw source pitch minus the domain's accepted tuning bias).</summary>
internal sealed record NormalizedPitchChange(long SamplePosition, double MidiNote);

/// <summary>
/// Normalized pitch view of ONE source note. Consumers (ShouldFoldPitch,
/// ComputeOriginShiftTicks, EmitNote/BuildPitchAnchors) read pitch exclusively
/// through this view so the origin shift and the emitted bends always cover the
/// SAME normalized event set (FR-1 coupling — ticks never drift from the set the
/// exporter actually serializes).
/// </summary>
internal sealed record NormalizedNoteView(
    NoteEvent Source,
    double InitialMidiNote,
    IReadOnlyList<NormalizedPitchChange>? Changes);

/// <summary>
/// The normalized pitch model: one view per source note, produced once by
/// <see cref="PitchNormalizationStage.Normalize"/> and consumed by every pitch
/// consumer of the exporter. Reference-identity keyed by <see cref="NoteEvent"/>.
/// </summary>
internal sealed record PitchNormalizationModel(
    IReadOnlyDictionary<NoteEvent, NormalizedNoteView> Views);

/// <summary>
/// The pitch-normalization stage (FR-1). A pure, deterministic function of the
/// timeline: identical inputs produce identical IEEE results, so re-exports are
/// byte-identical. The pipeline is a monotone sequence of REMOVE-ONLY passes over
/// each note's pitch-change list (event count never grows); every pass is
/// independently testable and revertable:
///
///   P0  same-sample final-wins collapse (mirrors BuildPitchAnchors — the stage
///       never flips a same-sample final write to an earlier one)
///   P1  exact-event dedup on a pitch grid (FR-2, pitch units ONLY)
///   P2  deadband + hysteresis stable-run compression (FR-3)      [next unit]
///   P3  consecutive-identical suppression                         [next unit]
///   P4  per-domain tuning-bias subtraction (FR-4)                 [next unit]
///
/// The run-start anchor (the folded initial pitch) is state #0 of every pass: it is
/// never dropped, but it participates in dedup/compression comparisons so a change
/// that reproduces the initial state is recognized as noise.
/// </summary>
internal static class PitchNormalizationStage
{
    public static PitchNormalizationModel Normalize(
        VisualizationTimeline timeline,
        PitchNormalizationThresholds thresholds)
    {
        var notes = timeline.Notes ?? Array.Empty<NoteEvent>();
        var views = new Dictionary<NoteEvent, NormalizedNoteView>(notes.Count);
        foreach (NoteEvent note in notes)
        {
            views[note] = new NormalizedNoteView(
                note,
                note.InitialMidiNote,
                NormalizeChanges(note, thresholds));
        }
        return new PitchNormalizationModel(views);
    }

    private static IReadOnlyList<NormalizedPitchChange>? NormalizeChanges(
        NoteEvent note, PitchNormalizationThresholds thresholds)
    {
        if (note.Pitch is null)
            return null;
        if (note.Pitch.Count == 0)
            return Array.Empty<NormalizedPitchChange>();
        List<PitchSample> samples = SameSampleCollapse(note.Pitch);
        samples = Pass1ExactDedup(note.InitialMidiNote, samples, thresholds.DedupGridCents);
        // Pass 2 (deadband/hysteresis), Pass 3 (consecutive-identical) and Pass 4
        // (per-domain bias subtraction) are added by the following work units.
        return samples.Select(s => new NormalizedPitchChange(s.SamplePosition, s.MidiNote)).ToList();
    }

    /// <summary>Working pitch sample (decoder order is chronological, so list order
    /// is source order).</summary>
    private readonly record struct PitchSample(long SamplePosition, double MidiNote);

    /// <summary>P0: same-sample collapse, final (latest) write wins — the exact rule
    /// BuildPitchAnchors applies downstream, applied here so P1/P2 never decide a
    /// same-sample race against an earlier write.</summary>
    private static List<PitchSample> SameSampleCollapse(IReadOnlyList<PitchChange> raw)
    {
        var collapsed = new List<PitchSample>(raw.Count);
        foreach (PitchChange c in raw)
        {
            if (collapsed.Count > 0 && collapsed[^1].SamplePosition == c.SamplePosition)
                collapsed[^1] = new PitchSample(c.SamplePosition, c.MidiNote);
            else
                collapsed.Add(new PitchSample(c.SamplePosition, c.MidiNote));
        }
        return collapsed;
    }

    /// <summary>
    /// P1 exact-event dedup (FR-2 / SC-2): quantize each change's pitch to a
    /// <paramref name="gridCents"/> grid and drop every change whose quantized pitch
    /// equals the previous RETAINED change's quantized pitch. The first occurrence of
    /// each quantized value is kept (with its sample). The run-start anchor — the
    /// folded initial pitch — is state #0: a change reproducing it is dropped, the
    /// anchor itself is never. Pitch units ONLY; FNUM-resolution differences (≈0.87c)
    /// are deliberately NOT deduped here — the deadband owns them.
    /// </summary>
    private static List<PitchSample> Pass1ExactDedup(
        double initialMidiNote, IReadOnlyList<PitchSample> samples, double gridCents)
    {
        var result = new List<PitchSample>(samples.Count);
        long lastQuantized = Quantize(initialMidiNote, gridCents);
        foreach (PitchSample s in samples)
        {
            long q = Quantize(s.MidiNote, gridCents);
            if (q == lastQuantized)
                continue; // exact-quantized duplicate of the previous retained state
            result.Add(s);
            lastQuantized = q;
        }
        return result;
    }

    /// <summary>Quantized grid index of a continuous-semitone pitch on a cents grid:
    /// count of grid steps from pitch 0 (round-half-away so grid cells are exact).</summary>
    private static long Quantize(double midiNote, double gridCents) =>
        (long)Math.Round(midiNote * 1200.0 / gridCents, MidpointRounding.AwayFromZero);
}
