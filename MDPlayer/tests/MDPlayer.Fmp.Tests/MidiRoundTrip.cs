using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Shared round-trip helper: parses exported SMF bytes with DryWetMIDI 8.0.3 so
/// tests assert the semantic object model rather than walking raw bytes.
/// </summary>
internal static class MidiRoundTrip
{
    /// <summary>Parses a stream into a <see cref="MidiFile"/>. The exact 8.0.3 API is
    /// <c>MidiFile.Read(Stream, ReadingSettings)</c> (no 2-arg overload).</summary>
    public static MidiFile Read(byte[] bytes) =>
        MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
        {
            // Keep End Of Track events so tests can assert one per track.
            EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store,
        });

    public static IReadOnlyList<TrackChunk> TrackChunks(byte[] bytes) =>
        Read(bytes).GetTrackChunks().ToList();

    public static bool StartsWith(string chunkId, byte[] bytes)
    {
        if (bytes.Length < 4)
            return false;
        return bytes[0] == (byte)chunkId[0]
            && bytes[1] == (byte)chunkId[1]
            && bytes[2] == (byte)chunkId[2]
            && bytes[3] == (byte)chunkId[3];
    }

    /// <summary>Parsed (absolute tick, event) pairs of a chunk in serialized order.</summary>
    public static IReadOnlyList<(long Tick, MidiEvent Event)> TimedEvents(byte[] bytes, int trackIndex)
        => TimedEvents(TrackChunks(bytes)[trackIndex]);

    /// <summary>Parsed (absolute tick, event) pairs of a chunk in serialized order.</summary>
    public static IReadOnlyList<(long Tick, MidiEvent Event)> TimedEvents(TrackChunk chunk)
    {
        var result = new List<(long, MidiEvent)>();
        long tick = 0;
        foreach (MidiEvent evt in chunk.Events)
        {
            tick += evt.DeltaTime;
            result.Add((tick, evt));
        }
        return result;
    }
}