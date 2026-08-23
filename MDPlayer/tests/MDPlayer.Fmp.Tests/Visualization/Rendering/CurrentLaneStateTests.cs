using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

public sealed class CurrentLaneStateTests
{
    [Theory]
    [InlineData(99, false, "")]
    [InlineData(100, true, "C4")]
    [InlineData(150, true, "C4")]
    [InlineData(199, true, "C4")]
    [InlineData(200, false, "")]
    public void ActiveNote_UsesHalfOpenSampleInterval(long sample, bool active, string label)
    {
        CurrentLaneState state = CurrentLaneStateResolver.ResolveNotesForTest(
            [PreparedNote(100, 200, 60)], sample);

        Assert.Equal(active, state.HasLabel);
        Assert.Equal(label, state.Label1 ?? "");
    }

    [Fact]
    public void SequentialTransition_HandsOffAtExactBoundary()
    {
        PreparedPanel panel = Panel(
            PreparedNote(100, 200, 60),
            PreparedNote(200, 300, 62));
        var resolver = new CurrentLaneStateResolver([panel]);

        Assert.Equal("C4", resolver.Resolve(0, 199, 1).Label1);
        Assert.Equal("D4", resolver.Resolve(0, 200, 1).Label1);
        Assert.Null(resolver.Resolve(0, 300, 1).Label1);
    }

    [Fact]
    public void PitchBend_UsesPreparedInstantaneousPitchLabel()
    {
        PreparedNote note = PreparedNote(
            100,
            200,
            60,
            new PreparedPitchPoint(125, 60.25));

        CurrentLaneState state = CurrentLaneStateResolver.ResolveNotesForTest([note], 150);

        Assert.Equal("C4 +25c", state.Label1);
    }

    [Fact]
    public void Polyphony_StaysBoundedAndKeepsActiveNotes()
    {
        CurrentLaneState state = CurrentLaneStateResolver.ResolveNotesForTest(
            [
                PreparedNote(100, 200, 60),
                PreparedNote(100, 200, 67),
                PreparedNote(100, 200, 69),
                PreparedNote(100, 200, 71),
            ],
            150);

        Assert.True(state.HasActiveNote);
        Assert.Equal("C4", state.Label1);
        Assert.Equal("G4", state.Label2);
        Assert.Equal("A4", state.Label3);
        Assert.Equal("+2", state.AdditionalLabel);
    }

    [Fact]
    public void Silence_LeavesChannelIdentityToTheStaticGutter()
    {
        PreparedPanel panel = Panel();
        var resolver = new CurrentLaneStateResolver([panel]);

        Assert.Equal("FM1", panel.Label);
        Assert.False(resolver.Resolve(0, 150, 1).HasLabel);
    }

    [Fact]
    public void Percussion_UsesPreparedSemanticRowLabel()
    {
        PreparedPanel panel = new()
        {
            Id = "test.rhythm",
            Label = "RHYTHM",
            Schema = PanelPresentationSchema.PercussionRows,
            Kind = PreparedPanelKind.Rhythm,
            Content = PanelContentKind.SingleVoice,
            Track = new VisualizationTrackDescriptor
            {
                Id = "test.rhythm",
                DisplayName = "RHYTHM",
                Kind = VisualizationTrackKind.Percussion,
                PitchSystem = PitchCoordinateSystem.None,
            },
            Rows = [new PanelRowDefinition("kick", "KICK")],
            MainNotes = [],
            InstrumentChanges = [],
            OperatorNotes = [],
            CameraNotes = [],
            Rhythm = [new PreparedRhythmEvent { Voice = "kick", RowIndex = 0, SamplePosition = 100 }],
            Waveforms = [],
            WaveformsById = new Dictionary<string, WaveformDefinition>(),
            WaveformChanges = [],
            Samples = [],
            SamplesById = new Dictionary<string, SampleDefinition>(),
            SamplePlayback = [],
            SpcVoiceStates = [],
            Noise = [],
            NoiseLabels = [],
            AggregateHits = [],
            AggregateSubVoices = [],
            AggregateLabels = new Dictionary<string, string>(),
        };
        var resolver = new CurrentLaneStateResolver([panel]);

        Assert.Equal("KICK", resolver.Resolve(0, 100, 1).Label1);
        Assert.Null(resolver.Resolve(0, 103, 1).Label1);
    }

    [Theory]
    [InlineData("TABLE UNAVAILABLE")]
    [InlineData(" TABLE UNAVAILABLE ")]
    [InlineData("UNKNOWN")]
    [InlineData("SMP UNKNOWN")]
    [InlineData("N/A")]
    [InlineData("NO TABLE")]
    [InlineData("---")]
    [InlineData("?")]
    public void MissingOptionalMetadata_IsNotPresented(string value)
        => Assert.Null(PresentationMetadata.OptionalLabel(value));

    [Fact]
    public void MetadataFilter_DoesNotBanLegitimateSongTextContainingSentinel()
        => Assert.Equal(
            "Song TABLE UNAVAILABLE",
            PresentationMetadata.OptionalLabel("Song TABLE UNAVAILABLE"));

    [Fact]
    public void LaneGutter_IsWideEnoughForIdentityAndBadge()
    {
        var layout = new OverlayLayout(1920, 1080, 0.75, 2.25);

        Assert.InRange(layout.PitchLabelWidth, 90, 125);
    }

    private static PreparedPanel Panel(params PreparedNote[] notes)
        => new()
        {
            Id = "test.fm1",
            Label = "FM1",
            Schema = PanelPresentationSchema.PitchedLane,
            Kind = PreparedPanelKind.Pitched,
            Content = PanelContentKind.SingleVoice,
            Track = new VisualizationTrackDescriptor
            {
                Id = "test.fm1",
                DisplayName = "FM1",
                Kind = VisualizationTrackKind.Pitched,
                PitchSystem = PitchCoordinateSystem.AbsoluteMidi,
            },
            Rows = [],
            MainNotes = notes,
            InstrumentChanges = [],
            OperatorNotes = [],
            CameraNotes = notes,
            Rhythm = [],
            Waveforms = [],
            WaveformsById = new Dictionary<string, WaveformDefinition>(),
            WaveformChanges = [],
            Samples = [],
            SamplesById = new Dictionary<string, SampleDefinition>(),
            SamplePlayback = [],
            SpcVoiceStates = [],
            Noise = [],
            NoiseLabels = [],
            AggregateHits = [],
            AggregateSubVoices = [],
            AggregateLabels = new Dictionary<string, string>(),
        };

    private static PreparedNote PreparedNote(
        long start,
        long end,
        double midi,
        params PreparedPitchPoint[] pitch)
        => new()
        {
            StartSample = start,
            EndSample = end,
            InitialMidiNote = midi,
            Mode = VisualizationNoteMode.Fm,
            InstrumentId = "test",
            Fill = new OverlayColor(80, 120, 220),
            ActiveFill = new OverlayColor(100, 150, 240),
            CapFill = new OverlayColor(120, 170, 250),
            Accent = new OverlayColor(140, 190, 255),
            Pitch = pitch,
        };
}
