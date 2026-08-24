using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Performance-lane geometry contract: every channel renders as one
/// full-width horizontal band on a single common time axis — identical
/// playhead X in all lanes, no header chrome, gutter-carried labels, and thin
/// separators instead of boxed panels.
/// </summary>
public sealed class PerformanceLaneLayoutTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Rate = 48_000;

    [Fact]
    public void Lanes_AreFullWidthContiguousBands()
    {
        (ResolvedVisualizationLayout layout, _) = Build(voiceCount: 5);

        Assert.Equal(VisualizationLayoutVariant.PerformanceLanes, layout.Variant);
        Assert.Equal(5, layout.Geometry.PanelCount);

        int expectedY = layout.Geometry.GridY;
        for (int i = 0; i < 5; i++)
        {
            OverlayRect band = layout.Geometry.GetPanelRect(i);
            Assert.Equal(layout.Geometry.Width, band.Width);
            Assert.Equal(0, band.X);
            Assert.Equal(expectedY, band.Y);
            expectedY = band.Bottom;
        }
        Assert.Equal(layout.Geometry.MasterWaveformRect.Y, expectedY);
        Assert.Equal(layout.Geometry.BottomBarRect.Y, layout.Geometry.MasterWaveformRect.Bottom);
    }

    [Fact]
    public void AllLanes_ShareOnePlayheadX()
    {
        (ResolvedVisualizationLayout layout, _) = Build(voiceCount: 5);

        int shared = layout.Geometry.GetPlayheadX(0);
        Assert.True(shared > 0 && shared < layout.Geometry.Width);
        for (int i = 1; i < layout.Geometry.PanelCount; i++)
            Assert.Equal(shared, layout.Geometry.GetPlayheadX(i));
    }

    [Fact]
    public void Lanes_HaveNoHeaderChrome_AndReserveFullBandForRoll()
    {
        (ResolvedVisualizationLayout layout, _) = Build(voiceCount: 5);

        Assert.Equal(0, layout.Geometry.PanelHeaderHeight);
        Assert.True(layout.Geometry.HasRoll);
        Assert.True(layout.Geometry.HasScopes);
        Assert.Equal(layout.Geometry.MasterWaveformHeight, layout.Geometry.CorrscopeGridHeight);
        Assert.Equal(112, layout.Geometry.PitchLabelWidth);

        for (int i = 0; i < layout.Geometry.PanelCount; i++)
        {
            Assert.Equal(0, layout.Geometry.GetHeaderRect(i).Height);
            OverlayRect band = layout.Geometry.GetPanelRect(i);
            OverlayRect roll = layout.Geometry.GetTimelineRect(i);
            Assert.Equal(band.Y, roll.Y);
            Assert.Equal(band.Height, roll.Height);

            // Identity, live state, and patch text live inside the pitch gutter.
            PanelHeaderLayout slots = layout.Geometry.HeaderSlots(i);
            Assert.True(slots.State.Width > 0 && slots.Patch.Width > 0);
            Assert.True(slots.Name.X >= roll.X && slots.Name.Right <= roll.X + layout.Geometry.PitchLabelWidth);
        }
    }

    [Fact]
    public void MixedContent_WeightsPitchedLanesAboveSampleAndNoise()
    {
        var topology = new VisualizationTopology(
        [
            new VisualizationPanel("fm3", "FM3", PreparedPanelKind.Fm3, PanelContentKind.SingleVoice, 0, ["fm3"], [])
            {
                Schema = PanelPresentationSchema.FmOperatorGroup,
            },
            new VisualizationPanel("fm5", "FM5", PreparedPanelKind.Pitched, PanelContentKind.SingleVoice, 1, ["fm5"], [])
            {
                Schema = PanelPresentationSchema.PitchedLane,
            },
            new VisualizationPanel("dac", "DAC", PreparedPanelKind.PcmVoice, PanelContentKind.SingleVoice, 2, ["dac"], [])
            {
                Schema = PanelPresentationSchema.SampleLane,
            },
            new VisualizationPanel("noise", "NOISE", PreparedPanelKind.Noise, PanelContentKind.SingleVoice, 3, ["noise"], [])
            {
                Schema = PanelPresentationSchema.NoiseLane,
            },
        ]);

        ResolvedVisualizationLayout layout = VisualizationLayoutResolver.ComposePerformance(
            Width,
            Height,
            0.75,
            2.25,
            VisualizationLayoutMode.Performance,
            topology,
            VisualizationScopePosition.Bottom,
            null,
            null,
            1.0,
            null,
            scopesEnabled: true);

        int[] heights = layout.Geometry.PerformanceLaneHeights.ToArray();
        Assert.Equal(4, heights.Length);
        Assert.True(heights[0] > heights[2]);
        Assert.True(heights[1] > heights[3]);
        Assert.Equal(55, layout.Geometry.TopBarHeight);
        Assert.Equal(25, layout.Geometry.BottomBarHeight);
        Assert.Equal(120, layout.Geometry.MasterWaveformHeight);
        Assert.Equal(
            layout.Geometry.GridHeight,
            heights.Sum() + layout.Geometry.MasterWaveformHeight);
    }

    [Fact]
    public void PerformanceLanes_HasCliNameAndVariant()
    {
        (ResolvedVisualizationLayout layout, _) = Build(voiceCount: 3);
        Assert.Equal("performance", VisualizationLayoutNames.ToCliName(layout.Mode));
        Assert.Equal("performance-lanes", VisualizationLayoutNames.ToCliVariant(layout.Variant));
    }

    [Fact]
    public void ManyChannels_AtSmallCanvas_ValidateAndRender()
    {
        // 11 lanes on a 960x540 canvas: ~40px bands. The grid-era 53-72px
        // pitch minimums would reject this; dedicated lane ribbons are legal.
        (ResolvedVisualizationLayout layout, VisualizationTimeline timeline) = Build(
            voiceCount: 11, width: 960, height: 540);

        Assert.Equal(11, layout.Geometry.PanelCount);
        Assert.Equal(VisualizationLayoutVariant.PerformanceLanes, layout.Variant);
        Assert.True(layout.Geometry.PanelHeight >= 8);

        using var renderer = new PanelOverlayRenderer(
            timeline, layout, new PanelOverlayRenderer.Options
            {
                FpsNumerator = 60,
                FpsDenominator = 1,
            });
        Assert.NotEmpty(renderer.RenderFrame(0));
    }

    [Fact]
    public void LaneRenderer_ProducesDeterministicNonEmptyFrames()
    {
        (ResolvedVisualizationLayout layout, VisualizationTimeline timeline) = Build(voiceCount: 5);

        using var renderer = new PanelOverlayRenderer(
            timeline, layout, new PanelOverlayRenderer.Options
            {
                FpsNumerator = 60,
                FpsDenominator = 1,
            });

        byte[] first = renderer.RenderFrame(0);
        byte[] second = renderer.RenderFrame(0);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(second)));
        Assert.NotEqual(new byte[first.Length], first);

        long last = renderer.TotalFrames - 1;
        Assert.NotEmpty(renderer.RenderFrame(last));
    }

    // ---- fixtures --------------------------------------------------------------

    private static (ResolvedVisualizationLayout Layout, VisualizationTimeline Timeline) Build(
        int voiceCount, int width = Width, int height = Height)
    {
        var voices = new List<VoiceDescriptor>();
        var notes = new List<NoteEvent>();
        for (int i = 0; i < voiceCount - 1; i++)
        {
            var voice = new VoiceDescriptor(
                new VoiceId(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, i),
                $"FM{i + 1}",
                VoicePresentationKind.Pitched,
                Order: i,
                IsPercussion: false,
                IsNoise: false,
                SupportsPitch: true);
            voices.Add(voice);
            string id = voice.Id.ToString();
            notes.Add(new NoteEvent(
                id, 4_800 * (i + 1), 24_000 + 4_800 * i, 440.0, 69.0,
                "instrument", VisualizationNoteMode.Fm, IsRetrigger: false, []));
        }

        var rhythmVoice = new VoiceDescriptor(
            new VoiceId(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0),
            "RHYTHM",
            VoicePresentationKind.Percussion,
            Order: voiceCount - 1,
            IsPercussion: true,
            IsNoise: false,
            SupportsPitch: false);
        voices.Add(rhythmVoice);
        string rhythmId = rhythmVoice.Id.ToString();
        var rhythm = new List<RhythmEvent>
        {
            new("bd", rhythmId, 9_600, 0.9f, 0f),
            new("sd", rhythmId, 19_200, 0.7f, 0f),
        };

        var timeline = new VisualizationTimeline
        {
            SampleRate = Rate,
            StartSample = 0,
            EndSample = 48_000,
            Devices = [new DeviceDescriptor(
                new DeviceId(ChipType.Ym2608, 0), nameof(ChipType.Ym2608), 1, DeviceCapabilities.Notes)],
            Voices = voices,
            Notes = notes,
            Rhythm = rhythm,
        };

        ResolvedVisualizationLayout layout = VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutMode.Performance,
            new VisualizationLayoutSettings(
                width, height,
                PastSeconds: 0.75, FutureSeconds: 2.25, RollZoom: 1.0,
                ScopeHeight: null, TimelineHeight: null, ScopeRatio: null,
                VisualizationScopePosition.Top,
                VisualizationChannelFilter.All,
                VisualizationGroupBy.None));
        return (layout, timeline);
    }
}
