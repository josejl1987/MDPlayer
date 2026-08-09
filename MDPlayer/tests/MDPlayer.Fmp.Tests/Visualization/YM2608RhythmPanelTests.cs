using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Patch-2 YM2608 rhythm renderer mapping. The rhythm sub-channel of the
/// OPNA maps to a percussion-rows panel, and each distinct rhythm instrument
/// present in the timeline (BD, SD, TOP, HH, TOM, RIM) becomes its own row so
/// the renderer draws one lane per instrument instead of a single merged lane.
/// </summary>
public sealed class YM2608RhythmPanelTests
{
    private sealed record Rhythms(VisualizationTimeline Timeline, VisualizationTopology Topology, VisualizationPanel Panel);

    private static Rhythms BuildRhythms(params string[] instruments)
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        VoiceDescriptor[] voices = VisualizationDeviceCatalog.Ym2608Voices().ToArray();

        var timeline = new VisualizationTimeline
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [device],
            Voices = voices,
            Notes = Array.Empty<NoteEvent>(),
            Rhythm = instruments
                .Select((instrument, index) => new RhythmEvent(
                    instrument,
                    $"ym2608.0.rhythm.{instrument}",
                    500 + index * 60,
                    Strength: 0.8f,
                    Pan: 0,
                    ParentVoiceId: "ym2608.0.rhythm",
                    InstrumentId: $"rhythm:{instrument}"))
                .ToArray(),
        };

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline, VisualizationChannelFilter.Active);

        VisualizationPanel rhythm = Assert.Single(
            topology.Panels, panel => panel.Kind == PreparedPanelKind.Rhythm);
        return new Rhythms(timeline, topology, rhythm);
    }

    [Fact]
    public void RhythmmPanel_UsesPercussionRowsSchema()
    {
        Rhythms r = BuildRhythms("bd", "hh");
        Assert.Equal(PanelPresentationSchema.PercussionRows, r.Panel.Schema);
    }

    [Fact]
    public void EachRhythmInstrument_IsItsOwnRow()
    {
        string[] instruments = ["bd", "sd", "top", "hh", "tom", "rim"];
        Rhythms r = BuildRhythms(instruments);

        // One row per instrument, in the presentation's stable order.
        string[] rowIds = r.Panel.Rows.Select(row => row.Id).ToArray();
        Assert.Equal(instruments.Length, rowIds.Length);
        foreach (string instrument in instruments)
            Assert.Contains(rowIds, id => id == instrument);
    }

    [Fact]
    public void RhythmRows_AreTriggerLanes()
    {
        Rhythms r = BuildRhythms("bd", "hh", "tom");
        Assert.All(r.Panel.Rows, row => Assert.Equal(VisualizationRowKind.Trigger, row.Kind));
    }

    [Fact]
    public void RhythmPanel_AlwaysCarriesCompleteKitAsSeparateRows()
    {
        // YM2608 exposes all six rhythm channels as first-class percussion rows;
        // the panel always presents the complete kit, and events only land on
        // the instrument row that fired.
        string[] kit = ["bd", "sd", "top", "hh", "tom", "rim"];
        Rhythms r = BuildRhythms("hh", "rim");

        string[] rowIds = r.Panel.Rows.Select(row => row.Id).ToArray();
        Assert.Equal(kit.Length, rowIds.Length);
        foreach (string instrument in kit)
            Assert.Contains(rowIds, id => id == instrument);
    }

    [Fact]
    public void RhythmPanelCarriesEvents_WithoutPitchedNotes()
    {
        Rhythms r = BuildRhythms("bd", "sd", "hh");

        // Scene preparation gathers every rhythm event owned by the panel into
        // its prepared lane data, even though the timeline has no pitched notes.
        OverlayScene scene = OverlaySceneBuilder.Build(
            r.Timeline,
            new OverlayLayout(960, 540, 0.75, 2.25, r.Topology.Panels.Count,
                VisualizationLayoutMode.Diagnostic),
            r.Topology);

        PreparedPanel prepared = Assert.Single(scene.Panels, p => p.Id == r.Panel.Id);
        Assert.Equal(PanelPresentationSchema.PercussionRows, prepared.Schema);
        Assert.Equal(3, prepared.Rhythm.Length);
    }
}
