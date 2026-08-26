#nullable enable

using System.Text;

namespace Fmp.Core.Midi;

/// <summary>The content of one MIDI track: its name and ordered musical events.</summary>
internal readonly record struct MidiEndpoint(byte Port, int Channel);

internal sealed class MidiTrack
{
    private readonly List<PackedMidiEvent> _packedEvents;

    public MidiTrack()
    {
        _packedEvents = new List<PackedMidiEvent>();
        Name = string.Empty;
        SourceVoiceId = string.Empty;
        Endpoint = new MidiEndpoint(0, 0);
    }

    internal MidiTrack(int initialEventCapacity)
    {
        _packedEvents = new List<PackedMidiEvent>(Math.Max(0, initialEventCapacity));
        Name = string.Empty;
        SourceVoiceId = string.Empty;
        Endpoint = new MidiEndpoint(0, 0);
    }

    public required string Name { get; init; }

    public string SourceVoiceId { get; init; } = string.Empty;

    public required MidiEndpoint Endpoint { get; init; }

    /// <summary>
    /// The canonical source notes compiled onto this track, in source order.
    /// Retained for test oracles (serialized-bytes pitch/timing verification);
    /// production export does not depend on it. Null when the track carries no
    /// pitch-validated source notes (sample triggers, rhythm hits).
    /// </summary>
    internal IReadOnlyList<SourcePitchNote>? SourceNotes { get; init; }

    /// <summary>
    /// Compatibility view. Production export stays packed; rich records are
    /// materialized only when an inspector explicitly asks for them.
    /// </summary>
    public List<MidiEventBase> Events => MaterializePackedEvents();

    internal List<PackedMidiEvent> PackedEvents => _packedEvents;

    internal void AddPacked(PackedMidiEvent evt) => _packedEvents.Add(evt);

    private List<MidiEventBase> MaterializePackedEvents()
    {
        var events = new List<MidiEventBase>(_packedEvents.Count);
        for (int index = 0; index < _packedEvents.Count; index++)
        {
            PackedMidiEvent evt = _packedEvents[index];
            MidiEventBase rich = evt.Kind switch
            {
                PackedMidiEventKind.NoteOff => new MidiNoteEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A, evt.B, NoteOn: false),
                PackedMidiEventKind.NoteOn => new MidiNoteEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A, evt.B, NoteOn: true),
                PackedMidiEventKind.Bank => new MidiBankEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.Program => new MidiProgramEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.PitchBend => new MidiPitchBendEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.BendRange => new MidiBendRangeEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.Tempo => new MidiTempoEvent(evt.Tick, evt.A),
                PackedMidiEventKind.TimeSignature => new MidiTimeSignatureEvent(
                    evt.Tick, evt.A, evt.B),
                _ => throw new InvalidOperationException($"Unknown packed MIDI event kind {evt.Kind}."),
            };
            rich.SourceOrder = evt.SourceOrder;
            events.Add(rich);
        }
        return events;
    }

    /// <summary>Set only after the planner has applied canonical ordering.</summary>
    internal bool HasCanonicalEventOrder { get; set; }
}

/// <summary>
/// Standard MIDI file (SMF) Format 1 serializer driven by a
/// <see cref="MidiTranscriber"/>-derived packed event stream. Track 0 is the
/// conductor track (tempo, time signature, markers, metadata); every other
/// track holds one logical voice. Events are ordered deterministically within a
/// tick (by tick, then <see cref="MidiEventOrder.Rank"/>, then
/// <see cref="PackedMidiEvent.SourceOrder"/>), so the same input always produces
/// identical bytes. This class performs NO timing conversion of its own.
/// </summary>
internal sealed class MidiFileWriter
{
    private readonly int _ppq;

    public MidiFileWriter(int ppq)
    {
        if (ppq <= 0 || ppq > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(ppq));
        _ppq = ppq;
    }

    /// <param name="conductor">Events for track 0 (tempo, time signature, markers, text).</param>
    /// <param name="tracks">The musical tracks, in order, with their events.</param>
    public byte[] Write(IReadOnlyList<MidiEventBase> conductor, IReadOnlyList<MidiTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(conductor);
        ArgumentNullException.ThrowIfNull(tracks);
        return WritePackedSequential(conductor, tracks);
    }

    /// <summary>
    /// Writes the production packed IR directly as SMF bytes, keeping the
    /// sequential delta/status/buffer state in this writer instead of creating
    /// one DryWetMIDI object per event.
    /// </summary>
    private byte[] WritePackedSequential(
        IReadOnlyList<MidiEventBase> conductor,
        IReadOnlyList<MidiTrack> tracks)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "MThd");
        WriteUInt32(stream, 6);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, checked((ushort)(tracks.Count + 1)));
        WriteUInt16(stream, checked((ushort)_ppq));

        long conductorStart = WriteTrackHeader(stream, "Conductor", port: null);
        AppendRawConductor(stream, conductor);
        WriteEndOfTrack(stream);
        FinishTrack(stream, conductorStart);

        var endpoints = new HashSet<MidiEndpoint>();
        for (int index = 0; index < tracks.Count; index++)
        {
            MidiTrack track = tracks[index];
            if (!endpoints.Add(track.Endpoint))
                throw new InvalidOperationException(
                    $"MIDI endpoint ({track.Endpoint.Port}, {track.Endpoint.Channel}) is assigned to more than one track.");
            if (track.Endpoint.Channel is < 0 or > 15)
                throw new InvalidOperationException(
                    $"MIDI channel {track.Endpoint.Channel} is outside [0, 15].");

            long trackStart = WriteTrackHeader(stream, track.Name, track.Endpoint.Port);
            AppendRawPackedEvents(stream, track.PackedEvents, track.HasCanonicalEventOrder);
            WriteEndOfTrack(stream);
            FinishTrack(stream, trackStart);
        }
        return stream.ToArray();
    }

    private static long WriteTrackHeader(Stream stream, string name, byte? port)
    {
        WriteAscii(stream, "MTrk");
        WriteUInt32(stream, 0);
        long dataStart = stream.Position;
        WriteDelta(stream, 0);
        WriteMeta(stream, 0x03, Encoding.UTF8.GetBytes(name ?? string.Empty));
        if (port is byte value)
        {
            WriteDelta(stream, 0);
            stream.WriteByte(0xFF);
            stream.WriteByte(0x21);
            stream.WriteByte(0x01);
            stream.WriteByte(value);
        }
        return dataStart;
    }

    private static void FinishTrack(Stream stream, long dataStart)
    {
        long end = stream.Position;
        stream.Position = dataStart - 4;
        WriteUInt32(stream, checked((uint)(end - dataStart)));
        stream.Position = end;
    }

    private static void AppendRawConductor(Stream stream, IReadOnlyList<MidiEventBase> events)
    {
        var ordered = new MidiEventBase[events.Count];
        for (int index = 0; index < events.Count; index++)
            ordered[index] = events[index];
        Array.Sort(ordered, static (left, right) => MidiEventOrder.Compare(left, right));

        long previousTick = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            MidiEventBase evt = ordered[index];
            if (evt.Tick < 0 || evt.Tick < previousTick)
                throw new InvalidOperationException("Conductor MIDI events must have non-decreasing non-negative ticks.");
            WriteRawRichEvent(stream, evt, evt.Tick - previousTick);
            previousTick = evt.Tick;
        }
    }

    private static void AppendRawPackedEvents(
        Stream stream,
        IReadOnlyList<PackedMidiEvent> events,
        bool canonical)
    {
        IReadOnlyList<PackedMidiEvent> ordered = events;
        if (!canonical)
        {
            var copy = new PackedMidiEvent[events.Count];
            for (int index = 0; index < events.Count; index++)
                copy[index] = events[index];
            Array.Sort(copy, static (left, right) => MidiEventOrder.Compare(left, right));
            ordered = copy;
        }

        long previousTick = 0;
        for (int index = 0; index < ordered.Count; index++)
        {
            PackedMidiEvent evt = ordered[index];
            if (evt.Tick < 0 || evt.Tick < previousTick)
                throw new InvalidOperationException("MIDI events must have non-decreasing non-negative ticks.");
            WriteRawPackedEvent(stream, evt, evt.Tick - previousTick);
            previousTick = evt.Tick;
        }
    }

    private static void WriteRawRichEvent(Stream stream, MidiEventBase evt, long delta)
    {
        switch (evt)
        {
            case MidiTempoEvent tempo:
                if (tempo.MicrosecondsPerQuarter <= 0)
                    throw new InvalidOperationException(
                        $"Tempo {tempo.MicrosecondsPerQuarter} us/qn is not positive.");
                WriteDelta(stream, delta);
                stream.WriteByte(0xFF);
                stream.WriteByte(0x51);
                stream.WriteByte(0x03);
                WriteUInt24(stream, checked((uint)tempo.MicrosecondsPerQuarter));
                return;
            case MidiTimeSignatureEvent signature:
                WriteDelta(stream, delta);
                WriteTimeSignature(stream, signature.Numerator, signature.Denominator);
                return;
            case MidiMetaTextEvent text:
                WriteDelta(stream, delta);
                WriteMeta(stream, checked((byte)text.Type), Encoding.UTF8.GetBytes(text.Text ?? string.Empty));
                return;
            case MidiMarkerEvent marker:
                WriteDelta(stream, delta);
                WriteMeta(stream, 0x06, Encoding.UTF8.GetBytes(marker.Name ?? string.Empty));
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported conductor MIDI event type {evt.GetType().Name} reached the sequential writer.");
        }
    }

    private static void WriteRawPackedEvent(Stream stream, PackedMidiEvent evt, long delta)
    {
        switch (evt.Kind)
        {
            case PackedMidiEventKind.NoteOn:
                WriteChannelEvent(stream, delta, (byte)(0x90 | (evt.Channel & 0x0F)), evt.A, evt.B);
                return;
            case PackedMidiEventKind.NoteOff:
                WriteChannelEvent(stream, delta, (byte)(0x80 | (evt.Channel & 0x0F)), evt.A, evt.B);
                return;
            case PackedMidiEventKind.Program:
                WriteDelta(stream, delta);
                stream.WriteByte((byte)(0xC0 | (evt.Channel & 0x0F)));
                stream.WriteByte((byte)(evt.A & 0x7F));
                return;
            case PackedMidiEventKind.Bank:
                WriteControlChange(stream, delta, evt.Channel, 0, evt.A);
                return;
            case PackedMidiEventKind.PitchBend:
                int bendValue = evt.A + 8192;
                if (bendValue is < 0 or > 16383)
                    throw new InvalidOperationException(
                        $"Pitch-bend value {evt.A} is out of the valid [-8192, 8191] range.");
                WriteChannelEvent(stream, delta, (byte)(0xE0 | (evt.Channel & 0x0F)),
                    bendValue & 0x7F, bendValue >> 7);
                return;
            case PackedMidiEventKind.Tempo:
                if (evt.A <= 0)
                    throw new InvalidOperationException(
                        $"Tempo {evt.A} us/qn is not positive.");
                WriteDelta(stream, delta);
                stream.WriteByte(0xFF);
                stream.WriteByte(0x51);
                stream.WriteByte(0x03);
                WriteUInt24(stream, checked((uint)evt.A));
                return;
            case PackedMidiEventKind.TimeSignature:
                WriteDelta(stream, delta);
                WriteTimeSignature(stream, evt.A, evt.B);
                return;
            case PackedMidiEventKind.BendRange:
                WritePackedBendRange(stream, evt, delta);
                return;
            default:
                throw new InvalidOperationException($"Unknown packed MIDI event kind {evt.Kind}.");
        }
    }

    private static void WritePackedBendRange(Stream stream, PackedMidiEvent evt, long delta)
    {
        if (evt.A is < 0 or > 127)
            throw new InvalidOperationException(
                $"Pitch-bend range {evt.A} is outside the valid [0, 127] semitone range.");
        int value = evt.A;
        // RPN pitch-bend range (semitones): select RPN 0, write both data-entry
        // bytes, then null the RPN (CC101/CC100 = 127) to prevent later Data
        // Entry messages from changing channel sensitivity. Only the first CC
        // carries the source delta.
        WriteControlChange(stream, delta, evt.Channel, 101, 0);
        WriteControlChange(stream, 0, evt.Channel, 100, 0);
        WriteControlChange(stream, 0, evt.Channel, 6, value);
        WriteControlChange(stream, 0, evt.Channel, 38, 0);
        WriteControlChange(stream, 0, evt.Channel, 101, 127);
        WriteControlChange(stream, 0, evt.Channel, 100, 127);
    }

    private static void WriteControlChange(Stream stream, long delta, int channel, int control, int value)
    {
        WriteChannelEvent(stream, delta, (byte)(0xB0 | (channel & 0x0F)), control, value);
    }

    private static void WriteChannelEvent(Stream stream, long delta, byte status, int first, int second)
    {
        WriteDelta(stream, delta);
        stream.WriteByte(status);
        stream.WriteByte((byte)(first & 0x7F));
        stream.WriteByte((byte)(second & 0x7F));
    }

    private static void WriteTimeSignature(Stream stream, int numerator, int denominator)
    {
        if (numerator is < 1 or > byte.MaxValue
            || denominator < 1 || denominator > 256
            || (denominator & (denominator - 1)) != 0)
            throw new InvalidOperationException("Invalid MIDI time-signature values.");
        int power = 0;
        for (int value = denominator; value > 1; value >>= 1)
            power++;
        stream.WriteByte(0xFF);
        stream.WriteByte(0x58);
        stream.WriteByte(0x04);
        stream.WriteByte((byte)numerator);
        stream.WriteByte((byte)power);
        stream.WriteByte(24);
        stream.WriteByte(8);
    }

    private static void WriteMeta(Stream stream, byte type, byte[] data)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(type);
        WriteVlq(stream, data.Length);
        stream.Write(data, 0, data.Length);
    }

    private static void WriteEndOfTrack(Stream stream)
    {
        WriteDelta(stream, 0);
        stream.WriteByte(0xFF);
        stream.WriteByte(0x2F);
        stream.WriteByte(0x00);
    }

    private static void WriteDelta(Stream stream, long value) => WriteVlq(stream, value);

    private static void WriteVlq(Stream stream, long value)
    {
        if (value is < 0 or > 0x0FFFFFFF)
            throw new InvalidOperationException($"MIDI variable-length value {value} is outside [0, 0x0FFFFFFF].");
        uint encoded = (uint)value;
        Span<byte> bytes = stackalloc byte[4];
        int count = 1;
        bytes[3] = (byte)(encoded & 0x7F);
        while ((encoded >>= 7) != 0)
        {
            bytes[3 - count] = (byte)((encoded & 0x7F) | 0x80);
            count++;
        }
        stream.Write(bytes.Slice(4 - count, count));
    }

    private static void WriteAscii(Stream stream, string value)
    {
        for (int index = 0; index < value.Length; index++)
            stream.WriteByte((byte)value[index]);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt24(Stream stream, uint value)
    {
        if (value > 0xFFFFFF)
            throw new InvalidOperationException("MIDI 24-bit value is out of range.");
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}