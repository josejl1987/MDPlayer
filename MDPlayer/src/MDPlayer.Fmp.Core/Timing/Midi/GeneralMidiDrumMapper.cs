#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>
/// Maps explicitly identified YM2608 rhythm voices to semantic General MIDI
/// percussion notes. Physical domain/index is authoritative; the legacy
/// <c>rhythm:&lt;name&gt;</c> identity is accepted only when no physical domain
/// is available.
/// </summary>
internal static class GeneralMidiDrumMapper
{
    public static int Map(RhythmEvent rhythm) =>
        TryMap(rhythm, out int note) ? note : 37;

    public static bool TryMap(RhythmEvent rhythm, out int note)
    {
        ArgumentNullException.ThrowIfNull(rhythm);

        if (rhythm.Domain is SourceDomainKey domain
            && domain.Device.Type == ChipType.Ym2608
            && domain.VoiceFamily == VoiceKind.Rhythm)
        {
            note = MapYm2608Voice(domain.Index, rhythm.Pan);
            return true;
        }

        // Compatibility for older timelines that used either rhythm:<voice> or
        // ...rhythm.<voice> instrument identities without a physical domain.
        if (rhythm.Domain is null)
        {
            string? voice = LegacyRhythmVoice(rhythm.InstrumentId);
            voice ??= LegacyRhythmVoice(rhythm.ChannelId);
            if (voice is not null)
            {
                note = voice switch
                {
                    "bd" => 36,
                    "sd" => 38,
                    "rim" => 37,
                    "hh" => 42,
                    "tom" => MapTom(rhythm.Pan),
                    "top" => MapTopCymbal(rhythm.Pan),
                    _ => 0,
                };
                if (note != 0)
                    return true;
            }
        }

        note = 37;
        return false;
    }

    private static string? LegacyRhythmVoice(string identity)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        int colon = identity.LastIndexOf("rhythm:", comparison);
        if (colon >= 0 && (colon == 0 || identity[colon - 1] == '.'))
            return identity[(colon + "rhythm:".Length)..].ToLowerInvariant();

        int dot = identity.LastIndexOf("rhythm.", comparison);
        if (dot >= 0 && (dot == 0 || identity[dot - 1] == '.'))
            return identity[(dot + "rhythm.".Length)..].ToLowerInvariant();

        return null;
    }


    private static int MapYm2608Voice(int voiceIndex, float pan) =>
        voiceIndex switch
        {
            0 => 36, // Bass Drum 1
            1 => 38, // Acoustic Snare
            2 => MapTopCymbal(pan),
            3 => 42, // Closed Hi-Hat
            4 => MapTom(pan),
            5 => 37, // Side Stick / rim
            _ => throw new ArgumentOutOfRangeException(
                nameof(voiceIndex), voiceIndex, "YM2608 rhythm index must be in [0, 5]."),
        };

    private static int MapTom(float pan) =>
        pan switch
        {
            < -0.25f => 50, // High Tom
            > 0.25f => 45,  // Low Tom
            _ => 48,        // Hi-Mid Tom
        };

    private static int MapTopCymbal(float pan) =>
        pan > 0.25f ? 57 : 49; // Crash Cymbal 2 : Crash Cymbal 1
}