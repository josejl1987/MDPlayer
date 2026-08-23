#nullable enable

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using System.Text;

namespace Fmp.Core.Midi;

/// <summary>The content of one MIDI track: its name and ordered musical events.</summary>
internal readonly record struct MidiEndpoint(byte Port, int Channel);

internal sealed class MidiTrack
{
    private List<MidiEventBase>? _events;
    private readonly List<PackedMidiEvent> _packedEvents;
    private MidiChannelProgram? _channelProgram;

    public MidiTrack()
    {
        _events = new List<MidiEventBase>();
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
    /// The source voice/channel-state domain that owns this track's pitch state.
    /// Compatibility tracks may leave this unset; production transcription sets it
    /// before serialization.
    /// </summary>
    internal MidiVoiceDomain? VoiceDomain { get; set; }

    /// <summary>
    /// The sole ordered channel-state stream for this playable track. Rich
    /// compatibility tracks may leave this unset; production tracks attach it
    /// before adding events.
    /// </summary>
    internal MidiChannelProgram? ChannelProgram
    {
        get => _channelProgram;
        init
        {
            if (value is null)
            {
                _channelProgram = null;
                return;
            }
            if (_events is null)
                throw new InvalidOperationException(
                    "A packed MIDI track cannot also own a rich channel program.");
            if (_events.Count != 0)
                throw new InvalidOperationException(
                    "A MIDI channel program must be attached before track events are added.");
            _channelProgram = value;
            _events = value.MutableEvents;
        }
    }

    /// <summary>
    /// Compatibility view. Production export keeps this null and materializes
    /// rich records only when an inspector explicitly asks for them.
    /// </summary>
    public List<MidiEventBase> Events => _events ??= MaterializePackedEvents();

    internal List<PackedMidiEvent> PackedEvents => _packedEvents;
    internal bool UsesPackedEvents => _events is null;

    internal void AddPacked(PackedMidiEvent evt)
    {
        if (_events is not null)
            throw new InvalidOperationException("Packed events cannot be mixed with compatibility events.");
        _packedEvents.Add(evt);
    }

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
                PackedMidiEventKind.ControlChange => new MidiControlChangeEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A, evt.B),
                PackedMidiEventKind.Program => new MidiProgramEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.PitchBend => new MidiPitchBendEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.BendRange => new MidiBendRangeEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A),
                PackedMidiEventKind.Tuning => new MidiTuningEvent(
                    evt.Tick, evt.Track, evt.Channel, evt.A, evt.B),
                PackedMidiEventKind.Tempo => new MidiTempoEvent(evt.Tick, evt.A),
                PackedMidiEventKind.TimeSignature => new MidiTimeSignatureEvent(
                    evt.Tick, evt.A, evt.B),
                _ => throw new InvalidOperationException($"Unknown packed MIDI event kind {evt.Kind}.")
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
/// Standard MIDI file (SMF) Format 1 serializer built as a thin adapter over the
/// Melanchall.DryWetMidi library, driven by a
/// <see cref="MidiTranscriber"/>-derived event stream. Track 0 is the conductor
/// track (tempo, time signature, markers, metadata); every other track holds one
/// logical voice. Events are ordered deterministically within a tick (by tick,
/// then <see cref="MidiEventOrder.Rank"/>, then
/// <see cref="MidiEventBase.SourceOrder"/>), so the same input always produces
/// identical bytes. Tempo events are guaranteed to precede any notes at the same
/// tick. This class performs NO timing conversion of its own.
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

        if (tracks.Any(track => track.VoiceDomain is not null))
            MidiConductor.Validate(conductor);
        ValidateVoiceDomains(tracks);

        bool allTracksPacked = true;
        for (int index = 0; index < tracks.Count; index++)
        {
            if (!tracks[index].UsesPackedEvents)
            {
                allTracksPacked = false;
                break;
            }
        }
        if (allTracksPacked)
            return WritePackedSequential(conductor, tracks);

        var file = new MidiFile
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision((short)_ppq),
        };

        var conductorChunk = new TrackChunk();
        conductorChunk.Events.Add(new SequenceTrackNameEvent("Conductor"));
        AppendEvents(conductorChunk, conductor);
        file.Chunks.Add(conductorChunk);

        var endpoints = new HashSet<MidiEndpoint>();
        foreach (MidiTrack track in tracks)
        {
            if (!endpoints.Add(track.Endpoint))
                throw new InvalidOperationException(
                    $"MIDI endpoint ({track.Endpoint.Port}, {track.Endpoint.Channel}) is assigned to more than one track.");
            if (track.Endpoint.Channel is < 0 or > 15)
                throw new InvalidOperationException(
                    $"MIDI channel {track.Endpoint.Channel} is outside [0, 15].");
            var chunk = new TrackChunk();
            chunk.Events.Add(new SequenceTrackNameEvent(track.Name));
            chunk.Events.Add(new PortPrefixEvent(track.Endpoint.Port));
            if (track.UsesPackedEvents)
                AppendPackedEvents(chunk, track.PackedEvents, track.HasCanonicalEventOrder);
            else
                AppendEvents(chunk, track.ChannelProgram?.OrderedEvents ?? track.Events,
                    track.HasCanonicalEventOrder);
            file.Chunks.Add(chunk);
        }

        using var stream = new MemoryStream();
        file.Write(stream, MidiFileFormat.MultiTrack, new WritingSettings());
        return stream.ToArray();
    }

    private static void ValidateVoiceDomains(IReadOnlyList<MidiTrack> tracks)
    {
        var domainsByEndpoint = new Dictionary<MidiEndpoint, MidiVoiceDomain>();
        foreach (MidiTrack track in tracks)
        {
            if (track.ChannelProgram is MidiChannelProgram program)
            {
                if (program.MidiChannel != track.Endpoint.Channel)
                    throw new InvalidOperationException(
                        $"MIDI channel program '{program.SourceVoiceId}' does not match track endpoint.");
                if (!string.IsNullOrEmpty(track.SourceVoiceId)
                    && !string.Equals(program.SourceVoiceId, track.SourceVoiceId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"MIDI channel program '{program.SourceVoiceId}' does not own track "
                        + $"source '{track.SourceVoiceId}'.");
                }
                program.Validate();
            }

            if (track.VoiceDomain is not MidiVoiceDomain domain)
                continue;

            if (track.ChannelProgram is not MidiChannelProgram ownedProgram)
                throw new InvalidOperationException(
                    $"MIDI voice domain '{domain.Source}' has no authoritative channel program.");

            MidiEndpoint domainEndpoint = new(domain.Port, domain.Channel);
            if (track.Endpoint != domainEndpoint)
                throw new InvalidOperationException(
                    $"MIDI track '{track.Name}' endpoint does not match its voice domain.");
            if (domainsByEndpoint.TryGetValue(track.Endpoint, out MidiVoiceDomain existing))
                existing.EnsureCompatible(domain);
            else
                domainsByEndpoint.Add(track.Endpoint, domain);

            if (ownedProgram.BendRange != domain.BendRange)
                throw new InvalidOperationException(
                    $"MIDI channel program '{ownedProgram.SourceVoiceId}' has bend range "
                    + $"{ownedProgram.BendRange}, expected {domain.BendRange}.");

            int rangeEvents = 0;
            int pitchBends = 0;
            int? emittedRange = null;
            if (track.UsesPackedEvents)
            {
                foreach (PackedMidiEvent evt in track.PackedEvents)
                {
                    if (evt.Kind == PackedMidiEventKind.BendRange)
                    {
                        rangeEvents++;
                        emittedRange = evt.A;
                    }
                    else if (evt.Kind == PackedMidiEventKind.PitchBend)
                    {
                        pitchBends++;
                    }
                }
            }
            else
            {
                foreach (MidiEventBase evt in track.Events)
                {
                    if (evt is MidiBendRangeEvent bendRangeEvent)
                    {
                        rangeEvents++;
                        emittedRange = bendRangeEvent.Semitones;
                    }
                    else if (evt is MidiPitchBendEvent)
                    {
                        pitchBends++;
                    }
                }
            }

            if (rangeEvents > 1)
                throw new InvalidOperationException(
                    $"MIDI voice domain '{domain.Source}' emits pitch-bend sensitivity more than once.");
            if (emittedRange is int configuredRange && configuredRange != domain.BendRange)
                throw new InvalidOperationException(
                    $"MIDI voice domain '{domain.Source}' emitted bend range {configuredRange}, "
                    + $"expected {domain.BendRange}.");
            if (pitchBends > 0 && rangeEvents != 1)
                throw new InvalidOperationException(
                    $"MIDI voice domain '{domain.Source}' emits pitch bends without one RPN range setup.");
        }
    }

    /// <summary>
    /// Writes the production packed IR directly as SMF bytes. This keeps the
    /// sequential delta/status/buffer state in this writer instead of creating
    /// one DryWetMIDI object per event. The compatibility writer above remains
    /// available for rich tracks supplied by tests or external callers.
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
        Array.Sort(ordered, CompareRichEvents);

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

    private static int CompareRichEvents(MidiEventBase left, MidiEventBase right)
        => MidiEventOrder.Compare(left, right);

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
            Array.Sort(copy, ComparePackedEvents);
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
            case PackedMidiEventKind.ControlChange:
                WriteControlChange(stream, delta, evt.Channel, evt.A, evt.B);
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
            case PackedMidiEventKind.Tuning:
                WritePackedTuning(stream, evt, delta);
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
        WriteControlChange(stream, delta, evt.Channel, 101, 0);
        WriteControlChange(stream, 0, evt.Channel, 100, 0);
        WriteControlChange(stream, 0, evt.Channel, 6, value);
        WriteControlChange(stream, 0, evt.Channel, 38, 0);
        WriteControlChange(stream, 0, evt.Channel, 101, 127);
        WriteControlChange(stream, 0, evt.Channel, 100, 127);
    }

    private static void WritePackedTuning(Stream stream, PackedMidiEvent evt, long delta)
    {
        if (evt.B is < -100 or > 100 || evt.A is < -12 or > 12
            || evt.A == 0 && evt.B == 0)
            throw new InvalidOperationException("Packed tuning values are outside the valid MIDI RPN range.");

        if (evt.A != 0)
        {
            int coarse = 0x2000 + evt.A;
            WriteControlChange(stream, delta, evt.Channel, 101, 0);
            WriteControlChange(stream, 0, evt.Channel, 100, 1);
            WriteControlChange(stream, 0, evt.Channel, 6, coarse >> 7);
            WriteControlChange(stream, 0, evt.Channel, 38, coarse & 0x7F);
        }
        else
        {
            int fine = Math.Clamp(0x2000 + (int)Math.Round(evt.B * 8192.0 / 100.0), 0, 0x3FFF);
            WriteControlChange(stream, delta, evt.Channel, 101, 0);
            WriteControlChange(stream, 0, evt.Channel, 100, 2);
            WriteControlChange(stream, 0, evt.Channel, 6, fine >> 7);
            WriteControlChange(stream, 0, evt.Channel, 38, fine & 0x7F);
        }
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

    /// <summary>
    /// Sorts a track's events deterministically, validates their absolute ticks,
    /// converts them to DryWetMIDI events and appends them to a track chunk. Deltas
    /// are computed here, never delegated to the library, so the absolute tick order
    /// and the equal-tick ordering (NoteOff before NoteOn for a retrigger) are fully
    /// controlled.
    /// </summary>
    private void AppendEvents(TrackChunk chunk, IReadOnlyList<MidiEventBase> events,
        bool canonical = false)
    {
        IEnumerable<MidiEventBase> ordered = events;
        if (!canonical)
            ordered = events.OrderBy(e => e, Comparer<MidiEventBase>.Create(MidiEventOrder.Compare));

        long previousTick = 0;
        foreach (MidiEventBase evt in ordered)
        {
            if (evt.Tick < 0)
                throw new InvalidOperationException("A MIDI event tick must be non-negative.");
            if (evt.Tick < previousTick)
                throw new InvalidOperationException("MIDI events must have non-decreasing ticks.");

            long delta = evt.Tick - previousTick;
            if (evt is MidiBendRangeEvent range)
            {
                AppendBendRange(chunk, range, delta);
            }
            else if (evt is MidiTuningEvent tuning)
            {
                AppendTuning(chunk, tuning, delta);
            }
            else
            {
                MidiEvent midiEvent = ConvertSingle(evt);
                midiEvent.DeltaTime = delta;
                chunk.Events.Add(midiEvent);
            }
            previousTick = evt.Tick;
        }
    }

    private void AppendPackedEvents(TrackChunk chunk, IReadOnlyList<PackedMidiEvent> events,
        bool canonical)
    {
        IReadOnlyList<PackedMidiEvent> ordered = events;
        if (!canonical)
        {
            var copy = new PackedMidiEvent[events.Count];
            for (int index = 0; index < events.Count; index++)
                copy[index] = events[index];
            Array.Sort(copy, ComparePackedEvents);
            ordered = copy;
        }

        long previousTick = 0;
        for (int index = 0; index < ordered.Count; index++)
        {
            PackedMidiEvent evt = ordered[index];
            if (evt.Tick < 0)
                throw new InvalidOperationException("A MIDI event tick must be non-negative.");
            if (evt.Tick < previousTick)
                throw new InvalidOperationException("MIDI events must have non-decreasing ticks.");

            long delta = evt.Tick - previousTick;
            switch (evt.Kind)
            {
                case PackedMidiEventKind.BendRange:
                    AppendPackedBendRange(chunk, evt, delta);
                    break;
                case PackedMidiEventKind.Tuning:
                    AppendPackedTuning(chunk, evt, delta);
                    break;
                default:
                    MidiEvent midiEvent = ConvertSingle(evt);
                    midiEvent.DeltaTime = delta;
                    chunk.Events.Add(midiEvent);
                    break;
            }
            previousTick = evt.Tick;
        }
    }

    private static int ComparePackedEvents(PackedMidiEvent left, PackedMidiEvent right)
        => MidiEventOrder.Compare(left, right);

    /// <summary>
    /// Converts one MDPlayer IR event to one or more DryWetMIDI events. Only the
    /// first generated event carries the source delta; subsequent events get delta 0.
    /// Unknown event types fail loudly rather than being silently dropped.
    /// </summary>
    private static MidiEvent ConvertSingle(MidiEventBase evt) => evt switch
    {
        MidiNoteEvent note => BuildNote(note),
        MidiProgramEvent program => BuildProgram(program),
        MidiBankEvent bank => BuildBank(bank),
        MidiControlChangeEvent controlChange => BuildControlChange(controlChange),
        MidiPitchBendEvent bend => BuildPitchBend(bend),
        MidiTempoEvent tempo => BuildTempo(tempo),
        MidiTimeSignatureEvent timeSignature => BuildTimeSignature(timeSignature),
        MidiMetaTextEvent text => BuildMetaText(text),
        MidiMarkerEvent marker => new MarkerEvent(marker.Name),
        _ => throw new InvalidOperationException(
            $"Unsupported MIDI event type {evt.GetType().Name} reached the SMF writer."),
    };

    private static MidiEvent ConvertSingle(PackedMidiEvent evt) => evt.Kind switch
    {
        PackedMidiEventKind.NoteOn => BuildPackedNote(evt, noteOn: true),
        PackedMidiEventKind.NoteOff => BuildPackedNote(evt, noteOn: false),
        PackedMidiEventKind.Program => new ProgramChangeEvent(
            new SevenBitNumber((byte)(evt.A & 0x7F))) { Channel = Channel(evt.Channel) },
        PackedMidiEventKind.Bank => new ControlChangeEvent(
            new SevenBitNumber(0), new SevenBitNumber((byte)(evt.A & 0x7F)))
            { Channel = Channel(evt.Channel) },
        PackedMidiEventKind.ControlChange => new ControlChangeEvent(
            new SevenBitNumber((byte)(evt.A & 0x7F)), new SevenBitNumber((byte)(evt.B & 0x7F)))
            { Channel = Channel(evt.Channel) },
        PackedMidiEventKind.PitchBend => BuildPackedPitchBend(evt),
        PackedMidiEventKind.Tempo => BuildPackedTempo(evt),
        PackedMidiEventKind.TimeSignature => BuildPackedTimeSignature(evt),
        _ => throw new InvalidOperationException($"Unsupported packed MIDI event kind {evt.Kind} reached the SMF writer."),
    };

    private static MidiEvent BuildPackedNote(PackedMidiEvent evt, bool noteOn)
    {
        var pitch = new SevenBitNumber((byte)(evt.A & 0x7F));
        var velocity = new SevenBitNumber((byte)(evt.B & 0x7F));
        return noteOn
            ? new NoteOnEvent(pitch, velocity) { Channel = Channel(evt.Channel) }
            : new NoteOffEvent(pitch, velocity) { Channel = Channel(evt.Channel) };
    }

    private static MidiEvent BuildPackedPitchBend(PackedMidiEvent evt)
    {
        int value = evt.A + 8192;
        if (value < 0 || value > 16383)
            throw new InvalidOperationException(
                $"Pitch-bend value {evt.A} is out of the valid [-8192, 8191] range.");
        return new PitchBendEvent((ushort)value) { Channel = Channel(evt.Channel) };
    }

    private static MidiEvent BuildPackedTempo(PackedMidiEvent evt) =>
        new SetTempoEvent(evt.A);

    private static MidiEvent BuildPackedTimeSignature(PackedMidiEvent evt) =>
        new TimeSignatureEvent((byte)evt.A, (byte)evt.B);

    private static FourBitNumber Channel(int channel) => new((byte)(channel & 0x0F));

    private static MidiEvent BuildNote(MidiNoteEvent note)
    {
        var channel = Channel(note.Channel);
        var pitch = new SevenBitNumber((byte)(note.Note & 0x7F));
        var velocity = new SevenBitNumber((byte)(note.Velocity & 0x7F));
        if (note.NoteOn)
            return new NoteOnEvent(pitch, velocity) { Channel = channel };
        return new NoteOffEvent(pitch, velocity) { Channel = channel };
    }

    private static MidiEvent BuildProgram(MidiProgramEvent program)
    {
        // Keep the 0-based program number; no GM remapping.
        var evt = new ProgramChangeEvent(new SevenBitNumber((byte)(program.Program & 0x7F)))
        {
            Channel = Channel(program.Channel),
        };
        return evt;
    }

    private static MidiEvent BuildBank(MidiBankEvent bank)
    {
        // Bank Select MSB only (controller 0), preserving the existing semantics.
        var evt = new ControlChangeEvent(new SevenBitNumber(0), new SevenBitNumber((byte)(bank.Bank & 0x7F)))
        {
            Channel = Channel(bank.Channel),
        };
        return evt;
    }

    private static MidiEvent BuildControlChange(MidiControlChangeEvent controlChange)
    {
        var evt = new ControlChangeEvent(
            new SevenBitNumber((byte)(controlChange.Control & 0x7F)),
            new SevenBitNumber((byte)(controlChange.Value & 0x7F)))
        {
            Channel = Channel(controlChange.Channel),
        };
        return evt;
    }

    private static MidiEvent BuildPitchBend(MidiPitchBendEvent bend)
    {
        int value = bend.Bend + 8192;
        if (value < 0 || value > 16383)
            throw new InvalidOperationException(
                $"Pitch-bend value {bend.Bend} is out of the valid [-8192, 8191] range.");
        var evt = new PitchBendEvent((ushort)value) { Channel = Channel(bend.Channel) };
        return evt;
    }

    private static void AppendBendRange(TrackChunk chunk, MidiBendRangeEvent range, long delta)
    {
        if (range.Semitones is < 0 or > 127)
            throw new InvalidOperationException(
                $"Pitch-bend range {range.Semitones} is outside the valid [0, 127] semitone range.");
        int value = range.Semitones;
        // RPN pitch-bend range (semitones): select RPN 0, write both data-entry
        // bytes, then null the RPN (CC101/CC100 = 127) to prevent later Data Entry
        // messages from changing channel sensitivity.
        // Only the first generated CC carries the source delta.
        var channel = Channel(range.Channel);
        AddControlChange(chunk, channel, 101, 0, ref delta);
        AddControlChange(chunk, channel, 100, 0, ref delta);
        AddControlChange(chunk, channel, 6, value, ref delta);
        AddControlChange(chunk, channel, 38, 0, ref delta);
        AddControlChange(chunk, channel, 101, 127, ref delta);
        AddControlChange(chunk, channel, 100, 127, ref delta);
    }

    private static void AppendPackedBendRange(TrackChunk chunk, PackedMidiEvent range, long delta)
    {
        if (range.A is < 0 or > 127)
            throw new InvalidOperationException(
                $"Pitch-bend range {range.A} is outside the valid [0, 127] semitone range.");
        int value = range.A;
        var channel = Channel(range.Channel);
        AddControlChange(chunk, channel, 101, 0, ref delta);
        AddControlChange(chunk, channel, 100, 0, ref delta);
        AddControlChange(chunk, channel, 6, value, ref delta);
        AddControlChange(chunk, channel, 38, 0, ref delta);
        AddControlChange(chunk, channel, 101, 127, ref delta);
        AddControlChange(chunk, channel, 100, 127, ref delta);
    }

    /// <summary>
    /// RPN channel tuning: fine = RPN 0x0002 with a 14-bit data entry centered at
    /// 0x2000 (±100c → [0x0000, 0x3FFF]); coarse = RPN 0x0001 with the semitone
    /// count as the same 14-bit center when |bias| exceeds ±100c. Both paths end
    /// with the null RPN unselect so the tuning setup is self-contained and
    /// conflict-free with the bend-range RPN (D10). Only the first generated CC
    /// carries the source delta.
    /// </summary>
    private static void AppendTuning(TrackChunk chunk, MidiTuningEvent tuning, long delta)
    {
        if (tuning.FineCents is < -100 or > 100)
            throw new InvalidOperationException(
                $"Fine tuning {tuning.FineCents}c is outside the valid [-100, 100] range.");
        if (tuning.CoarseSemitones is < -12 or > 12)
            throw new InvalidOperationException(
                $"Coarse tuning {tuning.CoarseSemitones} st is outside the valid [-12, 12] range.");
        if (tuning.CoarseSemitones == 0 && tuning.FineCents == 0)
            throw new InvalidOperationException("A tuning event must change something.");

        var channel = Channel(tuning.Channel);
        if (tuning.CoarseSemitones != 0)
        {
            int coarse = 0x2000 + tuning.CoarseSemitones;
            AddControlChange(chunk, channel, 101, 0, ref delta);
            AddControlChange(chunk, channel, 100, 1, ref delta);
            AddControlChange(chunk, channel, 6, coarse >> 7, ref delta);
            AddControlChange(chunk, channel, 38, coarse & 0x7F, ref delta);
            AddControlChange(chunk, channel, 101, 127, ref delta);
            AddControlChange(chunk, channel, 100, 127, ref delta);
            return;
        }

        int fine = 0x2000 + (int)Math.Round(tuning.FineCents * 8192.0 / 100.0);
        fine = Math.Clamp(fine, 0, 0x3FFF);
        AddControlChange(chunk, channel, 101, 0, ref delta);
        AddControlChange(chunk, channel, 100, 2, ref delta);
        AddControlChange(chunk, channel, 6, fine >> 7, ref delta);
        AddControlChange(chunk, channel, 38, fine & 0x7F, ref delta);
        AddControlChange(chunk, channel, 101, 127, ref delta);
        AddControlChange(chunk, channel, 100, 127, ref delta);
    }

    private static void AppendPackedTuning(TrackChunk chunk, PackedMidiEvent tuning, long delta)
    {
        if (tuning.B is < -100 or > 100)
            throw new InvalidOperationException(
                $"Fine tuning {tuning.B}c is outside the valid [-100, 100] range.");
        if (tuning.A is < -12 or > 12)
            throw new InvalidOperationException(
                $"Coarse tuning {tuning.A} st is outside the valid [-12, 12] range.");
        if (tuning.A == 0 && tuning.B == 0)
            throw new InvalidOperationException("A tuning event must change something.");

        var channel = Channel(tuning.Channel);
        if (tuning.A != 0)
        {
            int coarse = 0x2000 + tuning.A;
            AddControlChange(chunk, channel, 101, 0, ref delta);
            AddControlChange(chunk, channel, 100, 1, ref delta);
            AddControlChange(chunk, channel, 6, coarse >> 7, ref delta);
            AddControlChange(chunk, channel, 38, coarse & 0x7F, ref delta);
            AddControlChange(chunk, channel, 101, 127, ref delta);
            AddControlChange(chunk, channel, 100, 127, ref delta);
            return;
        }

        int fine = 0x2000 + (int)Math.Round(tuning.B * 8192.0 / 100.0);
        fine = Math.Clamp(fine, 0, 0x3FFF);
        AddControlChange(chunk, channel, 101, 0, ref delta);
        AddControlChange(chunk, channel, 100, 2, ref delta);
        AddControlChange(chunk, channel, 6, fine >> 7, ref delta);
        AddControlChange(chunk, channel, 38, fine & 0x7F, ref delta);
        AddControlChange(chunk, channel, 101, 127, ref delta);
        AddControlChange(chunk, channel, 100, 127, ref delta);
    }

    private static void AddControlChange(
        TrackChunk chunk, FourBitNumber channel, int control, int value, ref long delta)
    {
        var evt = new ControlChangeEvent(
            new SevenBitNumber((byte)control), new SevenBitNumber((byte)value))
        {
            Channel = channel,
            DeltaTime = delta,
        };
        chunk.Events.Add(evt);
        delta = 0;
    }

    private static MidiEvent BuildTempo(MidiTempoEvent tempo)
    {
        int us = tempo.MicrosecondsPerQuarter;
        if (us is < 1 or > 0xFFFFFF)
            throw new InvalidOperationException(
                $"Set Tempo microseconds-per-quarter {us} is out of the valid [1, 0xFFFFFF] range.");
        return new SetTempoEvent(us);
    }

    private static MidiEvent BuildTimeSignature(MidiTimeSignatureEvent timeSignature)
    {
        if (timeSignature.Numerator is < 1 or > byte.MaxValue)
            throw new InvalidOperationException(
                $"Time-signature numerator {timeSignature.Numerator} is out of the valid [1, 255] range.");
        int denominator = timeSignature.Denominator;
        if (denominator < 1 || denominator > 256 || (denominator & (denominator - 1)) != 0)
            throw new InvalidOperationException(
                $"Time-signature denominator {denominator} must be a power of two in [1, 256].");
        var evt = new TimeSignatureEvent((byte)timeSignature.Numerator, (byte)denominator);
        return evt;
    }

    private static MidiEvent BuildMetaText(MidiMetaTextEvent text)
    {
        MidiEvent evt = text.Type switch
        {
            0x01 => new TextEvent(text.Text),
            0x02 => new CopyrightNoticeEvent(text.Text),
            0x03 => new SequenceTrackNameEvent(text.Text),
            0x04 => new InstrumentNameEvent(text.Text),
            0x05 => new LyricEvent(text.Text),
            0x06 => new MarkerEvent(text.Text),
            0x07 => new CuePointEvent(text.Text),
            0x08 => new ProgramNameEvent(text.Text),
            _ => throw new InvalidOperationException(
                $"Unsupported meta-text event type 0x{text.Type:X2}."),
        };
        return evt;
    }
}
