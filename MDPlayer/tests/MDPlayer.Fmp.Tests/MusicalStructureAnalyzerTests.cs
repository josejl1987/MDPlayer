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
    public void Analyze_AbabForm_ProducesPrimeLabelsAndLoop()
    {
        // A A A A | B B B B | A A A A | B B B B  (4-bar phrases, 16 bars total).
        // A bars use pitch class 0 (MIDI 60); B bars use pitch class 5 (MIDI 65).
        NoteEvent[] notes = Enumerable.Range(0, 16)
            .Select(bar => Note(bar % 8 < 4 ? 60 : 65, 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(Map(64, new Meter(4, 4)), Timeline(notes));

        Assert.Equal(4, structure.Sections.Count);
        Assert.Equal(new MusicalSection(0, 4, "A"), structure.Sections[0]);
        Assert.Equal(new MusicalSection(4, 8, "B"), structure.Sections[1]);
        Assert.Equal(new MusicalSection(8, 12, "A"), structure.Sections[2]);
        Assert.Equal(new MusicalSection(12, 16, "B"), structure.Sections[3]);

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
        Assert.Equal("A", structure.Sections[0].Label);
    }
}