using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Xunit;
using NoteOnEvent = Melanchall.DryWetMidi.Core.NoteOnEvent;
using NoteOffEvent = Melanchall.DryWetMidi.Core.NoteOffEvent;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Per-channel MIDI export oracle. MidiTrailChannelExporter splits one raw
/// source-faithful transcript into one SMF file per semantic source voice so
/// each can be rendered by a separate stock MIDITrail instance under Wine and
/// tiled 3x2. The hard invariant under test: EVERY channel file shares the SAME
/// global transport as the all-channel file (division, tempo map, lead-in,
/// total duration) while carrying ONLY that channel's musical events.
/// </summary>
public sealed class MidiTrailChannelExporterTests
{
    private const int Sr = 44100;
    private const int Ppq = 960;

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        SampleRate = Sr,
        StartSample = 0,
        EndSample = notes.Select(n => n.EndSample).DefaultIfEmpty(1).Max(),
        Notes = notes,
    };

    // Domain voices produce SourceVoiceId "domain:ym2608.0.Fm:{index}" (Index is
    // 1-based into the FM region; SourceDomainKey.ToString uses the enum name, not
    // lowercased).
    private const string Fm1 = "domain:ym2608.0.Fm:1";
    private const string Fm2 = "domain:ym2608.0.Fm:2";
    private const string Fm3 = "domain:ym2608.0.Fm:3";

    private static NoteEvent Note(
        string voice, long start, long end, double midiNote,
        IReadOnlyList<PitchChange>? pitch = null)
    {
        var note = new NoteEvent(
            ChannelId: "voice-" + voice,
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 0,
            InitialMidiNote: midiNote,
            InstrumentId: "test",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: pitch ?? Array.Empty<PitchChange>());
        return note with { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, int.Parse(voice)) };
    }

    private static IReadOnlyList<(long Tick, MidiEvent Event)> Track(byte[] bytes, int trackIndex) =>
        MidiRoundTrip.TimedEvents(bytes, trackIndex);

    private static IReadOnlyList<TrackChunk> AllChannelFile(VisualizationTimeline timeline) =>
        MidiRoundTrip.TrackChunks(new MidiTranscriber().Transcribe(timeline).Bytes);

    [Fact]
    public void SplitsIntoOneFilePerSourceVoice()
    {
        // Three voices: FM1, FM2, SSG1 (types don't matter to the splitter; two
        // FM and one non-FM proves compeletes grouping).
        var timeline = Timeline(
            Note("1", 0, Sr, 60.0),
            Note("2", 0, Sr / 2, 64.0),
            Note("3", Sr / 2, Sr, 67.0));

        var result = MidiTrailChannelExporter.Export(timeline);

        Assert.Equal(3, result.Channels.Count);
        Assert.Contains(result.Channels, c => c.SourceVoiceId == Fm1);
        Assert.Contains(result.Channels, c => c.SourceVoiceId == Fm2);
        Assert.Contains(result.Channels, c => c.SourceVoiceId == Fm3);
    }

    [Fact]
    public void EveryChannelFile_ReusesAllChannelTransport()
    {
        // FM1 spans the whole song; FM2 ends at one second. FM2 is the
        // early-ending channel that must be padded to the shared end tick.
        var timeline = Timeline(
            Note("1", 0, 2 * Sr, 60.0),
            Note("2", 0, Sr, 64.0));

        MidiTrailChannelsResult result = MidiTrailChannelExporter.Export(timeline);
        long totalEndTick = MidiTransportClock.SampleToTick(0, 2 * Sr, Sr, Ppq);

        Assert.Equal(totalEndTick, result.TotalEndTick);

        foreach (MidiTrailChannelExport channel in result.Channels)
        {
            // Division = 960 PPQ, Format 1.
            var file = MidiRoundTrip.Read(channel.Bytes);
            Assert.Equal(Ppq, TicksPerQuarter(file));
            Assert.Equal(2, file.GetTrackChunks().Count()); // conductor + one voice

            // Tempo map: exactly one 120 BPM tempo event at tick 0, no timesig.
            IReadOnlyList<(long Tick, MidiEvent Event)> conductor = Track(channel.Bytes, 0);
            var tempos = conductor.Where(c => c.Event is SetTempoEvent).ToList();
            Assert.Single(tempos);
            Assert.Equal(0, tempos[0].Tick);
            Assert.Equal(500_000, ((SetTempoEvent)tempos[0].Event).MicrosecondsPerQuarterNote);
            Assert.DoesNotContain(conductor, c => c.Event is TimeSignatureEvent);
            Assert.DoesNotContain(conductor, c => c.Event is MarkerEvent && ((MarkerEvent)c.Event).Text != "End");

            // Lead-in: tempo sits at the very first tick (no artificial delay).
            Assert.Equal(0, conductor.First().Tick);
        }
    }

    [Fact]
    public void ChannelFile_CarriesOnlyItsOwnVoiceEvents()
    {
        var timeline = Timeline(
            Note("1", 0, Sr, 60.0),
            Note("2", 0, Sr, 64.0));

        MidiTrailChannelsResult result = MidiTrailChannelExporter.Export(timeline);

        MidiTrailChannelExport fm1 = result.Channels.Single(c => c.SourceVoiceId == Fm1);
        MidiTrailChannelExport fm2 = result.Channels.Single(c => c.SourceVoiceId == Fm2);

        var fm1Voice = Track(fm1.Bytes, 1);
        var fm2Voice = Track(fm2.Bytes, 1);

        // Each channel's musical track contains exactly its own note and bends.
        Assert.Single(fm1Voice, e => e.Event is NoteOnEvent);
        Assert.Single(fm1Voice, e => e.Event is NoteOnEvent n && n.NoteNumber == 60);
        Assert.DoesNotContain(fm1Voice, e => e.Event is NoteOnEvent n && n.NoteNumber == 64);

        Assert.Single(fm2Voice, e => e.Event is NoteOnEvent);
        Assert.Single(fm2Voice, e => e.Event is NoteOnEvent n && n.NoteNumber == 64);
        Assert.DoesNotContain(fm2Voice, e => e.Event is NoteOnEvent n && n.NoteNumber == 60);
    }

    [Fact]
    public void EarlyEndingChannel_IsPaddedToSharedEndTick()
    {
        // FM2 (voice "2") ends at 1 s = 1920 ticks; the song runs 2 s = 3840.
        // The channel's conductor End marker must pin the nominal end to the
        // FULL song duration, not its own last note.
        var timeline = Timeline(
            Note("1", 0, 2 * Sr, 60.0),
            Note("2", 0, Sr, 64.0));

        MidiTrailChannelsResult result = MidiTrailChannelExporter.Export(timeline);
        long totalEndTick = MidiTransportClock.SampleToTick(0, 2 * Sr, Sr, Ppq);

        MidiTrailChannelExport fm2 = result.Channels.Single(c => c.SourceVoiceId == Fm2);

        // Its own last musical event is early...
        Assert.True(fm2.EndTick < totalEndTick);

        // ...but the conductor carries a trailing End marker at the full end tick.
        IReadOnlyList<(long Tick, MidiEvent Event)> conductor = Track(fm2.Bytes, 0);
        var end = conductor.Single(c => c.Event is MarkerEvent && ((MarkerEvent)c.Event).Text == "End");
        Assert.Equal(totalEndTick, end.Tick);
    }

    [Fact]
    public void AllChannels_ShareTheIdenticalTempoAndEndFrame()
    {
        var timeline = Timeline(
            Note("1", 0, Sr, 60.0),
            Note("2", 0, 3 * Sr, 64.0),
            Note("3", Sr, 2 * Sr, 67.0));

        MidiTrailChannelsResult result = MidiTrailChannelExporter.Export(timeline);

        foreach (MidiTrailChannelExport channel in result.Channels)
        {
            IReadOnlyList<(long Tick, MidiEvent Event)> conductor = Track(channel.Bytes, 0);
            var tempo = conductor.Single(c => c.Event is SetTempoEvent);
            var end = conductor.Single(c => c.Event is MarkerEvent && ((MarkerEvent)c.Event).Text == "End");
            Assert.Equal(0, tempo.Tick);
            Assert.Equal(result.TotalEndTick, end.Tick);
        }

        // Cross-check against the all-channel file's end tick.
        var allTracks = AllChannelFile(timeline);
        long allEnd = 0;
        for (int t = 1; t < allTracks.Count; t++)
            allEnd = Math.Max(allEnd, Timed(allTracks[t]).Last().Tick);
        Assert.Equal(allEnd, result.TotalEndTick);
    }

    private static IReadOnlyList<(long Tick, MidiEvent Event)> Timed(TrackChunk chunk)
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

    private static int TicksPerQuarter(MidiFile file) =>
        file.TimeDivision is TicksPerQuarterNoteTimeDivision tpq
            ? tpq.TicksPerQuarterNote
            : throw new InvalidOperationException("Expected ticks-per-quarter time division");
}
