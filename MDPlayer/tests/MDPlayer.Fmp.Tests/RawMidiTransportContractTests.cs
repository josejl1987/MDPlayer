using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Xunit;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Serialized-SMF transport contract tests. The parser below deliberately reads
/// bytes directly so a DryWetMIDI object model cannot hide metadata written by the
/// exporter or move a tempo event to a nonzero absolute tick.
/// </summary>
public sealed class RawMidiTransportContractTests
{
    private const int SampleRate = 44100;
    private const int Ppq = 960;

    [Fact]
    public void SerializedSmf_UsesFixedTransport_AndContainsNoMusicalMetadata()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate,
            Notes = new[]
            {
                new NoteEvent(
                    ChannelId: "voice-0",
                    StartSample: 0,
                    EndSample: SampleRate,
                    InitialFrequencyHz: 0,
                    InitialMidiNote: 60,
                    InstrumentId: "test",
                    Mode: VisualizationNoteMode.Fm,
                    IsRetrigger: false,
                    Pitch: Array.Empty<PitchChange>()),
            },
        };

        byte[] bytes = new MidiTranscriber(Ppq).Transcribe(timeline).Bytes;
        SerializedSmf smf = SerializedSmf.Parse(bytes);

        Assert.Equal((ushort)1, smf.Format);
        Assert.Equal((short)Ppq, smf.Division);
        Assert.Equal((ushort)2, smf.TrackCount);

        SerializedMetaEvent[] tempos = smf.MetaEvents
            .Where(e => e.Type == (byte)MetaType.SetTempo)
            .ToArray();
        var tempo = Assert.Single(tempos);
        Assert.Equal(0, tempo.AbsoluteTick);
        Assert.Equal(500_000, ReadThreeByteBigEndian(tempo.Data));
        Assert.DoesNotContain(tempos, e => e.AbsoluteTick != 0);

        Assert.DoesNotContain(smf.MetaEvents, e => (MetaType)e.Type is
            MetaType.TimeSignature or
            MetaType.KeySignature or
            MetaType.Marker or
            MetaType.CuePoint or
            MetaType.Lyrics or
            MetaType.GenericText or
            MetaType.SequencerSpecific or
            MetaType.SmpteOffset);

        // MIDI Time Code quarter-frame messages are SMPTE timing events too;
        // reject them independently of the FF 54 SMPTE Offset meta event above.
        Assert.DoesNotContain(smf.SystemEvents, e => e.Status == 0xF1);
    }

    private static int ReadThreeByteBigEndian(byte[] bytes)
    {
        Assert.Equal(3, bytes.Length);
        return (bytes[0] << 16) | (bytes[1] << 8) | bytes[2];
    }

    private enum MetaType : byte
    {
        GenericText = 0x01,
        Lyrics = 0x05,
        Marker = 0x06,
        CuePoint = 0x07,
        SetTempo = 0x51,
        SmpteOffset = 0x54,
        TimeSignature = 0x58,
        KeySignature = 0x59,
        SequencerSpecific = 0x7F,
    }

    private readonly record struct SerializedMetaEvent(byte Type, long AbsoluteTick, byte[] Data);

    private readonly record struct SerializedSystemEvent(byte Status, long AbsoluteTick);

    private sealed class SerializedSmf
    {
        private SerializedSmf(
            ushort format,
            ushort trackCount,
            short division,
            IReadOnlyList<SerializedMetaEvent> metaEvents,
            IReadOnlyList<SerializedSystemEvent> systemEvents)
        {
            Format = format;
            TrackCount = trackCount;
            Division = division;
            MetaEvents = metaEvents;
            SystemEvents = systemEvents;
        }

        public ushort Format { get; }
        public ushort TrackCount { get; }
        public short Division { get; }
        public IReadOnlyList<SerializedMetaEvent> MetaEvents { get; }
        public IReadOnlyList<SerializedSystemEvent> SystemEvents { get; }

        public static SerializedSmf Parse(byte[] bytes)
        {
            var reader = new ByteReader(bytes);
            reader.ExpectAscii("MThd");
            uint headerLength = reader.ReadUInt32();
            if (headerLength != 6)
                throw new InvalidDataException("SMF header length must be six bytes.");

            ushort format = reader.ReadUInt16();
            ushort trackCount = reader.ReadUInt16();
            short division = unchecked((short)reader.ReadUInt16());
            var metaEvents = new List<SerializedMetaEvent>();
            var systemEvents = new List<SerializedSystemEvent>();

            for (int track = 0; track < trackCount; track++)
            {
                reader.ExpectAscii("MTrk");
                uint trackLength = reader.ReadUInt32();
                if (trackLength > int.MaxValue || trackLength > reader.Remaining)
                    throw new InvalidDataException("SMF track extends past the serialized bytes.");

                int trackEnd = checked(reader.Position + (int)trackLength);
                ParseTrack(reader, trackEnd, metaEvents, systemEvents);
                if (reader.Position != trackEnd)
                    throw new InvalidDataException("SMF track parser did not consume its chunk.");
            }

            if (reader.Remaining != 0)
                throw new InvalidDataException("Trailing bytes follow the declared SMF tracks.");

            return new SerializedSmf(format, trackCount, division, metaEvents, systemEvents);
        }

        private static void ParseTrack(
            ByteReader reader,
            int trackEnd,
            List<SerializedMetaEvent> metaEvents,
            List<SerializedSystemEvent> systemEvents)
        {
            long absoluteTick = 0;
            byte runningStatus = 0;

            while (reader.Position < trackEnd)
            {
                uint delta = reader.ReadVlq(trackEnd);
                absoluteTick = checked(absoluteTick + delta);

                byte statusOrData = reader.ReadByte(trackEnd);
                byte status;
                if (statusOrData < 0x80)
                {
                    if (runningStatus == 0)
                        throw new InvalidDataException("SMF data byte has no running status.");
                    status = runningStatus;
                    reader.Rewind();
                }
                else
                {
                    status = statusOrData;
                    if (status is >= 0x80 and <= 0xEF)
                        runningStatus = status;
                    else if (status is not (>= 0xF8 and <= 0xFE))
                        runningStatus = 0;
                }

                if (status == 0xFF)
                {
                    byte type = reader.ReadByte(trackEnd);
                    int length = checked((int)reader.ReadVlq(trackEnd));
                    byte[] data = reader.ReadBytes(length, trackEnd);
                    metaEvents.Add(new SerializedMetaEvent(type, absoluteTick, data));
                    continue;
                }

                if (status is 0xF0 or 0xF7)
                {
                    int length = checked((int)reader.ReadVlq(trackEnd));
                    reader.Skip(length, trackEnd);
                    continue;
                }

                if (status >= 0xF0)
                {
                    int dataBytes = status switch
                    {
                        0xF1 or 0xF3 => 1,
                        0xF2 => 2,
                        0xF4 or 0xF5 or 0xF6 or 0xF8 or 0xF9 or 0xFA or 0xFB or
                            0xFC or 0xFD or 0xFE => 0,
                        _ => throw new InvalidDataException($"Unsupported SMF system status 0x{status:X2}."),
                    };
                    if (status == 0xF1)
                        systemEvents.Add(new SerializedSystemEvent(status, absoluteTick));
                    reader.Skip(dataBytes, trackEnd);
                    continue;
                }

                int channelDataBytes = (status & 0xE0) == 0xC0 ? 1 : 2;
                reader.Skip(channelDataBytes, trackEnd);
            }
        }
    }

    private sealed class ByteReader
    {
        private readonly byte[] _bytes;
        private int _position;

        public ByteReader(byte[] bytes) => _bytes = bytes;

        public int Position => _position;
        public int Remaining => _bytes.Length - _position;

        public byte ReadByte(int limit)
        {
            if (_position >= limit || _position >= _bytes.Length)
                throw new InvalidDataException("Unexpected end of SMF chunk.");
            return _bytes[_position++];
        }

        public ushort ReadUInt16()
        {
            Ensure(2);
            ushort result = (ushort)((_bytes[_position] << 8) | _bytes[_position + 1]);
            _position += 2;
            return result;
        }

        public uint ReadUInt32()
        {
            Ensure(4);
            uint result = ((uint)_bytes[_position] << 24)
                | ((uint)_bytes[_position + 1] << 16)
                | ((uint)_bytes[_position + 2] << 8)
                | _bytes[_position + 3];
            _position += 4;
            return result;
        }

        public uint ReadVlq(int limit)
        {
            uint value = 0;
            for (int count = 0; count < 4; count++)
            {
                byte next = ReadByte(limit);
                value = (value << 7) | (uint)(next & 0x7F);
                if ((next & 0x80) == 0)
                    return value;
            }

            throw new InvalidDataException("SMF variable-length quantity exceeds four bytes.");
        }

        public byte[] ReadBytes(int count, int limit)
        {
            if (count < 0)
                throw new InvalidDataException("Negative SMF byte count.");
            Ensure(count, limit);
            byte[] result = new byte[count];
            Array.Copy(_bytes, _position, result, 0, count);
            _position += count;
            return result;
        }

        public void Skip(int count, int limit)
        {
            if (count < 0)
                throw new InvalidDataException("Negative SMF skip count.");
            Ensure(count, limit);
            _position += count;
        }

        public void Rewind()
        {
            if (_position == 0)
                throw new InvalidDataException("SMF running-status rewind underflow.");
            _position--;
        }

        public void ExpectAscii(string value)
        {
            Ensure(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (_bytes[_position + i] != value[i])
                    throw new InvalidDataException($"Expected SMF chunk {value}.");
            }
            _position += value.Length;
        }

        private void Ensure(int count)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException("Unexpected end of serialized SMF.");
        }

        private void Ensure(int count, int limit)
        {
            if (count < 0 || _position > limit || count > limit - _position || count > Remaining)
                throw new InvalidDataException("SMF event extends past its track chunk.");
        }
    }
}
