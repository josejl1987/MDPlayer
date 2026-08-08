#nullable enable

using System.Text;

namespace Fmp.Core.Midi;

/// <summary>The content of one MIDI track: its name and ordered musical events.</summary>
internal sealed class MidiTrack
{
    public required string Name { get; init; }

    public List<MidiEventBase> Events { get; } = new();
}

/// <summary>
/// Standard MIDI file (SMF) Format 1 serializer driven by a
/// <see cref="Fmp.Core.Timing.MusicalTimeMap"/>-derived event stream. Track 0 is
/// the conductor track (tempo, time signature, markers, metadata); every other
/// track holds one logical voice. Delta times use variable-length quantities;
/// events are ordered deterministically within a tick, so the same input always
/// produces identical bytes. Tempo events are guaranteed to precede any notes at
/// the same tick.
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

        int trackCount = tracks.Count + 1;
        var chunks = new List<byte[]>(trackCount);
        chunks.Add(BuildHeader(trackCount));

        chunks.Add(BuildConductorTrack(conductor));
        foreach (MidiTrack track in tracks)
            chunks.Add(BuildMusicalTrack(track));

        int total = chunks.Sum(c => c.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }
        return result;
    }

    private byte[] BuildHeader(int numTracks)
    {
        // MThd <len=6> format=1 ntrks division=ppq
        var bytes = new List<byte> { 0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06 };
        bytes.Add(0); bytes.Add(1); // format 1
        bytes.Add((byte)(numTracks >> 8)); bytes.Add((byte)numTracks);
        bytes.Add((byte)(_ppq >> 8)); bytes.Add((byte)_ppq);
        return bytes.ToArray();
    }

    private byte[] BuildConductorTrack(IReadOnlyList<MidiEventBase> conductor)
    {
        var body = new List<byte>();
        WriteMetaText(body, 0x03, "Conductor", deltaTime: 0);
        var ordered = conductor
            .OrderBy(e => e.Tick)
            .ThenBy(MidiEventOrder.Rank)
            .ThenBy(e => e.SourceOrder)
            .ToList();
        long last = 0;
        foreach (MidiEventBase evt in ordered)
        {
            long delta = Math.Max(0, evt.Tick - last);
            last = evt.Tick;
            switch (evt)
            {
                case MidiTempoEvent tempo:
                    WriteMeta(body, 0x51, TempoBytes(tempo.MicrosecondsPerQuarter), delta);
                    break;
                case MidiTimeSignatureEvent ts:
                    WriteMeta(body, 0x58, TimeSignatureBytes(ts.Numerator, ts.Denominator), delta);
                    break;
                case MidiMetaTextEvent text:
                    WriteMeta(body, (byte)text.Type, Encoding.ASCII.GetBytes(text.Text), delta);
                    break;
                case MidiMarkerEvent marker:
                    WriteMeta(body, 0x06, Encoding.ASCII.GetBytes(marker.Name), delta);
                    break;
            }
        }
        AppendEndOfTrack(body);
        return Chunk("MTrk", body);
    }

    private byte[] BuildMusicalTrack(MidiTrack track)
    {
        var body = new List<byte>();
        WriteMetaText(body, 0x03, track.Name, deltaTime: 0);

        var ordered = track.Events
            .OrderBy(e => e.Tick)
            .ThenBy(MidiEventOrder.Rank)
            .ThenBy(e => e.SourceOrder)
            .ToList();
        long lastTick = 0;
        foreach (MidiEventBase evt in ordered)
        {
            long delta = Math.Max(0, evt.Tick - lastTick);
            lastTick = evt.Tick;
            switch (evt)
            {
                case MidiNoteEvent note:
                    byte status = (byte)((note.NoteOn ? 0x90 : 0x80) | (note.Channel & 0x0F));
                    WriteDelta(body, delta);
                    body.Add(status);
                    body.Add((byte)(note.Note & 0x7F));
                    body.Add((byte)(note.Velocity & 0x7F));
                    break;
                case MidiProgramEvent program:
                    WriteDelta(body, delta);
                    body.Add((byte)(0xC0 | (program.Channel & 0x0F)));
                    body.Add((byte)(program.Program & 0x7F));
                    break;
                case MidiBankEvent bank:
                    WriteDelta(body, delta);
                    body.Add((byte)(0xB0 | (bank.Channel & 0x0F)));
                    body.Add(0x00); // bank select MSB
                    body.Add((byte)(bank.Bank & 0x7F));
                    break;
                case MidiPitchBendEvent bend:
                    WriteDelta(body, delta);
                    body.Add((byte)(0xE0 | (bend.Channel & 0x0F)));
                    int value = Math.Clamp(bend.Bend, -8192, 8191) + 8192;
                    body.Add((byte)(value & 0x7F));
                    body.Add((byte)((value >> 7) & 0x7F));
                    break;
                case MidiBendRangeEvent range:
                    // RPN pitch-bend range (semitones): RPN msb=0, lsb=0, data entry msb.
                    WriteRpn(body, range.Channel, delta, valueMsb: (byte)(range.Semitones & 0x7F));
                    break;
                case MidiMetaTextEvent text:
                    WriteMeta(body, (byte)text.Type, Encoding.ASCII.GetBytes(text.Text), delta);
                    break;
            }
        }
        AppendEndOfTrack(body);
        return Chunk("MTrk", body);
    }

    private void WriteRpn(List<byte> body, int channel, long delta, byte valueMsb)
    {
        // RPN select (101=m0, 100=m127) then data entry (6). Emit every CC with an
        // explicit delta and full status — running-status-with-no-delta is invalid
        // SMF and breaks strict readers/parsers.
        var writes = new (int cc, int value)[]
        {
            (101, 0), (100, 0), (6, valueMsb),   // select RPN 0 + data entry msb
            (101, 0x7F), (100, 0x7F), (6, 0),    // null RPN (unselect)
        };
        byte ccStatus = (byte)(0xB0 | (channel & 0x0F));
        foreach ((int cc, int value) in writes)
        {
            WriteDelta(body, delta); delta = 0;
            body.Add(ccStatus);
            body.Add((byte)cc);
            body.Add((byte)(value & 0x7F));
        }
    }

    private static byte[] TempoBytes(int microsPerQuarter) => new[]
    {
        (byte)((microsPerQuarter >> 16) & 0xFF),
        (byte)((microsPerQuarter >> 8) & 0xFF),
        (byte)(microsPerQuarter & 0xFF),
    };

    private static byte[] TimeSignatureBytes(int numerator, int denominator)
    {
        // Denominator must be a power of two (MIDI uses the exponent).
        int d = denominator;
        int exp = 0;
        while (d > 1 && (d & 1) == 0)
        {
            d >>= 1;
            exp++;
        }
        if (d != 1)
            exp = 2; // fall back to quarter note
        return new[] { (byte)numerator, (byte)exp, (byte)24, (byte)8 };
    }

    private static void WriteMeta(List<byte> body, byte type, byte[] payload, long deltaTime)
    {
        WriteVlv(body, deltaTime);
        body.Add(0xFF);
        body.Add(type);
        WriteVlv(body, payload.Length);
        body.AddRange(payload);
    }

    private static void WriteMetaText(List<byte> body, byte type, string text, long deltaTime) =>
        WriteMeta(body, type, Encoding.ASCII.GetBytes(text), deltaTime);

    private void WriteDelta(List<byte> body, long delta)
    {
        WriteVlv(body, delta);
    }

    private void AppendEndOfTrack(List<byte> body) => WriteMeta(body, 0x2F, Array.Empty<byte>(), 0);

    /// <summary>
    /// Encodes <paramref name="value"/> as a MIDI variable-length quantity and appends
    /// it to <paramref name="body"/>. Values MUST be non-negative and fit in the
    /// representable MIDI VLQ range ([0, 0x0FFFFFFF], ≤ 4 bytes); anything outside
    /// that range is rejected with an exception rather than truncated (§44). The
    /// writer therefore never silently emits a negative delta or an invalid
    /// over-length quantity.
    /// </summary>
    internal static void WriteVlv(List<byte> body, long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "VLQ value must be non-negative.");
        if (value > 0x0FFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(value), "VLQ value exceeds the representable MIDI range (0x0FFFFFFF).");
        // Collect 7-bit groups little-endian, then emit big-endian with continuation.
        ulong working = (ulong)value;
        var groups = new byte[10];
        int n = 0;
        do
        {
            groups[n++] = (byte)(working & 0x7F);
            working >>= 7;
        } while (working != 0);
        for (int i = n - 1; i >= 0; i--)
        {
            body.Add((byte)(groups[i] | (i > 0 ? 0x80 : 0x00)));
        }
    }

    private static byte[] Chunk(string id, List<byte> body)
    {
        var chunk = new List<byte>(body.Count + 8)
        {
            (byte)id[0], (byte)id[1], (byte)id[2], (byte)id[3],
        };
        chunk.Add((byte)((body.Count >> 24) & 0xFF));
        chunk.Add((byte)((body.Count >> 16) & 0xFF));
        chunk.Add((byte)((body.Count >> 8) & 0xFF));
        chunk.Add((byte)(body.Count & 0xFF));
        chunk.AddRange(body);
        return chunk.ToArray();
    }
}
