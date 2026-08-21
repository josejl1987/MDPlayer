#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>
/// One per-voice SMF export. A channel file carries only the musical events of
/// a single semantic source voice (its <see cref="SourceVoiceId"/>), while
/// preserving the SAME global transport as the all-channel source file: same
/// division (SMF1, 960 PPQ), same tempo map (fixed 120 BPM at tick 0) and the
/// SAME nominal total duration, so any consumer that tiles or diffs per-voice
/// files sees one common time base.
/// </summary>
internal sealed class MidiTrailChannelExport
{
    /// <summary>The semantic voice this channel isolates (e.g. <c>domain:FM1</c>).</summary>
    public required string SourceVoiceId { get; init; }

    /// <summary>Human-readable track name for the voice (from the transcript track).</summary>
    public required string DisplayName { get; init; }

    /// <summary>SMF1 bytes containing the conductor + only this voice's track(s).</summary>
    public required byte[] Bytes { get; init; }

    /// <summary>The absolute tick of this voice's last event (same transport frame as the source).</summary>
    public required long EndTick { get; init; }
}

/// <summary>
/// The result of splitting a transcript into one SMF file per semantic source
/// voice. <see cref="TotalEndTick"/> is the common end tick every channel file
/// shares (the max end tick across the all-channel transcript), used to pad
/// early-ending channels so their nominal duration matches the full song.
/// </summary>
internal sealed class MidiTrailChannelsResult
{
    public required IReadOnlyList<MidiTrailChannelExport> Channels { get; init; }

    /// <summary>Common transport end tick shared by every channel file.</summary>
    public required long TotalEndTick { get; init; }
}

/// <summary>
/// Splits a raw source-faithful transcription into one SMF file per semantic
/// source voice (FM channels, SSG voices, sample/DAC voices). This is a
/// standalone transcription surface: each returned file isolates one voice on
/// a transport identical to the all-channel file, so per-voice outputs can be
/// rendered, tiled or compared by any external tool against one common time
/// base. The visualization pipeline never consumes these files.
///
/// Transport parity invariant (same as the all-channel file):
///   - SMF Format 1, division = <paramref name="ppq"/> (960 by default)
///   - tempo map: a single fixed 120 BPM Set Tempo at tick 0, no time signatures
///   - lead-in: the conductor holds tempo at tick 0 (no artificial delay)
///   - total duration: a trailing End marker on the conductor pins every channel
///     file's nominal end to <see cref="MidiTrailChannelsResult.TotalEndTick"/>,
///     the same end tick as the all-channel song, so early-ending channels are
///     padded to the full song length.
/// </summary>
internal static class MidiTrailChannelExporter
{
    public const int DefaultPpq = MidiTranscriber.DefaultPpq;

    public static MidiTrailChannelsResult Export(VisualizationTimeline timeline, int ppq = DefaultPpq)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        MidiTranscriptionResult transcript = new MidiTranscriber(ppq).Transcribe(timeline);
        long totalEndTick = MaxEndTick(transcript.Tracks, ppq);

        var channels = new List<MidiTrailChannelExport>();
        foreach (IGrouping<string, MidiTrack> group in transcript.Tracks
            .GroupBy(track => track.SourceVoiceId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(group.Key))
                continue;

            MidiTrack primary = group.First();
            MidiTrack[] voiceTracks = group
                .OrderBy(track => track.Name, StringComparer.Ordinal)
                .ToArray();

            byte[] bytes = BuildChannelFile(ppq, totalEndTick, voiceTracks);
            channels.Add(new MidiTrailChannelExport
            {
                SourceVoiceId = group.Key,
                DisplayName = primary.Name,
                Bytes = bytes,
                EndTick = EndTick(voiceTracks),
            });
        }

        return new MidiTrailChannelsResult
        {
            Channels = channels,
            TotalEndTick = totalEndTick,
        };
    }

    /// <summary>
    /// Builds one SMF1 file: the shared conductor (tempo at tick 0 plus a
    /// trailing End marker pinned to <paramref name="totalEndTick"/>) followed by
    /// this channel's own tracks. Conductor events therefore carry the transport
    /// parity; channel tracks carry only their own musical events.
    /// </summary>
    private static byte[] BuildChannelFile(int ppq, long totalEndTick, IReadOnlyList<MidiTrack> voiceTracks)
    {
        var conductor = new List<MidiEventBase>
        {
            new MidiTempoEvent(0, MidiTranscriber.TransportMicrosecondsPerQuarter) { SourceOrder = 0 },
            new MidiMetaTextEvent(totalEndTick, 0x06, "End") { SourceOrder = 1 },
        };
        return new MidiFileWriter(ppq).Write(conductor, voiceTracks);
    }

    private static long MaxEndTick(IReadOnlyList<MidiTrack> tracks, int ppq)
    {
        long max = 0;
        foreach (MidiTrack track in tracks)
            max = Math.Max(max, EndTick(track));
        return max;
    }

    private static long EndTick(MidiTrack track)
    {
        long max = 0;
        if (track.UsesPackedEvents)
        {
            foreach (PackedMidiEvent evt in track.PackedEvents)
                max = Math.Max(max, evt.Tick);
        }
        else
        {
            foreach (MidiEventBase evt in track.Events)
                max = Math.Max(max, evt.Tick);
        }
        return max;
    }

    private static long EndTick(IReadOnlyList<MidiTrack> tracks)
    {
        long max = 0;
        foreach (MidiTrack track in tracks)
            max = Math.Max(max, EndTick(track));
        return max;
    }
}
