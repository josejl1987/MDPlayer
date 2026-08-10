#nullable enable

using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;

namespace Fmp.Core.Midi;

/// <summary>The content of one MIDI track: its name and ordered musical events.</summary>
internal readonly record struct MidiEndpoint(byte Port, int Channel);

internal sealed class MidiTrack
{
    public required string Name { get; init; }

    public required MidiEndpoint Endpoint { get; init; }

    public List<MidiEventBase> Events { get; } = new();
}

/// <summary>
/// Standard MIDI file (SMF) Format 1 serializer built as a thin adapter over the
/// Melanchall.DryWetMidi library, driven by a
/// <see cref="Fmp.Core.Timing.MusicalTimeMap"/>-derived event stream. Track 0 is
/// the conductor track (tempo, time signature, markers, metadata); every other
/// track holds one logical voice. Events are ordered deterministically within a
/// tick (by tick, then <see cref="MidiEventOrder.Rank"/>, then
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
            AppendEvents(chunk, track.Events);
            file.Chunks.Add(chunk);
        }

        using var stream = new MemoryStream();
        file.Write(stream, MidiFileFormat.MultiTrack, new WritingSettings());
        return stream.ToArray();
    }

    /// <summary>
    /// Sorts a track's events deterministically, validates their absolute ticks,
    /// converts them to DryWetMIDI events and appends them to a track chunk. Deltas
    /// are computed here, never delegated to the library, so the absolute tick order
    /// and the equal-tick ordering (NoteOff before NoteOn for a retrigger) are fully
    /// controlled.
    /// </summary>
    private void AppendEvents(TrackChunk chunk, IReadOnlyList<MidiEventBase> events)
    {
        var ordered = events
            .OrderBy(e => e.Tick)
            .ThenBy(MidiEventOrder.Rank)
            .ThenBy(e => e.SourceOrder)
            .ToList();

        long previousTick = 0;
        foreach (MidiEventBase evt in ordered)
        {
            if (evt.Tick < 0)
                throw new InvalidOperationException("A MIDI event tick must be non-negative.");
            if (evt.Tick < previousTick)
                throw new InvalidOperationException("MIDI events must have non-decreasing ticks.");

            long delta = evt.Tick - previousTick;
            bool firstGenerated = true;
            foreach (MidiEvent midiEvent in Convert(evt))
            {
                midiEvent.DeltaTime = firstGenerated ? delta : 0;
                chunk.Events.Add(midiEvent);
                firstGenerated = false;
            }
            previousTick = evt.Tick;
        }
    }

    /// <summary>
    /// Converts one MDPlayer IR event to one or more DryWetMIDI events. Only the
    /// first generated event carries the source delta; subsequent events get delta 0.
    /// Unknown event types fail loudly rather than being silently dropped.
    /// </summary>
    private static IEnumerable<MidiEvent> Convert(MidiEventBase evt) => evt switch
    {
        MidiNoteEvent note => BuildNote(note),
        MidiProgramEvent program => BuildProgram(program),
        MidiBankEvent bank => BuildBank(bank),
        MidiPitchBendEvent bend => BuildPitchBend(bend),
        MidiBendRangeEvent range => BuildBendRange(range),
        MidiTempoEvent tempo => BuildTempo(tempo),
        MidiTimeSignatureEvent timeSignature => BuildTimeSignature(timeSignature),
        MidiMetaTextEvent text => BuildMetaText(text),
        MidiMarkerEvent marker => new MidiEvent[] { new MarkerEvent(marker.Name) },
        _ => throw new InvalidOperationException(
            $"Unsupported MIDI event type {evt.GetType().Name} reached the SMF writer."),
    };

    private static FourBitNumber Channel(int channel) => new((byte)(channel & 0x0F));

    private static IEnumerable<MidiEvent> BuildNote(MidiNoteEvent note)
    {
        var channel = Channel(note.Channel);
        var pitch = new SevenBitNumber((byte)(note.Note & 0x7F));
        var velocity = new SevenBitNumber((byte)(note.Velocity & 0x7F));
        if (note.NoteOn)
            return new MidiEvent[] { new NoteOnEvent(pitch, velocity) { Channel = channel } };
        return new MidiEvent[] { new NoteOffEvent(pitch, velocity) { Channel = channel } };
    }

    private static IEnumerable<MidiEvent> BuildProgram(MidiProgramEvent program)
    {
        // Keep the 0-based program number; no GM remapping.
        var evt = new ProgramChangeEvent(new SevenBitNumber((byte)(program.Program & 0x7F)))
        {
            Channel = Channel(program.Channel),
        };
        return new MidiEvent[] { evt };
    }

    private static IEnumerable<MidiEvent> BuildBank(MidiBankEvent bank)
    {
        // Bank Select MSB only (controller 0), preserving the existing semantics.
        var evt = new ControlChangeEvent(new SevenBitNumber(0), new SevenBitNumber((byte)(bank.Bank & 0x7F)))
        {
            Channel = Channel(bank.Channel),
        };
        return new MidiEvent[] { evt };
    }

    private static IEnumerable<MidiEvent> BuildPitchBend(MidiPitchBendEvent bend)
    {
        int value = bend.Bend + 8192;
        if (value < 0 || value > 16383)
            throw new InvalidOperationException(
                $"Pitch-bend value {bend.Bend} is out of the valid [-8192, 8191] range.");
        var evt = new PitchBendEvent((ushort)value) { Channel = Channel(bend.Channel) };
        return new MidiEvent[] { evt };
    }

    private static IEnumerable<MidiEvent> BuildBendRange(MidiBendRangeEvent range)
    {
        int value = range.Semitones & 0x7F;
        // RPN pitch-bend range (semitones): select RPN 0, data entry MSB, then the
        // null RPN to unselect. Only the first generated CC carries the source delta.
        var channel = Channel(range.Channel);
        (int Control, int Value)[] writes =
        {
            (101, 0), (100, 0), (6, value),     // select RPN 0 + data entry msb
            (101, 127), (100, 127), (6, 0),     // null RPN (unselect)
        };
        return writes.Select(w => new ControlChangeEvent(
                new SevenBitNumber((byte)w.Control), new SevenBitNumber((byte)w.Value))
            { Channel = channel }).ToArray();
    }

    private static IEnumerable<MidiEvent> BuildTempo(MidiTempoEvent tempo)
    {
        int us = tempo.MicrosecondsPerQuarter;
        if (us is < 1 or > 0xFFFFFF)
            throw new InvalidOperationException(
                $"Set Tempo microseconds-per-quarter {us} is out of the valid [1, 0xFFFFFF] range.");
        return new MidiEvent[] { new SetTempoEvent(us) };
    }

    private static IEnumerable<MidiEvent> BuildTimeSignature(MidiTimeSignatureEvent timeSignature)
    {
        if (timeSignature.Numerator is < 1 or > byte.MaxValue)
            throw new InvalidOperationException(
                $"Time-signature numerator {timeSignature.Numerator} is out of the valid [1, 255] range.");
        int denominator = timeSignature.Denominator;
        if (denominator < 1 || denominator > 256 || (denominator & (denominator - 1)) != 0)
            throw new InvalidOperationException(
                $"Time-signature denominator {denominator} must be a power of two in [1, 256].");
        var evt = new TimeSignatureEvent((byte)timeSignature.Numerator, (byte)denominator);
        return new MidiEvent[] { evt };
    }

    private static IEnumerable<MidiEvent> BuildMetaText(MidiMetaTextEvent text)
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
        return new MidiEvent[] { evt };
    }
}
