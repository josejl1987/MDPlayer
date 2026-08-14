#nullable enable

using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Exporter integration for <see cref="MusicalStructure"/> markers: the conductor
/// must carry SECTION_* markers at labeled section boundaries and STRUCT_LOOP_* markers
/// at the fundamental loop bounds, derived from the structure passed in — the exporter
/// only encodes, it never re-derives structure.
/// </summary>
public sealed class MusicalStructureMidiExportTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    // 120 BPM ⇒ 22050 samples per quarter.
    private const double Spq = Sr * 60.0 / 120.0;

    private static MusicalTimeMap Map(int quarters, Meter? meter = null)
    {
        long end = (long)Math.Round(quarters * Spq);
        return new MusicalTimeMap(Sr, 0, new[]
        {
            new TempoSegment(0, end, 0.0, Spq, 120, TimingSource.UserOverride, 1.0),
        }, meter: meter, firstDownbeatQuarter: 0.0);
    }

    private static NoteEvent Note(int midi, int quarter)
    {
        long start = (long)Math.Round(quarter * Spq);
        return new NoteEvent(
            ChannelId: "v",
            StartSample: start,
            EndSample: start + (long)Math.Round(0.5 * Spq),
            InitialFrequencyHz: 440,
            InitialMidiNote: midi,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());
    }

    private static VisualizationTimeline Timeline(int quarters, params NoteEvent[] notes) => new()
    {
        SampleRate = Sr,
        StartSample = 0,
        EndSample = (long)Math.Round(quarters * Spq),
        Notes = notes,
    };

    private static (long Tick, string Text)[] Markers(byte[] bytes) =>
        MidiRoundTrip.TimedEvents(bytes, 0)
            .Where(pair => pair.Event is MarkerEvent)
            .Select(pair => (pair.Tick, ((MarkerEvent)pair.Event).Text))
            .ToArray();

    [Fact]
    public void Export_AbabForm_EmitsSectionAndLoopMarkers()
    {
        // A A A A | B B B B | A A A A | B B B B  (4-bar phrases, 16 bars).
        NoteEvent[] notes = Enumerable.Range(0, 16)
            .Select(bar => Note(bar % 8 < 4 ? 60 : 65, 4 * bar))
            .ToArray();
        var map = Map(64, new Meter(4, 4));
        var timeline = Timeline(64, notes);
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);

        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Structure = structure,
        };
        (long Tick, string Text)[] markers = Markers(exporter.Export(timeline).Bytes);

        // Every labeled section is represented, in analyzer order.
        string[] sectionMarkers = markers.Where(m => m.Text.StartsWith("SECTION_")).Select(m => m.Text).ToArray();
        Assert.Equal(structure.Sections.Select(s => "SECTION_" + s.Label), sectionMarkers);

        Assert.True(structure.HasLoop);
        (long Tick, string Text) loopStart = markers.Single(m => m.Text == "STRUCT_LOOP_START");
        (long Tick, string Text) loopEnd = markers.Single(m => m.Text == "STRUCT_LOOP_END");

        // Loop bounds map to the repeated-block span: bar 0 and the bar where the
        long sectionA = markers.First(m => m.Text == "SECTION_" + structure.Sections[0].Label).Tick;
        long sectionARepeat = markers.Where(m => m.Text == "SECTION_" + structure.Sections[2].Label)
            .ElementAt(1).Tick;
        Assert.Equal(0, loopStart.Tick);
        Assert.Equal(sectionA, loopStart.Tick);
        Assert.Equal(sectionARepeat, loopEnd.Tick);

        // Linear bar→tick: bar N (4/4) starts at quarter 4N ⇒ tick 4N·ppq.
        Assert.Equal(4 * 8 * Ppq, loopEnd.Tick);
    }

    [Fact]
    public void Export_AllRest_EmitsSectionMarkerButNoLoopMarkers()
    {
        var map = Map(64, new Meter(4, 4));
        var timeline = Timeline(64);
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);

        Assert.False(structure.HasLoop);
        Assert.Single(structure.Sections);

        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Structure = structure,
        };
        (long Tick, string Text)[] markers = Markers(exporter.Export(timeline).Bytes);

        Assert.Contains(markers, m => m.Text == "SECTION_A");
        Assert.DoesNotContain(markers, m => m.Text.StartsWith("STRUCT_LOOP_"));
    }

    [Fact]
    public void Export_NoStructure_EmitsBaseMarkersOnly()
    {
        var map = Map(16, new Meter(4, 4));
        var timeline = Timeline(16, Note(60, 0));
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true });
        string[] markerTexts = Markers(exporter.Export(timeline).Bytes).Select(m => m.Text).ToArray();

        Assert.Contains("SOURCE_START", markerTexts);
        Assert.Contains("RENDER_END", markerTexts);
        Assert.DoesNotContain(markerTexts, m => m.StartsWith("SECTION_"));
        Assert.DoesNotContain(markerTexts, m => m.StartsWith("STRUCT_LOOP_"));
    }
}