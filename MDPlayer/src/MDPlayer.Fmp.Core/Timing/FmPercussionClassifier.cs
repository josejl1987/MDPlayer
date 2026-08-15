#nullable enable

using Fmp.Core.PlaybackAssets.Opn;
using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Conservative FM note → percussion classifier (spec §5, D4). "Is percussive"
/// and "which drum" are independent questions: this classifier decides whether
/// an FM note behaves like a percussion attack from multi-signal evidence
/// (synthesis mode, retrigger/repetition, gate duration, instrument envelope),
/// and delegates the role label to the shared <see cref="RhythmRoleClassifier"/>
/// vocabulary. It NEVER classifies from "fm" naming, channel number, low MIDI
/// pitch, or short duration alone.
/// </summary>
internal static class FmPercussionClassifier
{
    /// <summary>OPN register attack rate at or above which the attack is "fast".</summary>
    private const int FastAttackRate = 28;

    /// <summary>OPN register sustain level at or below which the note decays.</summary>
    private const int LowSustainLevel = 4;

    /// <summary>OPN register decay/release rate at or above which the note drops fast.</summary>
    private const int FastDecayRate = 24;

    /// <summary>Gate below which a note is treated as "short" (milliseconds).</summary>
    private const int ShortGateMilliseconds = 150;

    /// <summary>Envelope score needed (0..1) for any percussive conclusion.</summary>
    private const double MinPercussiveEnvelope = 0.7;

    /// <summary>
    /// Confidence ceiling when a percussive note has no drum identity. A
    /// confidently percussive but unidentifiable onset is first-class evidence,
    /// but it must never look like a known-role classification.
    /// </summary>
    private const double UnknownRoleConfidenceCap = 0.92;

    /// <summary>
    /// Classifies one FM note. <paramref name="sampleRate"/> converts the note
    /// gate to milliseconds (0 = unknown; the gate signal is then suppressed,
    /// it is never decisive alone).
    /// </summary>
    public static PercussionClassification Classify(
        NoteEvent note,
        InstrumentDefinition? instrument,
        int sampleRate = 0)
    {
        if (note is null)
            return default;

        // Only FM synthesis is in scope; other modes are never classified here.
        if (note.Mode is not (VisualizationNoteMode.Fm or VisualizationNoteMode.Fm3Operator))
            return new PercussionClassification(false, RhythmRole.Unknown, 0.0);

        double envelope = EnvelopePercussiveness(instrument);
        bool repeated = note.IsRetrigger;
        bool shortGate = IsShortGate(note, sampleRate);
        bool pitchDrop = HasPercussivePitchContour(note);

        // Instrument absent (or envelope-less): conservative signature fallback.
        // Only repeated SHORT attacks classify — weakly enough to stay under
        // the drum-remap gate — and never with a drum role.
        if (instrument is null || instrument.Operators.Count == 0)
        {
            if (!repeated || !shortGate)
                return new PercussionClassification(false, RhythmRole.Unknown, 0.0);
            return new PercussionClassification(true, RhythmRole.Unknown, 0.70);
        }

        if (envelope < MinPercussiveEnvelope || !(repeated || shortGate || pitchDrop))
        {
            // Sustained or single-shot notes are not percussion, whatever their
            // pitch, name, or channel says.
            return new PercussionClassification(false, RhythmRole.Unknown, 0.0);
        }

        // Role comes from the shared rhythm-role vocabulary only; the
        // is-percussive decision above is behavioural and independent of it.
        RhythmRole role = RhythmRoleClassifier.Classify(note);

        double confidence =
            0.5 + 0.28 * envelope + 0.12 * (repeated ? 1.0 : 0.0)
            + 0.08 * (shortGate ? 1.0 : 0.0) + 0.02 * (pitchDrop ? 1.0 : 0.0);
        if (role == RhythmRole.Unknown)
            confidence = Math.Min(confidence, UnknownRoleConfidenceCap);
        confidence = Math.Clamp(confidence, 0.0, 1.0);

        return new PercussionClassification(true, role, confidence);
    }

    /// <summary>
    /// Percussiveness of the instrument envelope, scored from the carrier
    /// operators' attack/decay/release registers (carriers shape the audible
    /// envelope). OPN carrier sets come from the existing <c>OpnFmAlgorithm</c>
    /// helper — no OPN table duplication. Instruments without OPN-shaped data
    /// are scored from all operators.
    /// </summary>
    private static double EnvelopePercussiveness(InstrumentDefinition? instrument)
    {
        if (instrument is null || instrument.Operators.Count == 0)
            return 0.0;

        IReadOnlyList<FmOperatorDefinition> ops = instrument.Operators;
        bool hasOpoCarrierMap =
            ops.Count == 4 && instrument.Algorithm is >= 0 and <= 7;
        if (hasOpoCarrierMap && HasCarrier(ops.Count, instrument.Algorithm!.Value))
        {
            byte carrierMask =
                OpnFmAlgorithm.GetCarrierMask((byte)instrument.Algorithm.Value);
            var carriers = new List<FmOperatorDefinition>(4);
            for (int op = 0; op < ops.Count; op++)
            {
                if ((carrierMask & (1 << op)) != 0)
                    carriers.Add(ops[op]);
            }
            ops = carriers;
        }

        double attackRate = Median(ops, static op => op.AttackRate);
        double sustainLevel = Median(ops, static op => op.SustainLevel);
        double decayRate = Median(ops, static op => op.DecayRate);
        double releaseRate = Median(ops, static op => op.ReleaseRate);

        double score = 0.0;
        if (attackRate >= FastAttackRate)
            score += 0.35;
        if (sustainLevel <= LowSustainLevel && decayRate >= FastDecayRate)
            score += 0.35;
        if (releaseRate >= FastDecayRate)
            score += 0.30;
        return score;
    }

    private static bool HasCarrier(int opCount, int algorithm) =>
        (OpnFmAlgorithm.GetCarrierMask((byte)algorithm) & ((1 << opCount) - 1)) != 0;

    private static bool IsShortGate(NoteEvent note, int sampleRate)
    {
        long gate = Math.Max(0, note.EndSample - note.StartSample);
        if (gate <= 0 || sampleRate <= 0)
            return false;
        return gate * 1000.0 / sampleRate <= ShortGateMilliseconds;
    }

    /// <summary>
    /// A rapid downward pitch sweep (>= 4 semitones) is a supporting drum-like
    /// contour signal; never decisive on its own.
    /// </summary>
    private static bool HasPercussivePitchContour(NoteEvent note)
    {
        if (note.Pitch is null || note.Pitch.Count == 0)
            return false;
        double previous = note.InitialMidiNote;
        foreach (PitchChange change in note.Pitch)
        {
            if (previous - change.MidiNote >= 4.0)
                return true;
            previous = change.MidiNote;
        }
        return false;
    }

    private static double Median(IReadOnlyList<FmOperatorDefinition> ops, Func<FmOperatorDefinition, int> selector)
    {
        var values = new List<double>(ops.Count);
        foreach (FmOperatorDefinition op in ops)
            values.Add(selector(op));
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }
}

/// <summary>Result of <see cref="FmPercussionClassifier.Classify"/>: whether the
/// note is percussive, which drum role (if identifiable), and confidence in the
/// percussive conclusion.</summary>
internal readonly record struct PercussionClassification(
    bool IsPercussive,
    RhythmRole Role,
    double Confidence);