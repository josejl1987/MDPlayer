#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>Pitch-normalization modes (D9/D11).</summary>
internal enum PitchNormalizationMode
{
    /// <summary>DEFAULT: subtract the accepted per-domain bias and restore it via
    /// RPN 0x0002 (+0x0001 coarse) channel tuning at tick 0 — played pitch equals
    /// source pitch, bends carry only expressive deviation.</summary>
    Fidelity,

    /// <summary>OPT-IN: subtract an accepted SMALL bias (≤ DawFriendlySnapMaxCents)
    /// and do NOT restore it — notes snap to equal temperament. Emits no tuning
    /// events. A larger accepted bias is left raw with a warning.</summary>
    DawFriendly,

    /// <summary>Byte-identical legacy output: no normalization at all, no tuning,
    /// no per-domain statistics.</summary>
    Off,
}

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

    /// <summary>Pass 3 per-chip cap: SNES DSP is typically source-pitched, so a bias
    /// beyond this is rejected (Smash Up cannot emit an audibly wrong tuning).</summary>
    public double SnesDspBiasCapCents { get; init; } = 15.0;

    /// <summary>DAW-friendly mode: bias is snapped to equal temperament only when
    /// |bias| is at most this; a larger bias emits no tuning and is left raw.</summary>
    public double DawFriendlySnapMaxCents { get; init; } = 50.0;

    /// <summary>Pass 3 acceptance: |bias| beyond this is rejected outright (would
    /// require a tuning state no MIDI channel should carry).</summary>
    public double CoarseBiasCapCents { get; init; } = 600.0;

    /// <summary>
    /// Pass 3 acceptance floor (first-run calibration, FR-6): a tuning center within
    /// this many cents of equal temperament is NOT acted on. A sub-threshold bias is
    /// indistinguishable from register noise (the deadband noise floor), and acting
    /// on it would turn every exactly-integer note in the domain into a sub-LSB
    /// noise bend. Rejected silently — "in tune" is high confidence, not low, so no
    /// warning is emitted; the measured mode/MAD/coverage still reach the report.
    /// </summary>
    public double MinAcceptedBiasCents { get; init; } = 0.75;
}

/// <summary>A normalized pitch change: same sample, pitch expressed in the normalized
/// domain (raw source pitch minus the domain's accepted tuning bias).</summary>
internal readonly record struct NormalizedPitchChange(long SamplePosition, double MidiNote);

/// <summary>
/// Normalized pitch view of ONE source note. Consumers (ShouldFoldPitch,
/// ComputeOriginShiftTicks, EmitNote/BuildPitchAnchors) read pitch exclusively
/// through this view so the origin shift and the emitted bends always cover the
/// SAME normalized event set (FR-1 coupling — ticks never drift from the set the
/// exporter actually serializes).
/// </summary>
internal readonly record struct NormalizedNoteView(
    NoteEvent Source,
    double InitialMidiNote,
    IReadOnlyList<NormalizedPitchChange>? Changes);

/// <summary>
/// The normalized pitch model: one view per source note plus per-domain tuning
/// statistics, produced once by <see cref="PitchNormalizationStage.Normalize"/> and
/// consumed by every pitch consumer of the exporter. Reference-identity keyed by
/// <see cref="NoteEvent"/>; domains keyed by <see cref="MidiTrackKey"/>.
/// </summary>
internal sealed record PitchNormalizationModel(
    IReadOnlyDictionary<NoteEvent, NormalizedNoteView> Views,
    IReadOnlyDictionary<MidiTrackKey, DomainPitchStats> Domains);

/// <summary>
/// Per-domain tuning statistics for the pitch report (FR-6 / D12). Cents values are
/// TRUE cents (1 midi unit = 1 semitone = 100c). Attacks and RetriggerAttacks are
/// the stable-note pool fed to the detector (retriggers counted separately, D7);
/// RawPitchSamples is the raw decoder write count; RawBendTransitions … 
/// ExpressiveTransitions are the monotone pipeline counts.
/// </summary>
internal sealed record DomainPitchStats(
    MidiTrackKey Key,
    int Attacks,
    int RetriggerAttacks,
    int RawPitchSamples,
    double? ResidualModeCents,
    double? StableResidualMadCents,
    double? BaselineConfidence,
    int RawBendTransitions,
    int AfterDedup,
    int AfterDeadband,
    int ExpressiveTransitions,
    double? TuningCents,
    bool Accepted);

/// <summary>Result of the per-domain tuning-center detector (D7/D8).</summary>
internal sealed record TuningDetection(
    double BiasCents,
    bool Accepted,
    int Attacks,
    int RetriggerAttacks,
    double? ResidualModeCents,
    double? StableResidualMadCents,
    double? BaselineConfidence);

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
///   P2  deadband + hysteresis stable-run compression (FR-3)
///   P3  consecutive-identical suppression (cheap final fold, D6)
///   P4  per-domain tuning-bias subtraction (FR-4)
///
/// The run-start anchor (the folded initial pitch) is state #0 of every pass: it is
/// never dropped, but it participates in dedup/compression comparisons so a change
/// that reproduces the initial state is recognized as noise.
/// </summary>
internal static class PitchNormalizationStage
{
    public static PitchNormalizationModel Normalize(
        VisualizationTimeline timeline,
        PitchNormalizationMode mode,
        PitchNormalizationThresholds thresholds,
        Func<NoteEvent, MidiTrackKey> keyFor,
        List<string>? warnings)
    {
        var notes = timeline.Notes ?? Array.Empty<NoteEvent>();

        if (mode == PitchNormalizationMode.Off)
        {
            // Legacy passthrough: identity views (raw pitch, raw changes), no domain
            // statistics — byte-identical to the pre-normalization exporter.
            var identity = new Dictionary<NoteEvent, NormalizedNoteView>(notes.Count);
            foreach (NoteEvent note in notes)
                identity[note] = new NormalizedNoteView(note, note.InitialMidiNote, RawChanges(note.Pitch));
            return new PitchNormalizationModel(identity, new Dictionary<MidiTrackKey, DomainPitchStats>());
        }

        var notesByDomain = new Dictionary<MidiTrackKey, List<NoteEvent>>();
        foreach (NoteEvent note in notes)
        {
            MidiTrackKey key = keyFor(note);
            if (!notesByDomain.TryGetValue(key, out List<NoteEvent>? list))
            {
                list = new List<NoteEvent>();
                notesByDomain[key] = list;
            }
            list.Add(note);
        }

        // Tuning-center detection FIRST (raw residuals, stable regions only), then
        // the per-note pipeline subtracts the accepted domain bias (P4).
        var detectionByDomain = new Dictionary<MidiTrackKey, TuningDetection>();
        foreach ((MidiTrackKey key, List<NoteEvent> domainNotes) in notesByDomain)
            detectionByDomain[key] = DetectTuningCenter(key, domainNotes, thresholds, warnings);

        var views = new Dictionary<NoteEvent, NormalizedNoteView>(notes.Count);
        var domains = new Dictionary<MidiTrackKey, DomainPitchStats>(notesByDomain.Count);
        // Every normalization pass is remove-only and runs serially. Reuse one
        // working buffer across notes instead of allocating a temporary list
        // for each note; the final normalized list is still owned by the view.
        var samplesScratch = new List<PitchSample>();
        foreach ((MidiTrackKey key, List<NoteEvent> domainNotes) in notesByDomain)
        {
            TuningDetection detection = detectionByDomain[key];
            // DAW-friendly snaps only small accepted biases (D9); a larger one is
            // left raw (bias 0) with a warning — snapping it would audibly shift the part.
            double bias;
            if (detection.Accepted
                && (mode != PitchNormalizationMode.DawFriendly
                    || Math.Abs(detection.BiasCents) <= thresholds.DawFriendlySnapMaxCents + 1e-9))
            {
                bias = detection.BiasCents / 100.0; // cents → semitones
            }
            else
            {
                if (detection.Accepted && mode == PitchNormalizationMode.DawFriendly && warnings is not null)
                    warnings.Add($"pitch tuning: domain '{key}' bias {detection.BiasCents:0.0}c exceeds the " +
                        $"DAW-friendly snap cap {thresholds.DawFriendlySnapMaxCents:0.0}c — left raw, no snap");
                bias = 0.0;
            }
            var counters = new DomainCounters();
            foreach (NoteEvent note in domainNotes)
            {
                counters.RawPitchSamples += note.Pitch?.Count ?? 0;
                var (changes, counts) = NormalizeChanges(
                    note, thresholds, bias, samplesScratch);
                counters.RawTransitions += counts.RawTransitions;
                counters.AfterDedup += counts.AfterDedup;
                counters.AfterDeadband += counts.AfterDeadband;
                counters.Expressive += changes?.Count ?? 0;
                views[note] = new NormalizedNoteView(
                    note,
                    note.InitialMidiNote - bias,
                    changes);
            }
            domains[key] = new DomainPitchStats(
                Key: key,
                Attacks: detection.Attacks,
                RetriggerAttacks: detection.RetriggerAttacks,
                RawPitchSamples: counters.RawPitchSamples,
                ResidualModeCents: detection.ResidualModeCents,
                StableResidualMadCents: detection.StableResidualMadCents,
                BaselineConfidence: detection.BaselineConfidence,
                RawBendTransitions: counters.RawTransitions,
                AfterDedup: counters.AfterDedup,
                AfterDeadband: counters.AfterDeadband,
                ExpressiveTransitions: counters.Expressive,
                TuningCents: detection.Accepted ? detection.BiasCents : null,
                Accepted: detection.Accepted);
        }
        return new PitchNormalizationModel(views, domains);
    }

    /// <summary>Off-mode identity changes: the raw list converted 1:1 (no collapse,
    /// no dedup), preserving legacy byte-for-byte semantics.</summary>
    private static IReadOnlyList<NormalizedPitchChange>? RawChanges(IReadOnlyList<PitchChange>? raw)
    {
        if (raw is null)
            return null;

        var changes = new List<NormalizedPitchChange>(raw.Count);
        for (int index = 0; index < raw.Count; index++)
        {
            PitchChange change = raw[index];
            changes.Add(new NormalizedPitchChange(change.SamplePosition, change.MidiNote));
        }
        return changes;
    }

    /// <summary>Per-domain pipeline counters (monotone: never grows).</summary>
    private sealed class DomainCounters
    {
        public int RawPitchSamples;
        public int RawTransitions;
        public int AfterDedup;
        public int AfterDeadband;
        public int Expressive;
    }

    /// <summary>Per-note intermediate pass counts (RawTransitions is post-P0).</summary>
    private readonly record struct PassCounts(
        int RawTransitions,
        int AfterDedup,
        int AfterDeadband);

    private static (IReadOnlyList<NormalizedPitchChange>? Changes, PassCounts Counts) NormalizeChanges(
        NoteEvent note,
        PitchNormalizationThresholds thresholds,
        double biasSemitones,
        List<PitchSample> samples)
    {
        if (note.Pitch is null)
            return (null, new PassCounts());
        if (note.Pitch.Count == 0)
            return (Array.Empty<NormalizedPitchChange>(), new PassCounts());
        // All four remove-only passes share one working list. The previous
        // implementation allocated a replacement list for every pass, even
        // though each pass only removes entries and preserves source order.
        samples.Clear();
        if (samples.Capacity < note.Pitch.Count)
            samples.Capacity = note.Pitch.Count;
        SameSampleCollapseInPlace(samples, note.Pitch);
        int rawTransitions = samples.Count;
        Pass1ExactDedupInPlace(note.InitialMidiNote, samples, thresholds.DedupGridCents);
        int afterDedup = samples.Count;
        Pass2DeadbandHysteresisInPlace(note.InitialMidiNote, samples,
            thresholds.DeadbandEnterCents, thresholds.DeadbandExitCents);
        int afterDeadband = samples.Count;
        Pass3ConsecutiveIdenticalInPlace(samples);
        // P4: affine bias subtraction — normalized = raw − domainBias (D9 model:
        // sourcePitch(t) = nominalNote + bias + expressive(t)).
        var changes = new List<NormalizedPitchChange>(samples.Count);
        for (int index = 0; index < samples.Count; index++)
        {
            PitchSample sample = samples[index];
            changes.Add(new NormalizedPitchChange(sample.SamplePosition, sample.MidiNote - biasSemitones));
        }
        return (changes,
            new PassCounts(rawTransitions, afterDedup, afterDeadband));
    }

    /// <summary>Working pitch sample (decoder order is chronological, so list order
    /// is source order). Internal so the passes are independently testable.</summary>
    internal readonly record struct PitchSample(long SamplePosition, double MidiNote);

    private static void SameSampleCollapseInPlace(
        List<PitchSample> collapsed, IReadOnlyList<PitchChange> raw)
    {
        for (int index = 0; index < raw.Count; index++)
        {
            PitchChange change = raw[index];
            if (collapsed.Count > 0 && collapsed[^1].SamplePosition == change.SamplePosition)
                collapsed[^1] = new PitchSample(change.SamplePosition, change.MidiNote);
            else
                collapsed.Add(new PitchSample(change.SamplePosition, change.MidiNote));
        }
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
    private static void Pass1ExactDedupInPlace(
        double initialMidiNote, List<PitchSample> samples, double gridCents)
    {
        long lastQuantized = Quantize(initialMidiNote, gridCents);
        int write = 0;
        for (int read = 0; read < samples.Count; read++)
        {
            PitchSample sample = samples[read];
            long quantized = Quantize(sample.MidiNote, gridCents);
            if (quantized == lastQuantized)
                continue; // exact-quantized duplicate of the previous retained state
            samples[write++] = sample;
            lastQuantized = quantized;
        }
        if (write < samples.Count)
            samples.RemoveRange(write, samples.Count - write);
    }

    /// <summary>Quantized grid index of a continuous-semitone pitch on a cents grid:
    /// count of grid steps from pitch 0 (round-half-away so grid cells are exact).</summary>
    private static long Quantize(double midiNote, double gridCents) =>
        (long)Math.Round(midiNote * 1200.0 / gridCents, MidpointRounding.AwayFromZero);

    /// <summary>
    /// P2 deadband + MANDATORY hysteresis (FR-3 / SC-3). The FNUM noise floor
    /// (~0.87c at fNumber ≈ 2000) sits at the deadband threshold, so a single
    /// threshold would oscillate between retain/drop forever around the boundary;
    /// hysteresis re-centers the band on the first retained value and the other
    /// boundary value lands inside the new band → ONE stable state. The band is
    /// always relative to the last RETAINED value (center); the run-start anchor
    /// (the folded initial pitch) is the initial center.
    ///
    /// State machine (D5; Enter must stay &gt; Exit so a boundary value never ties):
    ///   Suppress: dev = |c − center| ≤ Enter → DROP (stay Suppress)
    ///             dev &gt; Enter            → RETAIN, center = c, mode = Pass
    ///   Pass:     dev ≤ Exit             → RETAIN as new center, mode = Suppress
    ///             dev &gt; Exit            → RETAIN, center = c, stay Pass
    ///
    /// Contract: worst-case suppressed drift is bounded by EnterCents (a dropped
    /// change is always within Enter of the last retained center).
    /// </summary>
    internal static List<PitchSample> Pass2DeadbandHysteresis(
        double initialMidiNote, IReadOnlyList<PitchSample> samples,
        double enterCents, double exitCents)
    {
        var result = new List<PitchSample>(samples.Count);
        for (int index = 0; index < samples.Count; index++)
            result.Add(samples[index]);
        Pass2DeadbandHysteresisInPlace(initialMidiNote, result, enterCents, exitCents);
        return result;
    }

    private static void Pass2DeadbandHysteresisInPlace(
        double initialMidiNote, List<PitchSample> samples,
        double enterCents, double exitCents)
    {
        // Float guard: cents computed as |Δ midi|·100 can land a hair above an exact
        // boundary (0.5c shows up as 0.5 + 1e-17); the bounds are INCLUSIVE by design
        // (D5: dev ≤ Enter drops, dev ≤ Exit retains), so compare against +ε.
        const double centsEpsilon = 1e-9;
        double center = initialMidiNote;
        bool pass = false;
        int write = 0;
        for (int read = 0; read < samples.Count; read++)
        {
            PitchSample sample = samples[read];
            double dev = Math.Abs(sample.MidiNote - center) * 100.0; // cents (1 midi unit = 1 semitone = 100c)
            if (!pass)
            {
                if (dev <= enterCents + centsEpsilon)
                    continue; // suppressed: within the deadband of the current center
                samples[write++] = sample;
                center = sample.MidiNote;
                pass = true;
            }
            else
            {
                samples[write++] = sample;
                center = sample.MidiNote;
                if (dev <= exitCents + centsEpsilon)
                    pass = false; // settled within Exit → re-center and re-suppress
            }
        }
        if (write < samples.Count)
            samples.RemoveRange(write, samples.Count - write);
    }

    /// <summary>
    /// P3 consecutive-identical suppression (D6): drop a retained state equal to the
    /// previous retained one. Belt-and-braces final fold — the passes before it make
    /// consecutive equal values unreachable in practice (P1 dedups equal grid cells,
    /// P2 suppresses zero-deviation writes in Suppress mode), but the fold guarantees
    /// the emitted change list is strictly alternating regardless of future pass
    /// changes.
    /// </summary>
    internal static List<PitchSample> Pass3ConsecutiveIdentical(IReadOnlyList<PitchSample> samples)
    {
        var result = new List<PitchSample>(samples.Count);
        for (int index = 0; index < samples.Count; index++)
            result.Add(samples[index]);
        Pass3ConsecutiveIdenticalInPlace(result);
        return result;
    }

    private static void Pass3ConsecutiveIdenticalInPlace(List<PitchSample> samples)
    {
        int write = 0;
        for (int read = 0; read < samples.Count; read++)
        {
            PitchSample sample = samples[read];
            if (write > 0 && samples[write - 1].MidiNote == sample.MidiNote)
                continue; // equal to the previous retained state
            samples[write++] = sample;
        }
        if (write < samples.Count)
            samples.RemoveRange(write, samples.Count - write);
    }

    /// <summary>
    /// Pass 3 tuning-center detector (FR-4 / D7-D8), per <see cref="MidiTrackKey"/>
    /// domain. Estimates the domain's tuning BIAS — never a bend value — from attack
    /// residuals (InitialMidiNote − round(InitialMidiNote), in cents) collected over
    /// STABLE note regions only. Portamento, vibrato, attack transients and
    /// intentional bends are excluded BY MAGNITUDE (the only signal the timeline
    /// model carries). Acceptance is deliberately conservative so real-decoder-only
    /// activation is structural: a 2–3 note synthetic fixture can never pass the
    /// distinct-note and persistence criteria, keeping it a byte-identical no-op.
    /// Rejected domains get bias 0 plus a Diagnostics warning.
    /// </summary>
    private static TuningDetection DetectTuningCenter(
        MidiTrackKey key, IReadOnlyList<NoteEvent> notes,
        PitchNormalizationThresholds t, List<string>? warnings)
    {
        // Float guard for boundary comparisons: residuals are |Δ midi|·100 so exact
        // thresholds (2c persistence, 60% coverage, MAD caps) can land a hair over
        // their bounds; the acceptance gates are inclusive by design (D8).
        const double centsEpsilon = 1e-9;
        var residuals = new List<(double Cents, int NoteNumber)>();
        int attacks = 0, retriggerAttacks = 0;
        foreach (NoteEvent note in notes)
        {
            if (!IsStableRegion(note, t))
                continue;
            if (note.IsRetrigger)
                retriggerAttacks++;
            else
                attacks++;
            // The residual is the deviation from equal temperament FOLDED into the
            // current semitone cell: InitialMidiNote − round(InitialMidiNote) is
            // always within ±0.5 st (±50c). The fold is deliberate and exact — the
            // RPN fine-tuning range (±100c) covers it, so fine-only restoration
            // always reproduces the source pitch. A coarse RPN 0x0001 engagement
            // threshold is therefore unreachable from the stage (the former
            // DomainBiasCapCents was removed for this reason); the coarse path lives
            // on only in the IR/writer (D10) for a hypothetical multi-semitone bias.
            residuals.Add(((note.InitialMidiNote - Math.Round(note.InitialMidiNote)) * 100.0,
                (int)Math.Round(note.InitialMidiNote)));
        }

        TuningDetection Reject(string reason)
        {
            if (warnings is not null)
                warnings.Add($"pitch tuning: domain '{key}' rejected ({reason}) — bias 0, no tuning applied");
            return new TuningDetection(0.0, false, attacks, retriggerAttacks, null, null, null);
        }

        if (residuals.Count < t.PersistenceMinAttacks)
            return Reject($"only {residuals.Count} stable attacks < floor {t.PersistenceMinAttacks}");

        // Greedy merge over sorted residuals while the cluster span stays within
        // ClusterSpanCents (±2c); the mode is the mean of the largest cluster.
        var sorted = residuals.OrderBy(r => r.Cents).ToList();
        int bestStart = 0, bestLen = 1;
        int start = 0;
        for (int i = 1; i <= sorted.Count; i++)
        {
            if (i == sorted.Count || sorted[i].Cents - sorted[start].Cents > t.ClusterSpanCents + centsEpsilon)
            {
                int len = i - start;
                if (len > bestLen)
                {
                    bestStart = start;
                    bestLen = len;
                }
                start = i;
            }
        }
        var cluster = sorted.Skip(bestStart).Take(bestLen).ToList();
        double mode = cluster.Average(r => r.Cents);

        // Acceptance — ALL must hold (D8).
        double coverage = (double)cluster.Count / residuals.Count;
        int distinctNotes = cluster.Select(r => r.NoteNumber).Distinct().Count();
        double mad = cluster.Average(r => Math.Abs(r.Cents - mode));
        int persistenceWindow = Math.Min(t.PersistenceAttacks, residuals.Count);
        bool persistent = residuals
            .Skip(residuals.Count - persistenceWindow)
            .All(r => Math.Abs(r.Cents - mode) <= t.ClusterSpanCents / 2.0 + centsEpsilon);

        if (coverage < t.CoverageMin - centsEpsilon)
            return Reject($"cluster coverage {coverage:0.00} < {t.CoverageMin:0.00}");
        if (distinctNotes < t.MinDistinctNoteNumbers)
            return Reject($"cluster spans only {distinctNotes} distinct notes < {t.MinDistinctNoteNumbers}");
        if (mad > t.MadMaxCents + centsEpsilon)
            return Reject($"cluster MAD {mad:0.00}c > {t.MadMaxCents:0.00}c");
        if (!persistent)
            return Reject("recent attacks leave the cluster (persistence)");
        // Caps (D8/D9): SNES DSP is typically source-pitched (15c cap); other chips
        // may reach the coarse-RPN path up to the coarse cap (600c).
        if (key.Device.Type == ChipType.SnesDsp)
        {
            if (Math.Abs(mode) > t.SnesDspBiasCapCents + centsEpsilon)
                return Reject($"SNES DSP |bias| {mode:0.0}c > {t.SnesDspBiasCapCents:0.0}c cap");
        }
        else if (Math.Abs(mode) > t.CoarseBiasCapCents + centsEpsilon)
        {
            return Reject($"|bias| {mode:0.0}c > coarse cap {t.CoarseBiasCapCents:0.0}c");
        }
        // Noise-floor gate (MinAcceptedBiasCents): a center within the deadband of
        // equal temperament is "in tune" — not acted on, silently (no warning).
        if (Math.Abs(mode) <= t.MinAcceptedBiasCents + centsEpsilon)
            return new TuningDetection(0.0, false, attacks, retriggerAttacks, mode, mad, coverage);

        return new TuningDetection(mode, true, attacks, retriggerAttacks, mode, mad, coverage);
    }

    /// <summary>
    /// Stable-region classification (D7): a note is a stable region iff
    /// (a) finite initial pitch in [0, 127] (excludes the −1 unpitched sentinel),
    /// (b) no pitch change deviates from the initial beyond StableDeviationMaxCents
    ///     and no consecutive pair moves beyond MaxInterChangeCents — portamento,
    ///     vibrato, attack transients and intentional bends are excluded by
    ///     magnitude; changes inside the FoldWindowSamples attack window are not
    ///     counted as instability (they fold into the initial pitch),
    /// (c) duration ≥ MinStableDurationSamples. Retriggers count as attacks and are
    ///     reported separately.
    /// </summary>
    private static bool IsStableRegion(NoteEvent note, PitchNormalizationThresholds t)
    {
        if (!double.IsFinite(note.InitialMidiNote) || note.InitialMidiNote is < 0 or > 127)
            return false;
        if (note.EndSample - note.StartSample < t.MinStableDurationSamples)
            return false;
        if (note.Pitch is null || note.Pitch.Count == 0)
            return true;
        double initial = note.InitialMidiNote;
        double? previous = null;
        foreach (PitchChange c in note.Pitch)
        {
            if (c.SamplePosition - note.StartSample < t.FoldWindowSamples)
            {
                previous = null; // attack transient: not counted, breaks the pair chain
                continue;
            }
            if (!double.IsFinite(c.MidiNote))
                return false;
            if (Math.Abs(c.MidiNote - initial) * 100.0 > t.StableDeviationMaxCents)
                return false;
            if (previous is double p && Math.Abs(c.MidiNote - p) * 100.0 > t.MaxInterChangeCents)
                return false;
            previous = c.MidiNote;
        }
        return true;
    }
}
