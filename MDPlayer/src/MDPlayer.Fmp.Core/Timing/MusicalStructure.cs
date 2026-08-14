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

/// <summary>A labeled structural span: bars [StartBar, EndBar).</summary>
internal sealed record MusicalSection(int StartBar, int EndBar, string Label);

/// <summary>
/// A repeated block: bars [StartBar, StartBar+LengthBars) are (near-)identical to the
/// immediately following equal-length span, i.e. a loop of <see cref="LengthBars"/> bars.
/// Similarity and coverage are retained so source-supported loops can outrank a
/// content-only coincidence.
/// </summary>
internal sealed record RepeatedBlock(
    int StartBar,
    int LengthBars,
    double Similarity = 0,
    double Coverage = 0,
    bool SourceSupported = false);
/// <summary>
/// Raw source-loop evidence. An entry is optional because many drivers expose only
/// restart positions; restart samples are preserved independently from inferred
/// musical loop boundaries.
/// </summary>
internal sealed record SourceLoopEvidence(
    long? EntrySample,
    IReadOnlyList<long> RestartSamples);

/// <summary>Coarse structural analysis of a decoded timeline against a time map.</summary>
internal sealed class MusicalStructure
{
    public static MusicalStructure Empty { get; } = new()
    {
        Bars = Array.Empty<BarFeature>(),
        Sections = Array.Empty<MusicalSection>(),
        Loops = Array.Empty<RepeatedBlock>(),
        PrimaryLoop = null,
    };

    public required IReadOnlyList<BarFeature> Bars { get; init; }

    public required IReadOnlyList<MusicalSection> Sections { get; init; }

    /// <summary>Detected repeated blocks, ordered with the strongest/longest validated period first.</summary>
    public required IReadOnlyList<RepeatedBlock> Loops { get; init; }

    /// <summary>The primary (largest validated) repeated cycle, or null when none detected.</summary>
    public RepeatedBlock? PrimaryLoop { get; init; }

    public bool HasSections => Sections.Count > 1;

    public bool HasLoop => PrimaryLoop is not null;
}