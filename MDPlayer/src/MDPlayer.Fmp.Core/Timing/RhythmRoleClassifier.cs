#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>Musical role of a percussion voice. One unified classification
/// feeds both tempo inference and structural grid selection (Patch 3d).</summary>
internal enum RhythmRole
{
    Unknown,
    Bd,
    Sd,
    Hh,
    Tom,
    Top,
    Rim,
}

/// <summary>
/// Single source of truth for classifying percussion events into
/// <see cref="RhythmRole"/>. Both tempo inference and structural grid selection
/// route through this classifier so their role labels can never drift apart.
/// Text matching is order-sensitive: "bassdrum" contains "sd", so the
/// bass-drum/kick checks must run before the snare check.
/// </summary>
internal static class RhythmRoleClassifier
{
    public static RhythmRole Classify(RhythmEvent rhythm)
    {
        if (rhythm is null)
            return RhythmRole.Unknown;
        string text = string.Join(
            " ",
            rhythm.Voice,
            rhythm.ChannelId,
            rhythm.ParentVoiceId,
            rhythm.InstrumentId,
            rhythm.Domain?.VoiceFamily.ToString() ?? string.Empty);
        return ClassifyText(text, midiNote: null);
    }

    public static RhythmRole Classify(NoteEvent note)
    {
        if (note is null)
            return RhythmRole.Unknown;
        string text = string.Join(
            " ",
            note.ChannelId,
            note.InstrumentId,
            note.Domain?.VoiceFamily.ToString() ?? string.Empty);
        return ClassifyText(text, note.InitialMidiNote);
    }

    private static RhythmRole ClassifyText(string text, double? midiNote)
    {
        string value = text.ToLowerInvariant();
        if (value.Contains("bassdrum", StringComparison.Ordinal)
            || value.Contains("bass-drum", StringComparison.Ordinal)
            || value.Contains("kick", StringComparison.Ordinal)
            || (value.Contains("bd", StringComparison.Ordinal) && !IsBareShortRole(value)))
            return RhythmRole.Bd;
        if (value.Contains("snare", StringComparison.Ordinal)
            || (value.Contains("sd", StringComparison.Ordinal) && !IsBareShortRole(value)))
            return RhythmRole.Sd;
        if (value.Contains("hihat", StringComparison.Ordinal)
            || value.Contains("hi-hat", StringComparison.Ordinal)
            || (value.Contains("hh", StringComparison.Ordinal) && !IsBareShortRole(value))
            || value.Contains("hat", StringComparison.Ordinal))
            return RhythmRole.Hh;
        if (value.Contains("tom", StringComparison.Ordinal))
            return RhythmRole.Tom;
        if (value.Contains("top", StringComparison.Ordinal))
            return RhythmRole.Top;
        if (value.Contains("rim", StringComparison.Ordinal))
            return RhythmRole.Rim;

        // Text alone is not a drum voice: fall back to the General MIDI drum map
        // only when the event looks percussive. Text-first keeps named voices
        // (e.g. "BD", "Snare 1") authoritative over pitch.
        bool drumText = value.Contains("drum", StringComparison.Ordinal)
            || value.Contains("rhythm", StringComparison.Ordinal)
            || value.Contains("perc", StringComparison.Ordinal);
        if (!drumText || midiNote is not double midi)
            return RhythmRole.Unknown;
        int pitch = (int)Math.Round(midi);
        return pitch switch
        {
            35 or 36 => RhythmRole.Bd,
            37 or 38 or 40 => RhythmRole.Sd,
            41 or 43 or 45 or 47 or 48 or 50 => RhythmRole.Tom,
            42 or 44 or 46 => RhythmRole.Hh,
            _ => RhythmRole.Unknown,
        };
    }

    /// <summary>True when the text is a bare two-letter role label (e.g. exactly
    /// "bd" or "sd"), so the short substrings do not fire on other words.</summary>
    private static bool IsBareShortRole(string value) =>
        value.Length <= 3 || value.Trim() is "bd" or "sd" or "hh";
}
