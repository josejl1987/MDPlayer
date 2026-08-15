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
/// must carry PHRASE_* markers for analyzer phrase boundaries and STRUCT_LOOP_*
/// markers at the fundamental loop bounds, derived from the structure passed in —
/// the exporter only encodes, it never re-derives structure.
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
    public void Export_AbabForm_EmitsPhraseAndLoopMarkers()
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

        // Every labeled phrase is represented, in analyzer order. Sections emit
        // their own SECTION_* markers.
        string[] phraseMarkers = markers.Where(m => m.Text.StartsWith("PHRASE_")).Select(m => m.Text).ToArray();
        Assert.Equal(structure.Phrases.Select(p => p.Label), phraseMarkers);

        Assert.True(structure.HasLoop);
        (long Tick, string Text) loopStart = markers.Single(m => m.Text == "STRUCT_LOOP_START");
        (long Tick, string Text) loopEnd = markers.Single(m => m.Text == "STRUCT_LOOP_END");

        long sectionA = markers.First(m => m.Text == structure.Phrases[0].Label).Tick;
        long sectionARepeat = markers.Where(m => m.Text == structure.Phrases[2].Label)
            .ElementAt(1).Tick;
        Assert.Equal(0, loopStart.Tick);
        Assert.Equal(sectionA, loopStart.Tick);
        Assert.Equal(sectionARepeat, loopEnd.Tick);

        // Phrases and sections are separate form levels: every section marker is
        // SECTION_<bare label> and coexists with its PHRASE_* phrase marker.
        Assert.Equal(4, markers.Count(m => m.Text.StartsWith("SECTION_")));
        Assert.Equal("SECTION_A", markers.Where(m => m.Text.StartsWith("SECTION_"))
            .ElementAt(0).Text);

        // Linear bar→tick: bar N (4/4) starts at quarter 4N ⇒ tick 4N·ppq.
        Assert.Equal(4 * 8 * Ppq, loopEnd.Tick);
    }

    [Fact]
    public void StructureBoundaryTick_ResolvesFinalBarAndThrowsOutsideRange()
    {
        // 16 bars of ABAB material; boundary == Bars.Count means the END of the
        // final bar (spec P0-9 final-bar semantics), boundaries outside
        // [0, Bars.Count] must throw instead of indexing past the array.
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

        Assert.Equal(0, exporter.StructureBoundaryTick(structure, 0));
        Assert.Equal(64 * Ppq, exporter.StructureBoundaryTick(structure, structure.Bars.Count));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => exporter.StructureBoundaryTick(structure, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => exporter.StructureBoundaryTick(structure, structure.Bars.Count + 1));
    }

    [Fact]
    public void Export_LoopEndingAtFinalBar_EmitsStructLoopEndWithoutCrash()
    {
        // Source loop of 33 bars whose restart markers sit at bars 33 and 66 of a
        // 66-bar capture: the detected loop starts at bar 33 and its END boundary
        // equals Bars.Count — the final bar. The exporter must resolve that
        // boundary to the end of the last bar (never index past the array) and
        // still emit STRUCT_LOOP_START/END.
        const int bars = 66;
        NoteEvent[] notes = Enumerable.Range(0, bars)
            .Select(bar => Note(48 + (bar % 33) % 12, 4 * bar))
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(bars * 4 * Spq),
            Notes = notes,
            LoopMarkers = new[]
            {
                new LoopMarker((long)Math.Round(33 * 4 * Spq), LoopMarkerKind.Restart, 1),
                new LoopMarker((long)Math.Round(66 * 4 * Spq), LoopMarkerKind.Restart, 2),
            },
        };
        var map = Map(bars * 4, new Meter(4, 4));
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);

        // The final-bar loop is source boundary-supported (its second pass lies
        // outside the capture) — never fabricated content, and never a crash.
        Assert.True(structure.HasLoop);
        Assert.Equal(33, structure.PrimaryLoop!.StartBar);
        Assert.Equal(33, structure.PrimaryLoop.LengthBars);
        Assert.Equal(bars, structure.PrimaryLoop.StartBar + structure.PrimaryLoop.LengthBars);
        Assert.False(structure.PrimaryLoop.ContentValidated);

        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Structure = structure,
        };
        (long Tick, string Text)[] markers = Markers(exporter.Export(timeline).Bytes);

        (long Tick, string Text) loopStart = markers.Single(m => m.Text == "STRUCT_LOOP_START");
        (long Tick, string Text) loopEnd = markers.Single(m => m.Text == "STRUCT_LOOP_END");
        Assert.Equal(33 * 4 * Ppq, loopStart.Tick);
        Assert.Equal(66 * 4 * Ppq, loopEnd.Tick);
    }

    [Fact]
    public void Export_AllRest_EmitsNoPhraseOrSectionMarkers()
    {
        var map = Map(64, new Meter(4, 4));
        var timeline = Timeline(64);
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);

        Assert.False(structure.HasLoop);
        Assert.Empty(structure.Phrases);
        Assert.Empty(structure.Sections);

        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Structure = structure,
        };
        (long Tick, string Text)[] markers = Markers(exporter.Export(timeline).Bytes);

        // Rest-only material has no musical form: no PHRASE_*, no SECTION_*,
        // no PICKUP, and never fabricated loop markers (spec P1-12).
        Assert.DoesNotContain(markers, m => m.Text.StartsWith("PHRASE_"));
        Assert.DoesNotContain(markers, m => m.Text.StartsWith("SECTION_"));
        Assert.DoesNotContain(markers, m => m.Text == "PICKUP");
        Assert.DoesNotContain(markers, m => m.Text.StartsWith("STRUCT_LOOP_"));
    }

    [Fact]
    public void Export_LateDownbeat_EmitsPickupMarkerPreservingDistances()
    {
        // Downbeat at quarter 2 with a pickup note at quarter 0: the optional
        // PICKUP marker must be emitted at the pickup bar start (downbeat - 4
        // quarters = -2), the origin shift keeps every tick nonnegative, and the
        // downbeat-to-pickup distance survives the shift untouched.
        long end = (long)Math.Round(34 * Spq);
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, end, 0.0, Spq, 120, TimingSource.UserOverride, 1.0),
            },
            meter: new Meter(4, 4),
            firstDownbeatQuarter: 2.0);
        NoteEvent[] notes = Enumerable.Range(0, 8)
            .Select(bar => Note(60, 2 + 4 * bar))
            .Prepend(Note(55, 0))
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = end,
            Notes = notes,
        };
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Structure = structure,
        };
        (long Tick, string Text)[] markers = Markers(exporter.Export(timeline).Bytes);

        (long Tick, string Text) pickup = markers.Single(m => m.Text == "PICKUP");
        (long Tick, string Text) downbeat = markers.Single(m => m.Text == "FIRST_DOWNBEAT");
        Assert.True(pickup.Tick >= 0, "PICKUP tick must be nonnegative after origin shift");
        Assert.True(downbeat.Tick >= 0, "FIRST_DOWNBEAT tick must be nonnegative after origin shift");
        // Downbeat (quarter 2) is 4 quarters after the pickup bar start (quarter -2).
        Assert.Equal(4 * Ppq, downbeat.Tick - pickup.Tick);
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
        Assert.DoesNotContain(markerTexts, m => m.StartsWith("PHRASE_"));
        Assert.DoesNotContain(markerTexts, m => m.StartsWith("STRUCT_LOOP_"));
    }
}