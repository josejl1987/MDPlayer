#nullable enable

namespace Fmp.Core.Midi;

/// <summary>
/// A serialized MIDI event with an explicit tick so events can be ordered
/// deterministically and independently of the track layout. Absolute ticks are
/// pre-computed through the <see cref="Fmp.Core.Timing.MusicalTimeMap"/> so
/// timing never accumulates rounding drift. <see cref="Tick"/> is mutable to
/// permit optional grid quantization after conversion.
/// </summary>
internal abstract record MidiEventBase
{
    protected MidiEventBase(long tick) => Tick = tick;

    public long Tick { get; set; }
}

/// <summary>Note on/off. Note-off uses velocity from the paired note; retriggers
/// rely on the writer ordering note-off before note-on at the same tick.</summary>
internal sealed record MidiNoteEvent(
    long TickIn,
    int Track,
    int Channel,
    int Note,
    int Velocity,
    bool NoteOn) : MidiEventBase(TickIn);

/// <summary>Set Tempo (FF 51).</summary>
internal sealed record MidiTempoEvent(long TickIn, int MicrosecondsPerQuarter) : MidiEventBase(TickIn);

/// <summary>Time Signature (FF 58).</summary>
internal sealed record MidiTimeSignatureEvent(long TickIn, int Numerator, int Denominator) : MidiEventBase(TickIn);

/// <summary>Track / sequence / copyright / text marker (FF 0x01-0x06, 0x08).</summary>
internal sealed record MidiMetaTextEvent(long TickIn, int Type, string Text) : MidiEventBase(TickIn);

/// <summary>Marker meta (FF 06).</summary>
internal sealed record MidiMarkerEvent(long TickIn, string Name) : MidiEventBase(TickIn);

/// <summary>Program change (Cn).</summary>
internal sealed record MidiProgramEvent(long TickIn, int Track, int Channel, int Program) : MidiEventBase(TickIn);

/// <summary>Bank select (control change 0 / 32) plus optional program.</summary>
internal sealed record MidiBankEvent(long TickIn, int Track, int Channel, int Bank) : MidiEventBase(TickIn);

/// <summary>Pitch bend (E0), 14-bit signed value in [-8192, 8191].</summary>
internal sealed record MidiPitchBendEvent(long TickIn, int Track, int Channel, int Bend) : MidiEventBase(TickIn);

/// <summary>RPN-based pitch-bend range (controller 101/100 + value) — Batch 4.</summary>
internal sealed record MidiBendRangeEvent(long TickIn, int Track, int Channel, int Semitones) : MidiEventBase(TickIn);

/// <summary>End of Track is appended automatically.</summary>
internal static class MidiEventOrder
{
    /// <summary>Deterministic ordering within a single tick (§9 / §10): note-off,
    /// tempo/time-signature, bank/program, pitch bend, note-on, other metadata.</summary>
    public static int Rank(MidiEventBase evt)
    {
        return evt switch
        {
            MidiNoteEvent note when !note.NoteOn => 0,
            MidiTempoEvent => 1,
            MidiTimeSignatureEvent => 1,
            MidiMarkerEvent => 1,
            MidiMetaTextEvent => 1,
            MidiBankEvent => 2,
            MidiProgramEvent => 2,
            MidiBendRangeEvent => 2,
            MidiPitchBendEvent => 3,
            MidiNoteEvent => 4,
            _ => 5,
        };
    }
}
