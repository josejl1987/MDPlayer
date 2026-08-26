#nullable enable

namespace Fmp.Core.Midi;

/// <summary>
/// A serialized MIDI event with an explicit tick so events can be ordered
/// deterministically and independently of the track layout. Absolute
/// source-relative MIDI tick: for raw transcription this is obtained directly
/// from source sample time on the fixed transport (no musical map, no grid
/// quantization, no accumulated rounding drift).
/// </summary>
internal abstract record MidiEventBase
{
    protected MidiEventBase(long tick) => Tick = tick;

    public long Tick { get; set; }

    /// <summary>
    /// Deterministic secondary ordering key for equal-priority events sharing a
    /// tick. Populated by the transcriber from the source event sequence so the
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

/// <summary>RPN-based pitch-bend range (controller 101/100 + value).</summary>
internal sealed record MidiBendRangeEvent(long TickIn, int Track, int Channel, int Semitones) : MidiEventBase(TickIn);

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
    /// <summary>Deterministic ordering within a single channel program.</summary>
    public static int Rank(MidiEventBase evt)
    {
        return evt switch
        {
            MidiNoteEvent note when !note.NoteOn => 0,
            MidiBankEvent => 1,
            MidiProgramEvent => 1,
            MidiBendRangeEvent => 2,
            MidiPitchBendEvent => 3,
            MidiNoteEvent => 4,
            MidiTempoEvent => 6,
            MidiTimeSignatureEvent => 6,
            MidiMarkerEvent => 6,
            MidiMetaTextEvent => 6,
            _ => 7,
        };
    }

    public static int Compare(MidiEventBase left, MidiEventBase right)
    {
        int compare = left.Tick.CompareTo(right.Tick);
        if (compare != 0)
            return compare;
        compare = Rank(left).CompareTo(Rank(right));
        return compare != 0 ? compare : left.SourceOrder.CompareTo(right.SourceOrder);
    }

    public static int Compare(PackedMidiEvent left, PackedMidiEvent right)
    {
        int compare = left.Tick.CompareTo(right.Tick);
        if (compare != 0)
            return compare;
        compare = Rank(left).CompareTo(Rank(right));
        return compare != 0 ? compare : left.SourceOrder.CompareTo(right.SourceOrder);
    }

    public static int Rank(PackedMidiEvent evt) => evt.Kind switch
    {
        PackedMidiEventKind.NoteOff => 0,
        PackedMidiEventKind.Bank => 1,
        PackedMidiEventKind.Program => 1,
        PackedMidiEventKind.BendRange => 2,
        PackedMidiEventKind.PitchBend => 3,
        PackedMidiEventKind.NoteOn => 4,
        PackedMidiEventKind.Tempo => 6,
        PackedMidiEventKind.TimeSignature => 6,
        _ => 7,
    };
}
