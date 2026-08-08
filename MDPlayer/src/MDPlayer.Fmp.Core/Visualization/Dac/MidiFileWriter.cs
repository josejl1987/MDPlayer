using System.Text;

namespace Fmp.Core.Visualization;

/// <summary>
/// Minimal standard MIDI file (SMF, format 1) serializer. Produces the MThd
/// header, a tempo/name meta track, and one MTrk per output track. Delta times
/// use variable-length quantities; only note-on/note-off and text meta events
/// are emitted, keeping the writer small and deterministic.
///
/// This helper is NOT a timing authority: it receives the per-segment Set Tempo
/// events, as already established by the caller's <see cref="MusicalTimeMap"/>, and
/// never infers a default BPM (§37). All event ticks are already resolved musical
/// ticks.
/// </summary>
internal static class MidiFileWriter
{
    public static byte[] Write(
        int ppqn,
        IReadOnlyList<DacMidiEvent> events,
        IReadOnlyList<string> trackNames,
        IReadOnlyList<DacTempoEvent> tempoEvents)
    {
        if (ppqn <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppqn));

        int trackCount = trackNames.Count;
        var chunks = new List<byte[]>();
        chunks.Add(BuildHeader(ppqn, trackCount + 1)); // +1 tempo/meta track

        // Tempo track: name + the Set Tempo events already established by the shared
        // map, one per distinct tempo segment (never a hardcoded 120 BPM default —
        // §37).
        chunks.Add(BuildMetaTrack(trackNames, tempoEvents));

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

    private static byte[] BuildMetaTrack(IReadOnlyList<string> trackNames, IReadOnlyList<DacTempoEvent> tempoEvents)
    {
        var body = new List<byte>();

        // Track name meta (FF 03 len name)
        string conductor = trackNames.FirstOrDefault() ?? "DAC";
        WriteMetaText(body, 0x03, conductor, deltaTime: 0);

        // Set Tempo meta (FF 51 03 tttttt) per tempo segment, oldest-to-newest.
        // The first tempo lands on tick 0; each later segment emits its tempo at its
        // own delta from the previous tempo, matching the conductor of the melodic
        // exporter so DAC playback tempo follows the full map (spec §19, §37).
        long lastTick = 0;
        foreach (DacTempoEvent tempo in tempoEvents.OrderBy(t => t.Tick))
        {
            long delta = tempo.Tick - lastTick;
            if (delta < 0)
                throw new ArgumentOutOfRangeException(nameof(tempoEvents), "tempo ticks must be non-decreasing.");
            WriteMeta(body, 0x51, new byte[]
            {
                (byte)(tempo.MicrosecondsPerQuarter >> 16),
                (byte)(tempo.MicrosecondsPerQuarter >> 8),
                (byte)tempo.MicrosecondsPerQuarter,
            }, deltaTime: delta);
            lastTick = tempo.Tick;
        }

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
            lastTick = evt.Tick;

            if (evt.Text is string text)
            {
                // Exactly ONE delta precedes the meta text (written by WriteMetaText,
                // which emits delta then FF 01 len text). Never also emit the loop
                // delta here, or a second zero delta would be consumed by the reader
                // as a bogus status byte and malform the track (P1).
                WriteMetaText(body, 0x01, text, deltaTime: delta);
                continue;
            }

            WriteVlv(body, delta);
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
        // MThd <len=6> format=1 ntrks=numTracks division=ppqn. The body MUST be
        // exactly 6 bytes (2 format + 2 ntrks + 2 division) to match the declared
        // length; no trailing padding.
        return new byte[]
        {
            0x4D, 0x54, 0x68, 0x64,             // "MThd"
            0x00, 0x00, 0x00, 0x06,             // body length = 6
            0x00, 0x01,                         // format 1
            (byte)(numTracks >> 8), (byte)numTracks,
            (byte)(ppqn >> 8), (byte)ppqn,
        };
    }

    private static void WriteMetaText(List<byte> body, byte type, string text, long deltaTime) =>
        WriteMeta(body, type, Encoding.ASCII.GetBytes(text), deltaTime);

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
        // Reject values outside the standard MIDI VLQ range ([0, 0x0FFFFFFF], ≤ 4
        // bytes). A larger delta would be emitted as a 5+ byte over-length quantity,
        // which MIDI readers do not consume (P2) — matching the core writer (§44).
        if (value > 0x0FFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(value), "delta time exceeds the representable MIDI range (0x0FFFFFFF).");
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