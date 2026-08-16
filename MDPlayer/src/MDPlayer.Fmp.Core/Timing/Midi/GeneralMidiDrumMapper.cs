#nullable enable

using Fmp.Core.Timing;
using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>
/// Maps explicitly identified YM2608 rhythm voices to semantic General MIDI
/// percussion notes. Physical domain/index is authoritative; the legacy
/// <c>rhythm:&lt;name&gt;</c> identity is accepted only when no physical domain
/// is available. There is intentionally NO fallback <c>Map()</c> — unknown
/// identities return false and route through the exporter's deterministic
/// unknown-note preallocation; an unknown identity must never masquerade as
/// a semantic GM role (side stick or otherwise).
/// </summary>
internal static class GeneralMidiDrumMapper
{
    /// <summary>
    /// Configuration-internal confidence gate (spec §8, D7): a percussive source
    /// onset is remapped to a GM drum note only when its evidence-driven role
    /// classification is at least this confident. NOT a CLI option. Unknown-role
    /// onsets never pass, whatever their confidence.
    /// </summary>
    internal const double RequiredDrumRoleConfidence = 0.80;

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

        note = 0;
        return false;
    }

    /// <summary>
    /// NoteEvent-capable GM drum mapping (spec §8, D6/D7): maps a classified
    /// <see cref="PercussiveOnset"/> to a GM percussion note, gated by the
    /// internal <see cref="RequiredDrumRoleConfidence"/> threshold. Role == Unknown
    /// or confidence below the threshold returns false — the note stays on its
    /// melodic track and remains percussion evidence (never invented roles, never
    /// a fabricated GM tom). The onset carries no pan, so tom/top resolve to their
    /// deterministic center mappings.
    /// </summary>
    public static bool TryMap(PercussiveOnset onset, out int note)
    {
        ArgumentNullException.ThrowIfNull(onset);

        if (onset.Role == RhythmRole.Unknown
            || onset.Confidence < RequiredDrumRoleConfidence)
        {
            note = 0;
            return false;
        }

        note = onset.Role switch
        {
            RhythmRole.Bd => 36,
            RhythmRole.Sd => 38,
            RhythmRole.Rim => 37,
            RhythmRole.Hh => 42,
            RhythmRole.Tom => MapTom(0f),
            RhythmRole.Top => MapTopCymbal(0f),
            _ => 0,
        };
        return note != 0;
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