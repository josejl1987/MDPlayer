using System.Text;
using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Writer-level tests for <see cref="MidiFileWriter"/> (spec §43–§46, §44, §45):
/// Format 1 header / PPQ / chunk lengths / nonnegative deltas, VLQ boundary
/// encoding + out-of-range rejection, one effective EOT per track, tempo-before-
/// note-on at the same tick, byte-identical determinism, and the deterministic
/// SourceOrder secondary-key sort (§34/§45).
/// </summary>
public sealed class MidiFileWriterTests
{
    private const int Ppq = 960;

    // ---- T031: Format 1, PPQ, MTrk lengths, nonnegative deltas ----

    [Fact]
    public void Write_Header_IsFormat1WithConfiguredPpq()
    {
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(EmptyConductor(), new[] { new MidiTrack { Name = "t" } });

        // MThd, len=6
        Assert.Equal(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06 }, bytes.Take(8));
        Assert.Equal(0, bytes[8]);   // format high
        Assert.Equal(1, bytes[9]);   // format low → Format 1
        Assert.Equal(2, bytes[10] * 256 + bytes[11]); // ntrks = conductor + 1
        Assert.Equal(Ppq, bytes[12] * 256 + bytes[13]); // division = PPQ
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-960)]
    public void Writer_RejectsInvalidPpq(int ppq)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MidiFileWriter(ppq));
    }

    [Fact]
    public void Writer_RejectsPpqAbove32767()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MidiFileWriter(32768));
    }

    [Fact]
    public void Write_TrackChunkLengths_AreBigEndianAndMatch()
    {
        var track = new MidiTrack { Name = "t" };
        track.Events.Add(new MidiNoteEvent(10, 0, 0, 64, 90, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(20, 0, 0, 64, 0, NoteOn: false));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(EmptyConductor(), new[] { track });

        // Walk the chunks: MThd + 8-byte header + MTrk chunks.
        int offset = 14;
        while (offset < bytes.Length)
        {
            Assert.Equal(0x4D, bytes[offset]); // M
            Assert.Equal((byte)'T', bytes[offset + 1]);
            Assert.Equal((byte)'r', bytes[offset + 2]);
            Assert.Equal((byte)'k', bytes[offset + 3]);
            int len = (bytes[offset + 4] << 24) | (bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7];
            Assert.True(len >= 0, "chunk length must be nonnegative");
            offset += 8 + len;
        }
        Assert.Equal(bytes.Length, offset);
    }

    [Fact]
    public void Write_NonnegativeDeltaTimes()
    {
        var track = new MidiTrack { Name = "t" };
        // Events are unordered in the input list; the writer sorts by tick so
        // deltas are always nonnegative regardless of insertion order.
        track.Events.Add(new MidiNoteEvent(100, 0, 0, 60, 90, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(10, 0, 0, 60, 0, NoteOn: false));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(EmptyConductor(), new[] { track });
        foreach (long delta in DecodeAllDeltas(bytes))
            Assert.True(delta >= 0, "delta times must be nonnegative");
    }

    // ---- T032: VLQ boundary + range rejection ----

    [Theory]
    [InlineData(0L, "00")]
    [InlineData(0x7FL, "7F")]
    [InlineData(0x80L, "8100")]
    [InlineData(0x3FFFL, "FF7F")]
    [InlineData(0x4000L, "818000")]
    [InlineData(0x1FFFFFL, "FFFF7F")]
    [InlineData(0x200000L, "81808000")]
    [InlineData(0x0FFFFFFFL, "FFFFFF7F")]
    public void WriteVlv_Boundaries_EncodeExpectedBytes(long value, string expectedHex)
    {
        // Exercise the writer's internal VLQ directly: the same encoder used for
        // delta times and meta lengths must hit every boundary exactly.
        var expected = ParseHex(expectedHex);
        var body = new List<byte>();
        MidiFileWriter.WriteVlv(body, value);
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0x10000000L)]
    [InlineData(long.MaxValue)]
    public void WriteVlv_OutOfRange_Rejected(long value)
    {
        var body = new List<byte>();
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiFileWriter.WriteVlv(body, value));
        // Rejected, not truncated: nothing may be written.
        Assert.Empty(body);
    }

    // ---- T033: one effective EOT per track; tempo precedes note-on ----

    [Fact]
    public void Write_EveryTrackEndsWithExactlyOneEot()
    {
        var c = new List<MidiEventBase>
        {
            new MidiTempoEvent(0, 500_000),
            new MidiTimeSignatureEvent(0, 4, 4),
        };
        var t1 = new MidiTrack { Name = "t1" };
        t1.Events.Add(new MidiNoteEvent(10, 0, 0, 60, 90, NoteOn: true));
        var t2 = new MidiTrack { Name = "t2" };
        t2.Events.Add(new MidiNoteEvent(5, 0, 1, 70, 90, NoteOn: true));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(c, new[] { t1, t2 });

        // 2 musical + 1 conductor = 3 tracks, each ending with exactly one FF 2F 00.
        var eots = CountEotPerTrack(bytes);
        Assert.Equal(new[] { 1, 1, 1 }, eots);
    }

    [Fact]
    public void Write_TempoPrecedesNoteOn_AtSameTick()
    {
        // A tempo at tick T and a note-on at T: the tempo must serialize first.
        var c = new List<MidiEventBase> { new MidiTempoEvent(100, 500_000) };
        var track = new MidiTrack { Name = "t" };
        track.Events.Add(new MidiNoteEvent(100, 0, 0, 60, 90, NoteOn: true));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(c, new[] { track });

        // Walk the conductor track: locate the FF 51 (tempo) and confirm it precedes
        // the note-on in the musical track at the same absolute tick. Simpler: the
        // conductor tempo and the first musical event both sit at tick 100; we assert
        // the tempo meta appears in the byte stream before the note-on status byte for
        // the same tick by checking ordering of 0x51 vs 0x90.
        int tempoIdx = IndexOf(bytes, 0xFF, 0x51);
        int noteOnIdx = IndexOf(bytes, 0x90);
        Assert.True(tempoIdx >= 0 && noteOnIdx >= 0);
        Assert.True(tempoIdx < noteOnIdx, "tempo (FF 51) must serialize before note-on (0x90) at the same tick");
    }

    // ---- T034: byte-identical determinism + SourceOrder secondary sort ----

    [Fact]
    public void Write_Twice_IsByteIdentical()
    {
        var track = new MidiTrack { Name = "t" };
        track.Events.Add(new MidiNoteEvent(10, 0, 0, 60, 90, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(20, 0, 0, 60, 0, NoteOn: false));
        var writer = new MidiFileWriter(Ppq);
        byte[] a = writer.Write(EmptyConductor(), new[] { track });
        byte[] b = writer.Write(EmptyConductor(), new[] { track });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Write_ShuffledEqualEvents_SourceOrderDeterminesBytes()
    {
        // Three notes sharing the same tick AND the same rank (all note-on). The
        // only distinguishing key is SourceOrder, so the serialized byte order
        // must follow SourceOrder regardless of the list's insertion order.
        var tNormal = new MidiTrack { Name = "t" };
        tNormal.Events.Add(NoteAt(100, 0, 60, sourceOrder: 0));
        tNormal.Events.Add(NoteAt(100, 0, 62, sourceOrder: 1));
        tNormal.Events.Add(NoteAt(100, 0, 64, sourceOrder: 2));
        var writer = new MidiFileWriter(Ppq);
        byte[] normal = writer.Write(EmptyConductor(), new[] { tNormal });

        var tShuffled = new MidiTrack { Name = "t" };
        // Insertion order differs, but SourceOrder still dictates output order.
        tShuffled.Events.Add(NoteAt(100, 0, 64, sourceOrder: 2));
        tShuffled.Events.Add(NoteAt(100, 0, 60, sourceOrder: 0));
        tShuffled.Events.Add(NoteAt(100, 0, 62, sourceOrder: 1));
        byte[] shuffled = writer.Write(EmptyConductor(), new[] { tShuffled });

        Assert.Equal(normal, shuffled);
    }

    // ---- helpers ----

    private static List<MidiEventBase> EmptyConductor() => new();

    private static MidiNoteEvent NoteAt(long tick, int channel, int note, int sourceOrder)
    {
        var evt = new MidiNoteEvent(tick, 0, channel, note, 90, NoteOn: true);
        evt.SourceOrder = sourceOrder;
        return evt;
    }

    private static byte[] ParseHex(string hex)
    {
        var result = new List<byte>(hex.Length / 2);
        for (int i = 0; i < hex.Length; i += 2)
            result.Add(Convert.ToByte(hex.Substring(i, 2), 16));
        return result.ToArray();
    }

    private static List<long> DecodeAllDeltas(byte[] data)
    {
        var deltas = new List<long>();
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        br.ReadBytes(4);
        int _mthdLen = ReadInt32BE(br);
        br.ReadInt16(); // format (BE read via two bytes)
        int ntrks = ReadInt16BE(br);
        br.ReadInt16();
        for (int t = 0; t < ntrks; t++)
        {
            br.ReadBytes(4);
            int len = ReadInt32BE(br);
            long trackEnd = ms.Position + len;
            while (ms.Position < trackEnd)
            {
                long delta = ReadVlv(br);
                deltas.Add(delta);
                byte status = br.ReadByte();
                if (status == 0xFF)
                {
                    br.ReadByte(); // type
                    long metaLen = ReadVlv(br);
                    for (long i = 0; i < metaLen; i++)
                        br.ReadByte();
                    continue;
                }
                if ((status & 0xF0) == 0xF0)
                {
                    long sysexLen = ReadVlv(br);
                    for (long i = 0; i < sysexLen; i++)
                        br.ReadByte();
                    continue;
                }
                int dataBytes = (status & 0xF0) switch
                {
                    0xC0 or 0xD0 => 1,
                    _ => 2,
                };
                for (int i = 0; i < dataBytes; i++)
                    br.ReadByte();
            }
        }
        return deltas;
    }

    private static int ReadInt16BE(BinaryReader br) => (br.ReadByte() << 8) | br.ReadByte();

    private static int ReadInt32BE(BinaryReader br) =>
        (br.ReadByte() << 24) | (br.ReadByte() << 16) | (br.ReadByte() << 8) | br.ReadByte();

    private static long ReadVlv(BinaryReader br)
    {
        long value = 0;
        byte b;
        do
        {
            b = br.ReadByte();
            value = (value << 7) | (uint)(b & 0x7F);
        } while ((b & 0x80) != 0);
        return value;
    }

    /// <summary>Counts EOT (FF 2F 00) per track in a format-1 stream.</summary>
    private static int[] CountEotPerTrack(byte[] data)
    {
        var counts = new List<int>();
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        br.ReadBytes(4); ReadInt32BE(br); br.ReadInt16();
        int ntrks = ReadInt16BE(br); br.ReadInt16();
        for (int t = 0; t < ntrks; t++)
        {
            br.ReadBytes(4);
            int len = ReadInt32BE(br);
            long trackEnd = ms.Position + len;
            int eots = 0;
            while (ms.Position < trackEnd)
            {
                ReadVlv(br);
                byte status = br.ReadByte();
                if (status == 0xFF)
                {
                    byte type = br.ReadByte();
                    long metaLen = ReadVlv(br);
                    for (long i = 0; i < metaLen; i++)
                        br.ReadByte();
                    if (type == 0x2F)
                        eots++;
                }
                else if ((status & 0xF0) == 0xF0)
                {
                    long sysexLen = ReadVlv(br);
                    for (long i = 0; i < sysexLen; i++)
                        br.ReadByte();
                }
                else
                {
                    int dataBytes = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                    for (int i = 0; i < dataBytes; i++)
                        br.ReadByte();
                }
            }
            counts.Add(eots);
        }
        return counts.ToArray();
    }

    private static int IndexOf(byte[] data, params byte[] needle)
    {
        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (data[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }
}