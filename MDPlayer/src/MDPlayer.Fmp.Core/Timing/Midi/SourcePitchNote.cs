namespace Fmp.Core.Midi;

/// <summary>One absolute source pitch sample in MIDI-semitone coordinates.</summary>
internal readonly record struct SourcePitchPoint(long Sample, double MidiNote);

/// <summary>
/// Canonical source-note representation consumed by the MIDI compiler. Chip
/// register formats and decoder-specific pitch math end before this boundary.
/// </summary>
internal sealed record SourcePitchNote(
    long StartSample,
    long EndSample,
    IReadOnlyList<SourcePitchPoint> PitchCurve,
    string InstrumentId,
    int Velocity,
    string SourceVoiceId)
{
    public double InitialMidiNote => PitchCurve.Count == 0 ? double.NaN : PitchCurve[0].MidiNote;
}
