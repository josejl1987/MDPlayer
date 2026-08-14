#nullable enable

using System;

namespace Fmp.Core.Timing;

/// <summary>
/// Per-bar feature signature used for section similarity and repeated-block (loop)
/// detection. A bar is a fixed span of quarter notes derived from the meter; only the
/// pitch-class content and note-on count of note events participate, so the signature
/// is chip-independent.
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

    /// <summary>Absolute quarter position at the bar start (inclusive).</summary>
    public double QuarterStart { get; }

    /// <summary>Absolute quarter position at the bar end (exclusive).</summary>
    public double QuarterEnd { get; }

    /// <summary>Time-weighted pitch-class content for the bar.</summary>
    public double[] PitchClassHistogram { get; }

    public ushort OnsetMask { get; private set; }

    public ulong ActiveVoiceMask { get; private set; }

    /// <summary>Number of note-ons that landed in this bar.</summary>
    public int NoteOnCount { get; private set; }

    /// <summary>L2 norm of the histogram; 0 for a rest (empty) bar.</summary>
    public double Norm { get; private set; }

    public bool IsRest => NoteOnCount == 0;

    internal void AddNoteOn(int pitchClass) =>
        AddNoteOn(pitchClass, 1.0, 0, null);

    internal void AddNoteOn(int pitchClass, double weight, int onsetSlot, string? voiceId)
    {
        NoteOnCount++;
        PitchClassHistogram[pitchClass] += Math.Max(0.001, weight);
        OnsetMask |= (ushort)(1 << Math.Clamp(onsetSlot, 0, 15));
        if (!string.IsNullOrEmpty(voiceId))
            ActiveVoiceMask |= 1UL << (int)(StableHash(voiceId) & 63);
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

    internal void AddRhythmOnset(int onsetSlot) =>
        OnsetMask |= (ushort)(1 << Math.Clamp(onsetSlot, 0, 15));

    internal void FinalizeFeatures()
    {
        double sumSq = 0;
        for (int i = 0; i < PitchClassHistogram.Length; i++)
            sumSq += PitchClassHistogram[i] * PitchClassHistogram[i];
        Norm = Math.Sqrt(sumSq);
    }

    /// <summary>Combined pitch, rhythmic-onset and active-voice similarity.</summary>
    public double Similarity(BarFeature other)
    {
        if (IsRest || other.IsRest)
            return IsRest == other.IsRest ? 1.0 : 0.0;
        double dot = 0;
        for (int i = 0; i < PitchClassHistogram.Length; i++)
            dot += PitchClassHistogram[i] * other.PitchClassHistogram[i];
        double pitch = Math.Clamp(dot / (Norm * other.Norm), 0.0, 1.0);
        int onsetBits = System.Numerics.BitOperations.PopCount((uint)(OnsetMask ^ other.OnsetMask));
        double rhythm = 1.0 - onsetBits / 16.0;
        double voices = ActiveVoiceMask == other.ActiveVoiceMask ? 1.0 : 0.0;
        return pitch * 0.60 + rhythm * 0.25 + voices * 0.15;
    }
}

/// <summary>A labeled structural span: bars [StartBar, EndBar).</summary>
internal sealed record MusicalSection(int StartBar, int EndBar, string Label);

/// <summary>
/// A repeated block: bars [StartBar, StartBar+LengthBars) are (near-)identical to the
/// immediately following equal-length span, i.e. a loop of <see cref="LengthBars"/> bars.
/// </summary>
internal sealed record RepeatedBlock(int StartBar, int LengthBars);

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

    /// <summary>Detected repeated blocks, sorted by period ascending.</summary>
    public required IReadOnlyList<RepeatedBlock> Loops { get; init; }

    /// <summary>The fundamental (shortest-period) loop, or null when none detected.</summary>
    public RepeatedBlock? PrimaryLoop { get; init; }

    public bool HasSections => Sections.Count > 1;

    public bool HasLoop => PrimaryLoop is not null;
}