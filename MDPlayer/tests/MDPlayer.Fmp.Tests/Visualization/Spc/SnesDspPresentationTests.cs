using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>PR 7: SNES S-DSP presentation metadata (§17-§21, §27).</summary>
public sealed class SnesDspPresentationTests
{
    [Fact]
    public void Geometry1080p_MatchesSpecification()
    {
        SnesDspLayoutGeometry g = SnesDspPresentation.Geometry1080p;
        Assert.Equal(1920, g.W);
        Assert.Equal(1080, g.H);
        Assert.Equal(64, g.TopBar);
        Assert.Equal(72, g.BottomBar);
        Assert.Equal(944, g.GridHeight);
        Assert.Equal(480, g.PanelW);
        Assert.Equal(472, g.PanelH);
        Assert.Equal(28, g.HeaderH);
        Assert.Equal(180, g.ScopeH);
        Assert.Equal(3, g.DividerH);
        Assert.Equal(245, g.PitchLaneH);
    }

    [Fact]
    public void Geometry720p_MatchesSpecification()
    {
        SnesDspLayoutGeometry g = SnesDspPresentation.Geometry720p;
        Assert.Equal(1440, g.W);
        Assert.Equal(720, g.H);
        Assert.Equal(44, g.TopBar);
        Assert.Equal(48, g.BottomBar);
        Assert.Equal(628, g.GridHeight);
        Assert.Equal(360, g.PanelW);
        Assert.Equal(314, g.PanelH);
        Assert.Equal(18, g.HeaderH);
        Assert.Equal(116, g.ScopeH);
        Assert.Equal(2, g.DividerH);
        Assert.Equal(164, g.PitchLaneH);
    }

    [Fact]
    public void BuildHeader_OnlyActiveBadgesAppear_InFixedOrder()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            voiceIndex: 0, sourceNumber: 23, shortHash: "A1B2C3",
            noise: true, pitchMod: true, echoSend: false,
            volLeft: 127, volRight: 127);

        Assert.Equal(new[] { "[PM]", "[N]" }, header.Badges);
    }

    [Fact]
    public void BuildHeader_NoActiveBadges_YieldsEmptyBadgeList()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            0, 23, "A1B2C3", noise: false, pitchMod: false, echoSend: false, 127, 127);
        Assert.Empty(header.Badges);
    }

    [Fact]
    public void BuildHeader_PanFormula_HardLeftIsMinusOne()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            0, 1, "A1B2C3", false, false, false, volLeft: 127, volRight: 0);
        Assert.Equal(-1.0, header.Pan, 10);
    }

    [Fact]
    public void BuildHeader_PanFormula_HardRightIsPlusOne()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            0, 1, "A1B2C3", false, false, false, volLeft: 0, volRight: 127);
        Assert.Equal(1.0, header.Pan, 10);
    }

    [Fact]
    public void BuildHeader_PanFormula_CenteredWhenVolumesEqual()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            0, 1, "A1B2C3", false, false, false, volLeft: 100, volRight: 100);
        Assert.Equal(0.0, header.Pan, 10);
        Assert.False(header.PhaseMarker);
    }

    [Fact]
    public void BuildHeader_PhaseMarker_WhenVolumesHaveOppositeSigns()
    {
        // Surround: equal energy, inverted right channel.
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            0, 2, "1A2B3C", false, false, false, volLeft: 127, volRight: -127);
        Assert.True(header.PhaseMarker);
        Assert.Equal(0.0, header.Pan, 10);
    }

    [Fact]
    public void BuildHeader_Label_MatchesTokenFormat()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            voiceIndex: 0, sourceNumber: 23, shortHash: "A1B2C3",
            noise: true, pitchMod: true, echoSend: true,
            volLeft: 127, volRight: 127);
        Assert.Equal("V1  SRC 23 · A1B2C3  [PM] [N] [E]  L◀●▶R", header.Label);
    }

    [Fact]
    public void BuildHeader_Label_AppendsPhaseMarkerForSurround()
    {
        SnesDspPanelHeader header = SnesDspPresentation.BuildHeader(
            voiceIndex: 3, sourceNumber: 5, shortHash: "1A2B3C",
            noise: true, pitchMod: false, echoSend: false,
            volLeft: 127, volRight: -127);
        Assert.Equal("V4  SRC 05 · 1A2B3C  [N]  L◀●▶RØ", header.Label);
    }

    [Fact]
    public void Topology_SnesDsp_BuildsFixedFourByTwoVoiceOrder()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.SnesDsp();
        var timeline = new VisualizationTimeline
        {
            SampleRate = 32_000,
            StartSample = 0,
            EndSample = 1_000,
            Devices = [device],
            // Deliberately reversed input: the fixed grid must still come out
            // as VOICE 1..8, never reordered by input order or activity.
            Voices = VisualizationDeviceCatalog.SnesDspVoices().Reverse().ToArray(),
        };

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(timeline);

        Assert.Equal(8, topology.Panels.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal($"VOICE {i + 1}", topology.Panels[i].Label);
            Assert.Equal(PreparedPanelKind.Pitched, topology.Panels[i].Kind);
            Assert.Equal(PanelContentKind.SingleVoice, topology.Panels[i].Content);
            Assert.Equal(i, topology.Panels[i].Order);
        }
    }

    [Fact]
    public void Topology_NonSnesDspTimeline_IsNotSpecialCased()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        var timeline = new VisualizationTimeline
        {
            SampleRate = 8_000,
            StartSample = 0,
            EndSample = 1_000,
            Devices = [device],
            Voices = VisualizationDeviceCatalog.Ym2608Voices(),
        };

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(timeline);

        // The generic FMP path is unchanged for non-S-DSP timelines.
        Assert.Equal(12, topology.Panels.Count);
        Assert.Equal("FM1", topology.Panels[0].Label);
    }

    [Theory]
    [InlineData(PitchAccuracy.Exact)]
    [InlineData(PitchAccuracy.Estimated)]
    public void PitchLane_ExactAndEstimated_AreChromaticGrid(PitchAccuracy accuracy)
    {
        SnesDspPitchLane lane = SnesDspPresentation.PitchLaneFor(accuracy);
        Assert.Equal(accuracy, lane.Accuracy);
        Assert.True(lane.ChromaticGrid);
        Assert.Equal("", lane.Header);
    }

    [Fact]
    public void PitchLane_Relative_IsSemitoneOffsetWithRelHeader()
    {
        SnesDspPitchLane lane = SnesDspPresentation.PitchLaneFor(PitchAccuracy.Relative);
        Assert.Equal(PitchAccuracy.Relative, lane.Accuracy);
        Assert.False(lane.ChromaticGrid);
        Assert.Equal("REL", lane.Header);
        Assert.Equal(new[] { "-12", "0", "+12" }, lane.AxisLabels);
    }

    [Fact]
    public void PitchLane_Unpitched_IsActivityLane()
    {
        SnesDspPitchLane lane = SnesDspPresentation.PitchLaneFor(PitchAccuracy.Unpitched);
        Assert.Equal(PitchAccuracy.Unpitched, lane.Accuracy);
        Assert.False(lane.ChromaticGrid);
        Assert.Equal("", lane.Header);
        Assert.Empty(lane.AxisLabels);
    }

    [Fact]
    public void EchoStrip_DerivesEnabledStateFromRegisters()
    {
        SnesDspEchoStripState state = SnesDspPresentation.DeriveEchoStrip(
            efb: 0x20, edl: 5, eon: 0xFF, flg: 0x00);
        Assert.True(state.Enabled);
        Assert.True(state.FirActive);
        Assert.Equal(32, state.Feedback); // 0x20 -> +32
        Assert.Equal(5, state.Delay);
    }

    [Fact]
    public void EchoStrip_EchoResetFlag_DisablesEchoBus()
    {
        SnesDspEchoStripState state = SnesDspPresentation.DeriveEchoStrip(
            efb: 0x20, edl: 5, eon: 0xFF, flg: 0x10);
        Assert.False(state.Enabled);
        Assert.False(state.FirActive);
    }

    [Fact]
    public void EchoStrip_NoVoicesRouted_KeepsFirButDisablesStrip()
    {
        SnesDspEchoStripState state = SnesDspPresentation.DeriveEchoStrip(
            efb: 0x00, edl: 4, eon: 0x00, flg: 0x00);
        Assert.False(state.Enabled);
        Assert.True(state.FirActive);
    }

    [Fact]
    public void EchoStrip_Feedback_IsSignedSevenBitValue()
    {
        Assert.Equal(63, SnesDspPresentation.DeriveEchoStrip(0x3F, 1, 1, 0).Feedback);
        Assert.Equal(-64, SnesDspPresentation.DeriveEchoStrip(0x40, 1, 1, 0).Feedback);
        Assert.Equal(-1, SnesDspPresentation.DeriveEchoStrip(0x7F, 1, 1, 0).Feedback);
        Assert.Equal(-32, SnesDspPresentation.DeriveEchoStrip(0x60, 1, 1, 0).Feedback);
        Assert.Equal(0, SnesDspPresentation.DeriveEchoStrip(0x00, 1, 1, 0).Feedback);
    }
}
