#nullable enable

using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Focused tests for <see cref="MusicalStructureAnalyzer"/>: meter-gated empty result,
/// repeated-block loop detection at 4/8/16/24 bars, section labeling with prime repeats,
/// and the rest-bar guard that prevents empty material from fabricating loops.
/// </summary>
public sealed class MusicalStructureAnalyzerTests
{
    private const int Sr = 44_100;

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

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        SampleRate = Sr,
        StartSample = 0,
        Notes = notes,
    };

    [Fact]
    public void Analyze_NoMeter_ReturnsEmptyStructure()
    {
        var map = Map(16); // meter: null
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, Timeline(Note(60, 0)));

        Assert.False(structure.HasSections);
        Assert.False(structure.HasLoop);
        Assert.Empty(structure.Bars);
        Assert.Empty(structure.Sections);
        Assert.Empty(structure.Loops);
        Assert.Null(structure.PrimaryLoop);
    }

    [Fact]
    public void Analyze_RepeatedEightBarPhrase_DetectsFundamentalLoop()
    {
        // 16 bars: an 8-bar phrase (distinct pitch class per bar) repeated verbatim.
        NoteEvent[] notes = Enumerable.Range(0, 16)
            .Select(bar => Note(48 + (bar % 8), 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(Map(64, new Meter(4, 4)), Timeline(notes));

        Assert.True(structure.HasLoop);
        Assert.NotNull(structure.PrimaryLoop);
        Assert.Equal(8, structure.PrimaryLoop!.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop.StartBar);
        Assert.Equal(16, structure.Bars.Count);
    }

    [Fact]
    public void Analyze_AbabForm_ProducesPrimePhraseLabelsAndLoop()
    {
        // A A A A | B B B B | A A A A | B B B B  (4-bar phrases, 16 bars total).
        // A bars use pitch class 0 (MIDI 60); B bars use pitch class 5 (MIDI 65).
        NoteEvent[] notes = Enumerable.Range(0, 16)
            .Select(bar => Note(bar % 8 < 4 ? 60 : 65, 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(Map(64, new Meter(4, 4)), Timeline(notes));

        Assert.Equal(4, structure.Sections.Count);
        Assert.Equal(new MusicalSection(0, 4, "PHRASE_A"), structure.Sections[0]);
        Assert.Equal(new MusicalSection(4, 8, "PHRASE_B"), structure.Sections[1]);
        Assert.Equal(new MusicalSection(8, 12, "PHRASE_A"), structure.Sections[2]);
        Assert.Equal(new MusicalSection(12, 16, "PHRASE_B"), structure.Sections[3]);

        // A+B repeats, so the fundamental loop is the 8-bar A-B span.
        Assert.True(structure.HasLoop);
        Assert.NotNull(structure.PrimaryLoop);
        Assert.Equal(8, structure.PrimaryLoop!.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop.StartBar);
    }

    [Fact]
    public void Analyze_AllRest_ReturnsNoLoop()
    {
        // No notes: every bar is a rest. Rest↔rest must not fabricate a loop.
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(Map(64, new Meter(4, 4)), Timeline());

        Assert.False(structure.HasLoop);
        Assert.Empty(structure.Loops);
        Assert.Null(structure.PrimaryLoop);
        Assert.Single(structure.Sections); // one homogeneous rest section
        Assert.Equal("PHRASE_A", structure.Sections[0].Label);
    }
    [Fact]
    public void Analyze_ArbitraryThirtyThreeBarLoop_UsesRepeatedContentAndPhraseOrder()
    {
        const int bars = 66;
        NoteEvent[] notes = Enumerable.Range(0, bars)
            .Select(bar => Note(48 + (bar % 33) % 12, 4 * bar))
            .ToArray();
        double spq = Spq;
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(bars * 4 * spq),
            Notes = notes,
            LoopMarkers = new[]
            {
                new LoopMarker((long)Math.Round(3 * spq), LoopMarkerKind.Restart, 1),
                new LoopMarker((long)Math.Round(3 * spq + 33 * 4 * spq), LoopMarkerKind.Restart, 2),
            },
        };

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(bars * 4, new Meter(4, 4)), timeline);

        Assert.Equal(33, structure.PrimaryLoop?.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop?.StartBar);
        Assert.Contains(structure.Sections, section => section.Label == "TURNAROUND");
        Assert.All(structure.Sections, section =>
            Assert.True(section.EndBar - section.StartBar >= 4
                || section.Label == "TURNAROUND"));
    }

    [Fact]
    public void GridCandidate_PhaseVariantsProduceDifferentDownbeatSamplePositions()
    {
        const double bpm = 120;
        double samplesPerQuarter = Sr * 60.0 / bpm;
        MusicalGridCandidate[] candidates =
        {
            new(bpm, new Meter(4, 4), 0.0, 0.0, 1.0),
            new(bpm, new Meter(4, 4), 0.0, 1.0, 1.0),
            new(bpm, new Meter(4, 4), 0.0, 2.0, 1.0),
            new(bpm, new Meter(4, 4), 0.0, 3.0, 1.0),
        };

        long[] downbeatSamples = candidates
            .Select(candidate => (long)Math.Round(
                (candidate.FirstDownbeatQuarter!.Value - candidate.QuarterAtSourceStart)
                * samplesPerQuarter))
            .ToArray();

        Assert.Equal(
            new[]
            {
                0L,
                (long)Math.Round(samplesPerQuarter),
                (long)Math.Round(2 * samplesPerQuarter),
                (long)Math.Round(3 * samplesPerQuarter),
            },
            downbeatSamples);
        Assert.Equal(4, downbeatSamples.Distinct().Count());
    }

    [Fact]
    public void SelectGrid_UsesOnsetAndRestartEvidenceToResolveHalfTempo()
    {
        const double bpm = 149.4;
        double spq = Sr * 60.0 / bpm;
        const int bars = 66;
        long end = (long)Math.Round(bars * 4 * spq);
        var notes = Enumerable.Range(0, bars * 16)
            .Select(index =>
            {
                long start = (long)Math.Round(index * 0.25 * spq);
                return new NoteEvent(
                    ChannelId: "v",
                    StartSample: start,
                    EndSample: start + Math.Max(1, (long)Math.Round(0.125 * spq)),
                    InitialFrequencyHz: 440,
                    InitialMidiNote: 48 + (index / 16 % 33) % 12,
                    InstrumentId: "inst",
                    Mode: VisualizationNoteMode.Fm,
                    IsRetrigger: false,
                    Pitch: Array.Empty<PitchChange>());
            })
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = end,
            Notes = notes,
            LoopMarkers = new[]
            {
                new LoopMarker(0, LoopMarkerKind.Restart, 1),
                new LoopMarker((long)Math.Round(33 * 4 * spq), LoopMarkerKind.Restart, 2),
            },
        };
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, end, 0, Sr * 60.0 / (bpm * 2), bpm * 2,
                    TimingSource.SymbolicInference, 0.2),
            },
            meter: new Meter(4, 4),
            firstDownbeatQuarter: 0,
            confidence: 0.2,
            alternateBpm: bpm,
            isTempoAmbiguous: true,
            gridCandidates: new[]
            {
                new MusicalGridCandidate(bpm * 2, new Meter(4, 4), 0, 1),
                new MusicalGridCandidate(bpm, new Meter(4, 4), 0, 1),
            });

        MusicalTimeMap selected = MusicalStructureAnalyzer.SelectGrid(map, timeline);
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(selected, timeline);

        Assert.Equal(bpm, selected.Segments[0].BeatsPerMinute, precision: 6);
        Assert.Equal(33, structure.PrimaryLoop?.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop?.StartBar);
    }
}