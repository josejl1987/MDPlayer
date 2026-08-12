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

    /// <summary>
    /// Deterministic secondary ordering key for equal-priority events sharing a
    /// tick. Populated by the exporter from the source event sequence so the
    /// writer's absolute-tick sort is fully deterministic and never depends on
    /// insertion order, dictionary enumeration, hash codes, object identity, or
    /// thread scheduling (§34, §45, §73). Lower values sort first.
    /// </summary>
    public int SourceOrder { get; set; }
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

/// <summary>
/// RPN-based channel tuning (controller 101/100 + CC6/CC38 data entry): fine
/// tuning in cents (RPN 0x0002) plus optional coarse semitones (RPN 0x0001) for
/// biases beyond ±100c. Emitted at tick 0, null-RPN terminated, at most one per
/// endpoint (D10/D11). Fidelity mode restores the accepted domain bias through
/// this event; DAW-friendly mode emits none.
/// </summary>
internal sealed record MidiTuningEvent(
    long TickIn,
    int Track,
    int Channel,
    int CoarseSemitones,
    int FineCents) : MidiEventBase(TickIn);

/// <summary>
/// Compact production MIDI IR. Ordinary channel events carry only fixed-width
/// values; the richer polymorphic records remain a compatibility view for tests
/// and callers that inspect an exported track after serialization.
/// </summary>
internal enum PackedMidiEventKind : byte
{
    NoteOff,
    NoteOn,
    Tempo,
    TimeSignature,
    Bank,
    Program,
    BendRange,
    PitchBend,
    Tuning,
}

internal struct PackedMidiEvent
{
    public long Tick;
    public int SourceOrder;
    public int Track;
    public int A;
    public int B;
    public int Channel;
    public PackedMidiEventKind Kind;

    public static PackedMidiEvent Note(
        long tick, int track, int channel, int note, int velocity, bool noteOn)
        => new()
        {
            Tick = tick,
            Track = track,
            Channel = channel,
            A = note,
            B = velocity,
            Kind = noteOn ? PackedMidiEventKind.NoteOn : PackedMidiEventKind.NoteOff,
        };

    public static PackedMidiEvent Bank(long tick, int track, int channel, int bank)
        => new()
        {
            Tick = tick,
            Track = track,
            Channel = channel,
            A = bank,
            Kind = PackedMidiEventKind.Bank,
        };

    public static PackedMidiEvent Program(long tick, int track, int channel, int program)
        => new()
        {
            Tick = tick,
            Track = track,
            Channel = channel,
            A = program,
            Kind = PackedMidiEventKind.Program,
        };

    public static PackedMidiEvent PitchBend(long tick, int track, int channel, int bend)
        => new()
        {
            Tick = tick,
            Track = track,
            Channel = channel,
            A = bend,
            Kind = PackedMidiEventKind.PitchBend,
        };

    public static PackedMidiEvent BendRange(long tick, int track, int channel, int semitones)
        => new()
        {
            Tick = tick,
            Track = track,
            Channel = channel,
            A = semitones,
            Kind = PackedMidiEventKind.BendRange,
        };

    public static PackedMidiEvent Tuning(
        long tick, int track, int channel, int coarseSemitones, int fineCents)
        => new()
        {
            Tick = tick,
            Track = track,
            Channel = channel,
            A = coarseSemitones,
            B = fineCents,
            Kind = PackedMidiEventKind.Tuning,
        };

    public static PackedMidiEvent Tempo(long tick, int microsecondsPerQuarter)
        => new()
        {
            Tick = tick,
            A = microsecondsPerQuarter,
            Kind = PackedMidiEventKind.Tempo,
        };

    public static PackedMidiEvent TimeSignature(long tick, int numerator, int denominator)
        => new()
        {
            Tick = tick,
            A = numerator,
            B = denominator,
            Kind = PackedMidiEventKind.TimeSignature,
        };
}

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
            MidiTuningEvent => 2,
            MidiPitchBendEvent => 3,
            MidiNoteEvent => 4,
            _ => 5,
        };
    }

    public static int Rank(PackedMidiEvent evt) => evt.Kind switch
    {
        PackedMidiEventKind.NoteOff => 0,
        PackedMidiEventKind.Tempo => 1,
        PackedMidiEventKind.TimeSignature => 1,
        PackedMidiEventKind.BendRange => 2,
        PackedMidiEventKind.Tuning => 2,
        PackedMidiEventKind.Bank => 2,
        PackedMidiEventKind.Program => 2,
        PackedMidiEventKind.PitchBend => 3,
        PackedMidiEventKind.NoteOn => 4,
        _ => 5,
    };
}
