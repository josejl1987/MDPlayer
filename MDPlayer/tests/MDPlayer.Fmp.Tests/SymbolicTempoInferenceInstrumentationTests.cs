#nullable enable

using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TI-INSTRUMENT: verifies the opt-in TempoInferenceCounters are measurable and
/// that instrumenting does not change the inference result (same winner, same
/// diagnostics) versus the default build path.
/// </summary>
public sealed class SymbolicTempoInferenceInstrumentationTests
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

    private static VisualizationTimeline Timeline(int beats)
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = Enumerable.Range(0, beats)
            .Select(i => NewNote((long)Math.Round(i * spq), (long)Math.Round(i * spq) + 4410, 60 + i % 12))
            .ToArray();
        return new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = notes.Last().EndSample,
            Notes = notes,
            Beats = Array.Empty<BeatEvent>(),
            Timing = Array.Empty<DriverTimingEvent>(),
        };
    }

    [Fact]
    public void InstrumentedResult_MatchesDefault_AndCountersAreMeasurable()
    {
        VisualizationTimeline timeline = Timeline(beats: 64);
        var options = new MusicalTimeMapOptions { QuartersPerBeat = 1, DetectTempoChanges = true };

        MusicalTimeMapBuildResult reference = SymbolicTempoInference.Build(timeline, options, beatOffsetQuarter: null);
        MusicalTimeMapBuildResult instrumented = SymbolicTempoInference.Build(
            timeline, options, beatOffsetQuarter: null, out TempoInferenceCounters counters);

        // Output identical: same selected BPM, same phase sample, same scores.
        Assert.NotNull(reference.Diagnostics.SelectedBpm);
        Assert.Equal(reference.Diagnostics.SelectedBpm, instrumented.Diagnostics.SelectedBpm);
        Assert.Equal(reference.Diagnostics.SelectedScore, instrumented.Diagnostics.SelectedScore);
        Assert.Equal(reference.Diagnostics.PhaseSample, instrumented.Diagnostics.PhaseSample);
        Assert.Equal(reference.Diagnostics.AnchorCount, instrumented.Diagnostics.AnchorCount);

        // Counters are populated and meaningful.
        Assert.True(counters.OnsetCount > 0, "onset count should be positive");
        Assert.True(counters.ScoreForPhaseCalls > 0, "ScoreForPhase should be invoked");
        Assert.True(counters.SubdivisionFitEvals >= counters.ScoreForPhaseCalls,
            "each phase scores at least one SubdivisionFit evaluation");
        Assert.Equal(timeline.Notes!.Count, counters.OnsetCount);
    }
}