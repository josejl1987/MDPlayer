#nullable enable

using System;
using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Per-bar feature signature used for phrase similarity and repeated-block (loop)
/// detection. Contributions are duration-weighted over every note/pitch segment that
/// overlaps the bar; attacks, rhythm onsets, and physical source domains remain
/// separate from instrument/display identity.
/// </summary>
internal sealed class BarFeature
{
    public BarFeature(int barIndex, double quarterStart, double quarterEnd)
    {
        BarIndex = barIndex;
        QuarterStart = quarterStart;
        QuarterEnd = quarterEnd;
        PitchClassHistogram = new double[12];
    }

    public int BarIndex { get; }
    public double QuarterStart { get; }
    public double QuarterEnd { get; }
    public double[] PitchClassHistogram { get; }
    public ushort OnsetMask { get; private set; }
    public ulong ActiveVoiceMask { get; private set; }
    public int NoteOnCount { get; private set; }
    public int PitchSegmentCount { get; private set; }
    public int RhythmOnsetCount { get; private set; }
    public double ActiveDuration { get; private set; }
    public double Norm { get; private set; }
    /// <summary>True only when the bar has no attacks, sustained pitch activity, or rhythm activity.</summary>
    public bool IsRest =>
        NoteOnCount == 0
        && RhythmOnsetCount == 0
        && PitchSegmentCount == 0
        && ActiveVoiceMask == 0;

    internal void AddNoteOn(int pitchClass) =>
        AddNoteOn(pitchClass, 1.0, 0, null, null);

    internal void AddNoteOn(
        int pitchClass,
        double weight,
        int onsetSlot,
        string? voiceId,
        SourceDomainKey? domain = null)
        => AddNoteSegment(pitchClass, weight, onsetSlot, voiceId, domain, isAttack: true);

    /// <summary>
    /// Adds one duration-bearing pitch segment. Only the first segment of a note
    /// should set <paramref name="isAttack"/>; continuation segments contribute
    /// pitch/duration and physical activity without fabricating extra attacks.
    /// </summary>
    internal void AddNoteSegment(
        int pitchClass,
        double overlap,
        int onsetSlot,
        string? voiceId,
        SourceDomainKey? domain,
        bool isAttack)
    {
        if (!double.IsFinite(overlap) || overlap <= 0)
            return;

        int normalizedPitchClass = pitchClass % 12;
        if (normalizedPitchClass < 0)
            normalizedPitchClass += 12;

        PitchSegmentCount++;
        ActiveDuration += overlap;
        PitchClassHistogram[normalizedPitchClass] += Math.Max(0.001, overlap);
        if (isAttack)
        {
            NoteOnCount++;
            OnsetMask |= (ushort)(1 << Math.Clamp(onsetSlot, 0, 15));
        }

        AddPhysicalActivity(voiceId, domain);
    }

    private void AddPhysicalActivity(string? voiceId, SourceDomainKey? domain)
    {
        string? physicalVoice = domain?.ToString() ?? CanonicalPhysicalVoiceId(voiceId);
        if (!string.IsNullOrEmpty(physicalVoice))
            ActiveVoiceMask |= 1UL << (int)(StableHash(physicalVoice) & 63);
    }


    private static string? CanonicalPhysicalVoiceId(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return null;

        string value = voiceId.Trim().ToLowerInvariant();
        int marker = value.LastIndexOf(".fm.", StringComparison.Ordinal);
        int channelStart = marker >= 0 ? marker + 4 : -1;
        if (channelStart < 0)
        {
            for (int index = 0; index + 3 < value.Length; index++)
            {
                if (value[index] != '.' || value[index + 1] != 'f' || value[index + 2] != 'm'
                    || !char.IsDigit(value[index + 3]))
                    continue;
                channelStart = index + 3;
                break;
            }
        }

        if (channelStart < 0 || channelStart >= value.Length || !char.IsDigit(value[channelStart]))
            return value;

        int channelEnd = channelStart;
        while (channelEnd < value.Length && char.IsDigit(value[channelEnd]))
            channelEnd++;
        if (channelEnd == value.Length)
            return value;

        char suffix = value[channelEnd];
        return suffix is '.' or ':' or '/' or '\\' or '-' or '_' or '#'
            ? value[..channelEnd]
            : value;
    }

    private static ulong StableHash(string value)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            foreach (char c in value)
                hash = (hash ^ c) * 1099511628211UL;
            return hash;
        }
    }

    internal void AddRhythmOnset(
        int onsetSlot,
        string? voiceId = null,
        SourceDomainKey? domain = null)
    {
        RhythmOnsetCount++;
        OnsetMask |= (ushort)(1 << Math.Clamp(onsetSlot, 0, 15));
        AddPhysicalActivity(voiceId, domain);
    }

    internal void FinalizeFeatures()
    {
        double sumSq = 0;
        for (int i = 0; i < PitchClassHistogram.Length; i++)
            sumSq += PitchClassHistogram[i] * PitchClassHistogram[i];
        Norm = Math.Sqrt(sumSq);
    }

    public double Similarity(BarFeature other)
    {
        if (IsRest || other.IsRest)
            return IsRest == other.IsRest ? 1.0 : 0.0;
        double dot = 0;
        for (int index = 0; index < PitchClassHistogram.Length; index++)
            dot += PitchClassHistogram[index] * other.PitchClassHistogram[index];
        double pitch = Norm > 0 && other.Norm > 0
            ? Math.Clamp(dot / (Norm * other.Norm), 0.0, 1.0)
            : 1.0;
        int onsetBits = System.Numerics.BitOperations.PopCount((uint)(OnsetMask ^ other.OnsetMask));
        double rhythm = 1.0 - onsetBits / 16.0;
        ulong union = ActiveVoiceMask | other.ActiveVoiceMask;
        ulong intersection = ActiveVoiceMask & other.ActiveVoiceMask;
        double voices = union == 0
            ? 1.0
            : (double)System.Numerics.BitOperations.PopCount(intersection)
                / System.Numerics.BitOperations.PopCount(union);
        double firstSpan = Math.Max(0.001, QuarterEnd - QuarterStart);
        double secondSpan = Math.Max(0.001, other.QuarterEnd - other.QuarterStart);
        double firstDuration = Math.Clamp(ActiveDuration / firstSpan, 0.0, 1.0);
        double secondDuration = Math.Clamp(other.ActiveDuration / secondSpan, 0.0, 1.0);
        double duration = 1.0 - Math.Abs(firstDuration - secondDuration);
        return pitch * 0.55 + rhythm * 0.20 + voices * 0.15 + duration * 0.10;
    }
}


/// <summary>Ordered phrase signature compared in corresponding-bar order.</summary>
internal sealed class PhraseFeature
{
    public PhraseFeature(int startBar, IReadOnlyList<BarFeature> bars)
    {
        StartBar = startBar;
        Bars = bars;
    }

    public int StartBar { get; }
    public IReadOnlyList<BarFeature> Bars { get; }
    public int LengthBars => Bars.Count;

    public double Similarity(PhraseFeature other)
    {
        if (LengthBars != other.LengthBars || LengthBars == 0)
            return 0;
        double total = 0;
        for (int index = 0; index < LengthBars; index++)
            total += Bars[index].Similarity(other.Bars[index]);
        return total / LengthBars;
    }
}

/// <summary>
/// A labeled phrase span: bars [StartBar, EndBar). <see cref="Confidence"/> is the
/// corresponding-bar similarity that earned the label (1.0 for the first
/// occurrence of a label, the match similarity for repeats, 1.0 for TURNAROUND
/// which is deterministic by construction).
/// </summary>
internal sealed record MusicalPhrase(int StartBar, int EndBar, string Label, double Confidence);

/// <summary>
/// A labeled structural span: bars [StartBar, EndBar). Sections are formed by
/// merging adjacent phrases that share a label (target 8 bars); <see cref="Label"/>
/// carries the bare section name (e.g. "A"), <see cref="Confidence"/> the average
/// confidence of the merged phrases. Turnarounds are phrase-level only and never
/// form sections.
/// </summary>
internal sealed record MusicalSection(int StartBar, int EndBar, string Label, double Confidence);

/// <summary>
/// A repeated block: bars [StartBar, StartBar+LengthBars) repeat, starting with the
/// immediately following equal-length span, with <see cref="RepeatCount"/> total
/// occurrences validated in order (block1-block2, then block1-block3, ... until the
/// first failing pair). <see cref="SpanCoverage"/> is RepeatCount*LengthBars over the
/// total bar count; <see cref="MaterialCoverage"/> is the share of bars carrying
/// material across the compared span. Loops rejected by the spec gates
/// (RepeatCount&lt;2, Similarity&lt;0.85, MaterialCoverage&lt;0.40,
/// SpanCoverage&lt;0.25) are never emitted. <see cref="ContentValidated"/> distinguishes
/// blocks verified against actual bar content from source boundary-only blocks whose
/// second pass lies outside the capture (those carry Similarity 0 and never fabricate
/// coverage). <see cref="BoundaryErrorBars"/> carries the worst source-boundary error
/// for source-supported blocks (0 for content-only detection).
/// </summary>
internal sealed record RepeatedBlock(
    int StartBar,
    int LengthBars,
    int RepeatCount = 0,
    double Similarity = 0,
    double SpanCoverage = 0,
    double MaterialCoverage = 0,
    bool SourceSupported = false,
    bool ContentValidated = true,
    double BoundaryErrorBars = 0);
/// <summary>
/// Raw source-loop evidence. An entry is optional because many drivers expose only
/// restart positions; restart samples are preserved independently from inferred
/// musical loop boundaries.
/// </summary>
internal sealed record SourceLoopEvidence(
    long? EntrySample,
    IReadOnlyList<long> RestartSamples);

/// <summary>
/// Best single-restart loop inference for one grid: a restart marker whose
/// surrounding content validates an inferred loop period on both sides. Only
/// produced when the restart lands within <c>0.125</c> bars of a bar boundary and
/// content similarity/material gates pass — a raw restart alone never establishes
/// a loop. <see cref="SpanCoverage"/> is 1.0 because periods whose spans are not
/// fully inside the capture are rejected (never fabricated).
/// <see cref="BoundaryErrorBars"/> is |restartBarExact - chosenBar| for the marker
/// that produced this evidence (Patch 8A audit visibility).
/// </summary>
internal sealed record RestartBoundaryEvidence(
    long RestartSample,
    int RestartBarBoundary,
    int PeriodBars,
    int InferredStartBar,
    double BoundaryFit,
    double Similarity,
    double SpanCoverage,
    double MaterialCoverage,
    double BoundaryErrorBars = 0);

/// <summary>
/// One restart marker's bar-grid snapping record (Patch 8A audit): the exact bar
/// position of the restart under the candidate's downbeat,
/// <c>(quarter(restart) - firstDownbeatQuarter) / quartersPerBar</c>, the integer bar
/// chosen (AwayFromZero rounding), the |exact - chosen| error, and whether the marker
/// was dropped from restart-boundary evidence (error &gt; 0.125 bars, or no
/// downbeat/meter grid). Every restart is recorded — none are silently discarded.
/// </summary>
internal sealed record RestartSnapRecord(
    long RestartSample,
    double RestartBarExact,
    int ChosenBar,
    double ErrorBars,
    bool Dropped);

/// <summary>
/// Structural-evidence audit for one grid candidate (Patch 8A): how the candidate's
/// bar grid was built (bar origin = candidate downbeat, barCount =
/// Ceil((lastQuarter - downbeat) / quartersPerBar)), every restart's bar snapping,
/// and the repeated-block search outcome over those bars. Reported per candidate by
/// the corpus harness so the extraction chain (candidate grid → bar construction →
/// restart snapping → repeated-block search) is visible end to end.
/// </summary>
internal sealed record CandidateStructuralEvidence(
    bool HasMeter,
    bool HasDownbeat,
    double BarOrigin,
    int BarCount,
    double QuartersPerBar,
    double LastQuarter,
    bool RangeCovered,
    IReadOnlyList<RestartSnapRecord> RestartSnaps,
    int EvaluatedMaxPeriod,
    bool Period33Evaluated,
    bool Start0Evaluated,
    int CompleteSpansForPeriod33,
    RepeatedBlock? BestRepeatedBlock,
    RestartBoundaryEvidence? RestartEvidence);

/// <summary>
/// Outcome of structural grid selection (Patch 8B): tempo, meter, and downbeat
/// carry separate margins and separate resolution flags. Tempo may resolve on
/// its own evidence path while the meter stays unresolved (and vice versa);
/// <see cref="DownbeatResolved"/> is decided independently from the phase margin
/// and restart-boundary evidence — a map may carry a resolved tempo and meter
/// with an unresolved downbeat.
/// </summary>
internal sealed record GridResolution(
    MusicalGridCandidate Winner,
    double WinnerScore,
    double TempoMargin,
    double MeterMargin,
    double DownbeatMargin,
    bool TempoResolved,
    bool MeterResolved,
    bool DownbeatResolved,
    GridScoreBreakdown Breakdown);

/// <summary>Coarse structural analysis of a decoded timeline against a time map.</summary>
internal sealed class MusicalStructure
{
    public static MusicalStructure Empty { get; } = new()
    {
        Bars = Array.Empty<BarFeature>(),
        Phrases = Array.Empty<MusicalPhrase>(),
        Sections = Array.Empty<MusicalSection>(),
        Loops = Array.Empty<RepeatedBlock>(),
        PrimaryLoop = null,
        Pickup = null,
    };

    public required IReadOnlyList<BarFeature> Bars { get; init; }

    /// <summary>Detected phrases: 4-bar units that never cross a validated loop
    /// boundary; a 1-bar remainder becomes TURNAROUND, a 2-3 bar remainder is
    /// attached to the preceding phrase. Empty for rest-only material.</summary>
    public required IReadOnlyList<MusicalPhrase> Phrases { get; init; }

    public required IReadOnlyList<MusicalSection> Sections { get; init; }

    /// <summary>Detected repeated blocks, ordered with the strongest/longest validated period first.</summary>
    public required IReadOnlyList<RepeatedBlock> Loops { get; init; }

    /// <summary>The primary (largest validated) repeated cycle, or null when none detected.</summary>
    public RepeatedBlock? PrimaryLoop { get; init; }

    /// <summary>The partial leading bar before the first downbeat, when the downbeat
    /// lands after the source start (spec P0-8). Its span is [FirstDownbeatQuarter -
    /// QuartersPerBar, FirstDownbeatQuarter); pre-downbeat events live here and never
    /// in the bar grid or loop counting. Null when no pre-downbeat material exists.</summary>
    public BarFeature? Pickup { get; init; }

    public bool HasSections => Sections.Count > 1;

    public bool HasLoop => PrimaryLoop is not null;
}