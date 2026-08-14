#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>
/// Semantic YM2608 rhythm → General MIDI drum-kit note selection. The YM2608
/// exposes six rhythm voices as stable <c>rhythm:&lt;voice&gt;</c> instrument
/// identities; each maps to a GM percussion note. Tom and top-cymbal selection is
/// pan-aware so a naturally positioned kit matches the source stereo spread
/// (descending left-to-right tom spread in a typical GM kit).
/// </summary>
/// <remarks>
/// Only the six YM2608 identities are treated semantically. Other sample
/// identities (OPL percussion, SSG noise, …) carry no YM2608 rhythm meaning and
/// are left for the exporter's per-identity unique-note allocator, so
/// <see cref="Map"/>'s fallback is never reached in production for those voices.
/// </remarks>
internal static class GeneralMidiDrumMapper
{
    /// <summary>Semantic GM percussion note for a known YM2608 rhythm identity,
    /// with the side-stick (37) as the defensive fallback for unknown identities.</summary>
    public static int Map(RhythmEvent rhythm) =>
        TryMap(rhythm, out int note) ? note : 37;

    /// <summary>True when <paramref name="rhythm"/> is a known YM2608 rhythm voice
    /// and <paramref name="note"/> is its semantic GM note; false for any other
    /// sample identity (which should use the exporter's unique-note allocator).</summary>
    public static bool TryMap(RhythmEvent rhythm, out int note)
    {
        switch (rhythm.InstrumentId)
        {
            case "rhythm:bd":  note = 36; return true; // Bass Drum 1
            case "rhythm:sd":  note = 38; return true; // Acoustic Snare
            case "rhythm:rim": note = 37; return true; // Side Stick
            case "rhythm:hh":  note = 42; return true; // Closed Hi-Hat
            case "rhythm:tom": note = MapTom(rhythm.Pan); return true;
            case "rhythm:top": note = MapTopCymbal(rhythm.Pan); return true;
            default:
                note = 37;
                return false;
        }
    }

    private static int MapTom(float pan) =>
        pan switch
        {
            < -0.25f => 50, // High Tom
            >  0.25f => 45, // Low Tom
            _        => 47, // Low-Mid Tom
        };

    private static int MapTopCymbal(float pan) =>
        pan > 0.25f ? 57 : 49; // Crash Cymbal 2 : Crash Cymbal 1
}