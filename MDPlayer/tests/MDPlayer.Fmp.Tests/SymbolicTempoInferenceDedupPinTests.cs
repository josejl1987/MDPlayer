#nullable enable

using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TI-DEDUP-PIN: freezes the CURRENT same-sample note accumulation semantics of
/// <see cref="SymbolicTempoInference.CollectOnsets"/>.
///
/// PINNED BEHAVIOR (do not "fix"): notes are filtered with
/// <c>seen.Contains(note.StartSample)</c> WITHOUT adding to the set, so notes are
/// NOT deduped against each other. Two or more notes sharing a StartSample are all
/// emitted as separate onsets (each weight 0.6), and Search() later groups
/// same-sample onsets and sums their weights — a chord accumulates polyphony
/// weight (3 simultaneous notes → 1.8). This is a deliberate freeze to back the
/// future A/B decision (binary onset vs polyphony-weight), NOT a bug to fix.
/// </summary>
public sealed class SymbolicTempoInferenceDedupPinTests
{
    private const int Sr = 44_100;

    private static NoteEvent NewNote(long start, long end, int midi)
        => new(
            ChannelId: "ym2608.0.fm.1",
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 440,
            InitialMidiNote: midi,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(params NoteEvent[] notes)
        => new()
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = notes.Max(n => n.EndSample),
            Notes = notes,
            Beats = Array.Empty<BeatEvent>(),
            Timing = Array.Empty<DriverTimingEvent>(),
        };

    [Fact]
    public void SameSampleNotes_EmitAsSeparateOnsets_WithAccumulatingWeight()
    {
        const long chordSample = 1000;
        const long soloSample = 5000;

        // Chord: three notes sharing one StartSample, plus a contrast note on its own.
        VisualizationTimeline timeline = Timeline(
            NewNote(chordSample, chordSample + 4410, 60),
            NewNote(chordSample, chordSample + 4410, 64),
            NewNote(chordSample, chordSample + 4410, 67),
            NewNote(soloSample, soloSample + 4410, 72));

        SymbolicTempoInference.Onset[] onsets = SymbolicTempoInference.CollectOnsets(timeline);

        // Each note becomes its own onset entry — same-sample notes are NOT deduped.
        Assert.Equal(4, onsets.Length);

        SymbolicTempoInference.Onset[] chord = onsets.Where(o => o.Sample == chordSample).ToArray();
        Assert.Equal(3, chord.Length);
        Assert.All(chord, o => Assert.Equal(0.6, o.Weight));

        // Contrast: the distinct-sample note is a single 0.6 onset.
        SymbolicTempoInference.Onset[] solo = onsets.Where(o => o.Sample == soloSample).ToArray();
        Assert.Single(solo);
        Assert.Equal(0.6, solo[0].Weight);

        // Pinned consequence: Search() sums same-sample weights, so a 3-note chord
        // carries 1.8 of evidence vs 0.6 for a single note.
        Assert.Equal(1.8, chord.Sum(o => o.Weight), precision: 6);
    }

    [Fact]
    public void DistinctSampleNotes_EachEmitOneOnset()
    {
        VisualizationTimeline timeline = Timeline(
            NewNote(0, 4410, 60),
            NewNote(4410, 8820, 64),
            NewNote(8820, 13230, 67));

        SymbolicTempoInference.Onset[] onsets = SymbolicTempoInference.CollectOnsets(timeline);

        Assert.Equal(3, onsets.Length);
        Assert.Equal(0.6, onsets[0].Weight);
        Assert.Equal(0.6, onsets[1].Weight);
        Assert.Equal(0.6, onsets[2].Weight);
    }
}
