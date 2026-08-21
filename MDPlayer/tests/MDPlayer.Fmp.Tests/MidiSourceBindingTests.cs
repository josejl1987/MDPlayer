using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiSourceBindingTests
{
    [Fact]
    public void GeneratedTracks_ExposeSemanticSourceVoiceIds_WithoutChangingNames()
    {
        var melodicDomain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 2);
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44_100,
            StartSample = 100,
            EndSample = 44_200,
            Notes = new[]
            {
                new NoteEvent(
                    "display-melodic",
                    100,
                    44_200,
                    440,
                    60,
                    "instrument",
                    VisualizationNoteMode.Fm,
                    false,
                    Array.Empty<PitchChange>())
                { Domain = melodicDomain },
            },
            SamplePlayback = new[]
            {
                new SamplePlaybackEvent(
                    "sample-voice",
                    100,
                    44_200,
                    "sample-id",
                    null,
                    1,
                    1,
                    0,
                    false,
                    false),
            },
            Rhythm = new[]
            {
                new RhythmEvent("display-rhythm", "rhythm-voice", 100, 1, 0),
            },
        };

        MidiTranscriptionResult result = new MidiTranscriber().Transcribe(timeline);
        Assert.Equal("domain:" + melodicDomain, result.Tracks[0].SourceVoiceId);
        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal(melodicDomain.ToString(), result.Tracks[0].SourceVoiceId["domain:".Length..]);
        Assert.Equal(melodicDomain.ToString(), result.Tracks[0].Name);
        Assert.Equal("sample-voice", result.Tracks[1].SourceVoiceId);
        Assert.Equal("Sample sample-voice", result.Tracks[1].Name);
        Assert.Equal("rhythm", result.Tracks[2].SourceVoiceId);
        Assert.Equal("Native Rhythm", result.Tracks[2].Name);
    }
    
    [Fact]
    public void PanelProjection_UsesSemanticIdsAndIgnoresTrackOrderAndLabels()
    {
        var panelA = new VisualizationPanel(
            "panel-a", "Renamed A", PreparedPanelKind.Pitched,
            PanelContentKind.SingleVoice, 0, ["voice-a"], []);
        var panelB = new VisualizationPanel(
            "panel-b", "Renamed B", PreparedPanelKind.Pitched,
            PanelContentKind.SingleVoice, 1, ["voice-b"], []);
        var topology = new VisualizationTopology([panelA, panelB]);
        var trackB = new MidiTrack { Name = "display-a", SourceVoiceId = "voice-b", Endpoint = new MidiEndpoint(0, 1) };
        var trackA = new MidiTrack { Name = "display-b", SourceVoiceId = "voice-a", Endpoint = new MidiEndpoint(0, 0) };

        IReadOnlyList<IReadOnlyList<MidiTrackBinding>> projected =
            MidiTrailPanelProjector.Project(topology, [trackB, trackA]);

        Assert.Equal("voice-a", projected[0][0].SourceVoiceId);
        Assert.Same(trackA, projected[0][0].Track);
        Assert.Equal("voice-b", projected[1][0].SourceVoiceId);
        Assert.Same(trackB, projected[1][0].Track);
    }
}
