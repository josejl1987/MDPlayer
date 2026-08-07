using System.Text;

namespace Fmp.Core.Visualization;

/// <summary>
/// Minimal standard MIDI file (SMF, format 1) serializer. Produces the MThd
/// header, a tempo/name meta track, and one MTrk per output track. Delta times
/// use variable-length quantities; only note-on/note-off and text meta events
/// are emitted, keeping the writer small and deterministic.
/// </summary>
internal static class MidiFileWriter
{
    public static byte[] Write(int ppqn, IReadOnlyList<DacMidiEvent> events, IReadOnlyList<string> trackNames)
    {
        if (ppqn <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppqn));

        int trackCount = trackNames.Count;
        var chunks = new List<byte[]>();
        chunks.Add(BuildHeader(ppqn, trackCount + 1)); // +1 tempo/meta track

        // Tempo track: name + tempo (120 BPM = 500000 us/quarter).
        chunks.Add(BuildMetaTrack(trackNames, ppqn, tempoMicrosPerQuarter: 500_000));

        var byTrack = events
            .GroupBy(e => e.Track)
            .OrderBy(g => g.Key)
            .ToArray();
        for (int t = 0; t < Math.Max(1, trackCount); t++)
        {
            string name = t < trackNames.Count ? trackNames[t] : $"YM2612 DAC Samples";
            var trackEvents = byTrack.FirstOrDefault(g => g.Key == t)?.ToList() ?? [];
            chunks.Add(BuildDacTrack(trackEvents, name));
        }

        return Concatchains(chunks);
    }

    private static byte[] BuildMetaTrack(IReadOnlyList<string> trackNames, int ppqn, int tempoMicrosPerQuarter)
    {
        var body = new List<byte>();

        // Track name meta (FF 03 len name)
        string conductor = trackNames.FirstOrDefault() ?? "DAC";
        WriteMetaText(body, 0x03, conductor, deltaTime: 0);

        // Tempo meta (FF 51 03 tttttt)
        WriteMeta(body, 0x51, new byte[]
        {
            (byte)(tempoMicrosPerQuarter >> 16),
            (byte)(tempoMicrosPerQuarter >> 8),
            (byte)tempoMicrosPerQuarter,
        }, deltaTime: 0);

        // End of track (FF 2F 00).
        AppendEndOfTrack(body);
        return Chunk("MTrk", body);
    }

    private static byte[] BuildDacTrack(List<DacMidiEvent> events, string trackName)
    {
        var body = new List<byte>();
        WriteMetaText(body, 0x03, trackName, deltaTime: 0);

        // Merge/normalize per-channel running notes is unnecessary for the
        // sparse DAC stream; emit each event with its delta time.
        long lastTick = 0;
        foreach (DacMidiEvent evt in events.OrderBy(e => e.Tick))
        {
            long delta = evt.Tick - lastTick;
            WriteVlv(body, delta);
            lastTick = evt.Tick;

            if (evt.Text is string text)
            {
                WriteMetaText(body, 0x01, text, deltaTime: 0); // FF 01 len text
                continue;
            }

            byte status = (byte)((evt.NoteOn ? 0x90 : 0x80) | (evt.Channel & 0x0F));
            body.Add(status);
            body.Add((byte)(evt.Note & 0x7F));
            body.Add((byte)(Math.Clamp(evt.Velocity, 0, 127)));
        }

        AppendEndOfTrack(body);
        return Chunk("MTrk", body);
    }

    private static byte[] BuildHeader(int ppqn, int numTracks)
    {
        // MThd <len=6> format=1 ntrks=numTracks division=ppqn
        var data = new byte[8];
        data[0] = 0; data[1] = 1; // format 1
        data[2] = (byte)(numTracks >> 8); data[3] = (byte)numTracks;
        data[4] = (byte)(ppqn >> 8); data[5] = (byte)ppqn;
        data[6] = 0; data[7] = 0;
        var bytes = new List<byte> { 0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06 };
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    private static void WriteMetaText(List<byte> body, byte type, string text, long deltaTime)
    {
        WriteVlv(body, deltaTime);
        WriteMeta(body, type, Encoding.ASCII.GetBytes(text));
    }

    private static void WriteMeta(List<byte> body, byte type, byte[] payload, long deltaTime = 0)
    {
        WriteVlv(body, deltaTime);
        body.Add(0xFF);
        body.Add(type);
        WriteVlv(body, payload.Length);
        body.AddRange(payload);
    }

    private static void AppendEndOfTrack(List<byte> body)
    {
        WriteVlv(body, 0);
        body.Add(0xFF);
        body.Add(0x2F);
        body.Add(0x00);
    }

    private static void WriteVlv(List<byte> body, long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "delta time must be non-negative.");
        // Compute number of bytes.
        int count = 1;
        long v = (value >> 7);
        while (v > 0)
        {
            count++;
            v >>= 7;
        }
        for (int i = count - 1; i >= 0; i--)
        {
            int b = (int)((value >> (i * 7)) & 0x7F);
            if (i != 0)
                b |= 0x80;
            body.Add((byte)b);
        }
    }

    private static byte[] Chunk(string id, List<byte> body)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(id));
        bytes.Add((byte)(body.Count >> 24));
        bytes.Add((byte)(body.Count >> 16));
        bytes.Add((byte)(body.Count >> 8));
        bytes.Add((byte)body.Count);
        bytes.AddRange(body);
        return bytes.ToArray();
    }

    private static byte[] Concatchains(List<byte[]> chunks)
    {
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
}