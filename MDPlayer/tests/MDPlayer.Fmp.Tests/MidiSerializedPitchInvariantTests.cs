using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Xunit;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Export-level pitch oracle. The SMF is parsed again, events are merged by
/// (port, channel), and the channel's RPN/bend state is reconstructed exactly as
/// a synthesizer observes it.
/// </summary>
public sealed class MidiSerializedPitchInvariantTests
{
    private const int SampleRate = 44_100;
    private const int Ppq = 960;

    [Fact]
    public void SerializedSmf_PreservesPitchAndOneRangePerVoiceDomain()
    {
        VisualizationTimeline timeline = new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate,
            Notes = new[]
            {
                Note("a", 60.25,
                    Pitch(0, 60.25),
                    Pitch(1, 60.75), // Same serialized tick as NoteOn: final state wins.
                    Pitch(SampleRate / 4, 61.50),
                    Pitch(SampleRate / 4 + 1, 61.75)),
                Note("b", 48.0,
                    Pitch(0, 48.0),
                    Pitch(SampleRate / 2, 45.50)),
            },
        };

        MidiTranscriptionResult export = new MidiTranscriber(Ppq).Transcribe(timeline);
        IReadOnlyList<TimedEvent> events = ReadMergedEvents(export.Bytes);

        Assert.Single(events.Where(e => e.Event is SetTempoEvent));
        Assert.Equal(0, events.Single(e => e.Event is SetTempoEvent).Tick);

        var domains = export.Tracks
            .Where(track => track.VoiceDomain is not null)
            .ToDictionary(
                track => (track.Endpoint.Port, track.Endpoint.Channel),
                track => track.VoiceDomain!.Value);
        Assert.Equal(2, domains.Count);

        Dictionary<string, ExpectedDomain> expected = BuildExpected(timeline);
        var channelStates = new Dictionary<(byte Port, int Channel), ChannelState>();
        var eventsByChannel = events
            .Where(e => e.Event is ControlChangeEvent or PitchBendEvent or NoteOnEvent or NoteOffEvent)
            .GroupBy(e => (e.Port, e.Channel));

        foreach (IGrouping<(byte Port, int Channel), TimedEvent> channelEvents in eventsByChannel)
        {
            Assert.True(domains.TryGetValue(channelEvents.Key, out MidiVoiceDomain domain));
            Assert.True(expected.TryGetValue(
                TrackVoiceId(export, channelEvents.Key), out ExpectedDomain source));

            ChannelState state = channelStates.GetValueOrDefault(channelEvents.Key) ?? new();
            long currentTick = -1;
            int bendsAtCurrentTick = 0;
            foreach (TimedEvent timed in channelEvents.OrderBy(e => e.Tick).ThenBy(e => e.Track).ThenBy(e => e.Order))
            {
                if (timed.Tick != currentTick)
                {
                    currentTick = timed.Tick;
                    bendsAtCurrentTick = 0;
                }

                switch (timed.Event)
                {
                    case ControlChangeEvent cc:
                        state.Apply(cc.ControlNumber, cc.ControlValue);
                        break;

                    case PitchBendEvent bend:
                        bendsAtCurrentTick++;
                        state.ApplyBend(bend.PitchValue - 8192);
                        Assert.Equal(domain.BendRange, state.BendRange);
                        Assert.True(source.Points.TryGetValue(timed.Tick, out ExpectedPoint point));
                        double decoded = MidiSemanticDecoder.EffectivePitch(
                            point.BaseNote,
                            bend.PitchValue - 8192,
                            state.BendRange);
                        Assert.InRange(Math.Abs(decoded - point.Pitch), 0, 0.01);
                        break;

                    case NoteOnEvent noteOn when noteOn.Velocity != 0:
                        Assert.InRange(bendsAtCurrentTick, 0, 1);
                        Assert.True(source.Points.TryGetValue(timed.Tick, out ExpectedPoint attack));
                        Assert.Equal(domain.BendRange, state.BendRange);
                        double attackPitch = MidiSemanticDecoder.EffectivePitch(
                            noteOn.NoteNumber,
                            state.Bend,
                            state.BendRange);
                        Assert.InRange(Math.Abs(attackPitch - attack.Pitch), 0, 0.01);
                        state.ActiveNote = noteOn.NoteNumber;
                        break;

                    case NoteOffEvent noteOff:
                        state.ActiveNote = null;
                        break;
                }
            }

            Assert.Equal(1, channelEvents.Count(e => e.Event is ControlChangeEvent cc && cc.ControlNumber == 6));
        }
    }

    private static IReadOnlyList<TimedEvent> ReadMergedEvents(byte[] bytes)
    {
        MidiFile file = MidiFile.Read(new MemoryStream(bytes));
        var result = new List<TimedEvent>();
        TrackChunk[] tracks = file.GetTrackChunks().ToArray();
        for (int trackIndex = 0; trackIndex < tracks.Length; trackIndex++)
        {
            long tick = 0;
            byte port = 0;
            var trackEvents = tracks[trackIndex].Events;
            for (int order = 0; order < trackEvents.Count; order++)
            {
                MidiEvent midiEvent = trackEvents[order];
                tick += midiEvent.DeltaTime;
                if (midiEvent is PortPrefixEvent prefix)
                {
                    port = prefix.Port;
                    continue;
                }
                if (midiEvent is not ControlChangeEvent
                    and not PitchBendEvent
                    and not NoteOnEvent
                    and not NoteOffEvent
                    and not SetTempoEvent)
                    continue;

                int channel = midiEvent switch
                {
                    ControlChangeEvent cc => cc.Channel,
                    PitchBendEvent bend => bend.Channel,
                    NoteOnEvent noteOn => noteOn.Channel,
                    NoteOffEvent noteOff => noteOff.Channel,
                    _ => -1,
                };
                result.Add(new TimedEvent(tick, port, channel, trackIndex, order, midiEvent));
            }
        }

        return result.OrderBy(e => e.Tick).ThenBy(e => e.Track).ThenBy(e => e.Order).ToArray();
    }

    private static string TrackVoiceId(
        MidiTranscriptionResult export,
        (byte Port, int Channel) endpoint)
    {
        return export.Tracks
            .Single(track => (track.Endpoint.Port, track.Endpoint.Channel) == endpoint)
            .SourceVoiceId;
    }

    private static Dictionary<string, ExpectedDomain> BuildExpected(VisualizationTimeline timeline)
    {
        return timeline.Notes
            .Select((note, index) => (note, index))
            .GroupBy(item => VoiceId(item.note), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var points = new SortedDictionary<long, ExpectedPoint>();
                    foreach ((NoteEvent note, int index) in group.OrderBy(item => item.index))
                    {
                        long onTick = MidiTransportClock.SampleToTick(
                            timeline.StartSample, note.StartSample, timeline.SampleRate, Ppq);
                        double initial = note.InitialMidiNote;
                        foreach (PitchChange change in note.Pitch ?? Array.Empty<PitchChange>())
                            if (change.SamplePosition == note.StartSample)
                                initial = change.MidiNote;

                        var notePoints = new SortedDictionary<long, ExpectedPoint>
                        {
                            [onTick] = new ExpectedPoint(initial, 0),
                        };
                        foreach (PitchChange change in note.Pitch ?? Array.Empty<PitchChange>())
                        {
                            if (change.SamplePosition <= note.StartSample || change.SamplePosition >= note.EndSample)
                                continue;
                            long tick = MidiTransportClock.SampleToTick(
                                timeline.StartSample, change.SamplePosition, timeline.SampleRate, Ppq);
                            notePoints[tick] = new ExpectedPoint(change.MidiNote, 0);
                        }

                        // The base note belongs to the attack state, not to each
                        // later source pitch. The exporter keeps it stable for the note.
                        int attackBase = BaseNote(notePoints[onTick].Pitch);
                        foreach ((long tick, ExpectedPoint point) in notePoints)
                            points[tick] = point with { BaseNote = attackBase };
                    }
                    return new ExpectedDomain(points);
                });
    }

    private static NoteEvent Note(string voice, double initial, params PitchChange[] pitch)
    {
        var note = new NoteEvent(
            "voice-" + voice,
            0,
            SampleRate,
            0,
            initial,
            "test",
            VisualizationNoteMode.Fm,
            false,
            pitch);
        return note with
        {
            Domain = new SourceDomainKey(
                new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, voice == "a" ? 0 : 1),
        };
    }

    private static PitchChange Pitch(long sample, double midiNote) => new(sample, 0, midiNote);

    private static string VoiceId(NoteEvent note) =>
        note.Domain is SourceDomainKey domain ? "domain:" + domain : "channel:" + note.ChannelId;

    private static int BaseNote(double pitch) =>
        (int)Math.Clamp(Math.Round(pitch, MidpointRounding.AwayFromZero), 0, 127);

    private sealed class ChannelState
    {
        public int BendRange { get; private set; } = 2;
        public int Bend { get; private set; }
        public int? ActiveNote { get; set; }
        private int _rpnMsb = -1;
        private int _rpnLsb = -1;

        public void ApplyBend(int bend) => Bend = bend;

        public void Apply(int control, int value)
        {
            switch (control)
            {
                case 101:
                    _rpnMsb = value;
                    break;
                case 100:
                    _rpnLsb = value;
                    break;
                case 6 when _rpnMsb == 0 && _rpnLsb == 0:
                    BendRange = value;
                    break;
            }
        }
    }

    private readonly record struct TimedEvent(
        long Tick,
        byte Port,
        int Channel,
        int Track,
        int Order,
        MidiEvent Event);

    private readonly record struct ExpectedPoint(double Pitch, int BaseNote);
    private sealed record ExpectedDomain(SortedDictionary<long, ExpectedPoint> Points);
}
