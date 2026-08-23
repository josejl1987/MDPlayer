using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>Focused source-time regressions for tatum/beat inference.</summary>
public sealed class MetricalHierarchyInferenceTests
{
    private const int SampleRate = 48_000;
    private const int Ppq = 960;

    [Fact]
    public void StraightSixteenths_SelectFourTatumsPerBeat()
    {
        TimingDiagnostics diagnostics = Build(1_000, 4).Diagnostics;

        Assert.Equal(4, diagnostics.TatumsPerBeat);
        Assert.InRange(diagnostics.TatumDurationSamples!.Value, 999, 1001);
        Assert.InRange(diagnostics.BeatDurationSamples!.Value, 3999, 4001);
    }

    [Fact]
    public void Sextuplets_SelectSixTatumsPerBeat()
    {
        TimingDiagnostics diagnostics = Build(1_000, 6).Diagnostics;

        Assert.Equal(6, diagnostics.TatumsPerBeat);
        Assert.InRange(diagnostics.BeatDurationSamples!.Value, 5999, 6001);
    }

    [Fact]
    public void XaStyle162Bpm_SelectsQuarterBeatInsteadOfThreeToTwoAlias()
    {
        const int sourceRate = 44_100;
        long beat = (long)Math.Round(sourceRate * 60.0 / 162.75);
        long tatum = (long)Math.Round(beat / 4.0);
        MusicalTimeMapBuildResult build = Build(Timeline(tatum, 4, true, sampleRate: sourceRate));

        Assert.Equal(4, build.Diagnostics.TatumsPerBeat);
        Assert.InRange(build.Diagnostics.SelectedBpm!.Value, 160, 165);
        Assert.InRange(build.Map.SampleToTick(tatum, Ppq), 239, 241);
        Assert.InRange(build.Map.SampleToTick(beat, Ppq), 959, 961);
    }

    [Fact]
    public void SymbolicInference_ResolvesThreeToTwoPulseAmbiguityFromMetricAccents()
    {
        TimingDiagnostics diagnostics = Build(1_000, 4).Diagnostics;

        // Raw onset spacing alone supports both interpretations. The structural
        // second voice attacks every four tatums, so metrical salience selects 4.
        Assert.Equal(4, diagnostics.TatumsPerBeat);
        Assert.True(diagnostics.MetricalConfidence > 0.20);
    }

    [Fact]
    public void UnsupportedHalfTatum_DoesNotReplaceDirectlyObservedQuantum()
    {
        VisualizationTimeline timeline = Timeline(1_000, accentEvery: 4, includeAccentVoice: false);
        TimingDiagnostics diagnostics = Build(timeline).Diagnostics;

        Assert.InRange(diagnostics.TatumDurationSamples!.Value, 999, 1001);
    }

    [Fact]
    public void Pickup_PreservesBeatPhaseWithoutRequiringDownbeat()
    {
        VisualizationTimeline timeline = Timeline(
            1_000, accentEvery: 4, includeAccentVoice: true, firstOnset: 500);
        TimingDiagnostics diagnostics = Build(timeline).Diagnostics;

        Assert.Equal(4, diagnostics.TatumsPerBeat);
        Assert.NotEqual(0, diagnostics.BeatPhaseSample);
        Assert.False(diagnostics.DownbeatKnown);
    }

    [Fact]
    public void SymbolicBeat_DerivesTempoAndAbsoluteTicksFromSourceTime()
    {
        const long tatum = 1_000;
        MusicalTimeMapBuildResult build = Build(tatum, 4);
        TimingDiagnostics diagnostics = build.Diagnostics;
        Assert.Equal(4, diagnostics.TatumsPerBeat);

        double bpm = SampleRate * 60.0 / (tatum * 4.0);
        Assert.InRange(diagnostics.SelectedBpm!.Value, bpm - 0.01, bpm + 0.01);
        Assert.Equal(240, build.Map.SampleToTick(1_000, Ppq));
        Assert.Equal(960, build.Map.SampleToTick(4_000, Ppq));
        Assert.Equal(2_400, build.Map.SampleToTick(10_000, Ppq));
    }


    [Fact]
    public void SourceTimeMapping_StaysBoundedAtLateEventsWithoutAccumulatedDrift()
    {
        MusicalTimeMapBuildResult build = Build(Timeline(
            1_000, 4, includeAccentVoice: true, firstOnset: 500));
        int usPerQuarter = build.Map.Segments[0].MicrosecondsPerQuarter;
        double secondsPerTick = usPerQuarter / 1_000_000.0 / Ppq;
        double originSeconds = build.Map.SampleToTick(0, Ppq) * secondsPerTick;

        double maximumError = 0;
        foreach (long sample in new[] { 500L, 1_000L, 4_000L, 10_000L, 90_000L })
        {
            double midiSeconds = build.Map.SampleToTick(sample, Ppq) * secondsPerTick;
            double sourceSeconds = originSeconds + (double)sample / SampleRate;
            maximumError = Math.Max(maximumError, Math.Abs(midiSeconds - sourceSeconds));
        }

        Assert.InRange(maximumError, 0, secondsPerTick + 1e-9);
    }

    [Fact]
    public void SparseSymbolicSource_UsesDeterministicLowConfidenceFallback()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate,
            Notes = Array.Empty<NoteEvent>(),
            Rhythm = Array.Empty<RhythmEvent>(),
            Timing = Array.Empty<DriverTimingEvent>(),
        };
        MusicalTimeMapBuildResult build = Build(timeline);
        MusicalTimeMapBuildResult repeat = Build(timeline);

        Assert.Equal(120, build.Diagnostics.SelectedBpm);
        Assert.Equal(0, build.Diagnostics.MetricalConfidence);
        Assert.False(build.Diagnostics.DownbeatKnown);
        Assert.Equal(0, build.Map.SampleToTick(0, Ppq));
        Assert.Equal(build.Diagnostics.SelectedBpm, repeat.Diagnostics.SelectedBpm);
        Assert.Equal(build.Diagnostics.TatumsPerBeat, repeat.Diagnostics.TatumsPerBeat);
        Assert.Equal(build.Diagnostics.BeatPhase, repeat.Diagnostics.BeatPhase);
    }

    private static MusicalTimeMapBuildResult Build(long tatum, int accentEvery)
        => Build(Timeline(tatum, accentEvery, includeAccentVoice: true));

    private static MusicalTimeMapBuildResult Build(VisualizationTimeline timeline)
        => MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Source = TimingSource.SymbolicInference,
            Meter = null,
            EnableLegacyHierarchyInference = true,
        });

    private static VisualizationTimeline Timeline(
        long tatum,
        int accentEvery,
        bool includeAccentVoice,
        long firstOnset = 0,
        int sampleRate = SampleRate)
    {
        const int count = 96;
        var notes = new List<NoteEvent>(count * (includeAccentVoice ? 2 : 1));
        for (int index = 0; index < count; index++)
        {
            long sample = firstOnset + index * tatum;
            notes.Add(Note("melodic", sample, sample + tatum / 2));
            if (includeAccentVoice && index % accentEvery == 0)
                notes.Add(Note("accent", sample, sample + tatum / 2));
        }
        return new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = firstOnset + count * tatum + 100,
            Notes = notes.ToArray(),
            Rhythm = Array.Empty<RhythmEvent>(),
            Timing = Array.Empty<DriverTimingEvent>(),
        };
    }

    private static NoteEvent Note(string voice, long start, long end) =>
        new(voice, start, end, 440, 60, "inst", VisualizationNoteMode.Fm, false,
            Array.Empty<PitchChange>());
    [Fact]
    public void RhythmRoles_SelectMiddleTempoAliasAndEstablishDownbeat()
    {
        const int sampleRate = 44_100;
        const double expectedBpm = 149.408;
        long tatum = (long)Math.Round(sampleRate * 60.0 / expectedBpm / 4.0);
        var rhythm = new List<RhythmEvent>();
        int count = 264;
        for (int index = 0; index < count; index++)
        {
            long sample = index * tatum;
            rhythm.Add(new RhythmEvent("hi-hat", "hi-hat", sample, 1.0f, 0));
            if (index % 8 == 4)
                rhythm.Add(new RhythmEvent("snare", "snare", sample, 1.0f, 0));
            if (index % 8 == 0)
                rhythm.Add(new RhythmEvent("kick", "kick", sample, 1.0f, 0));
        }

        var timeline = new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = count * tatum,
            Rhythm = rhythm,
            Timing = Array.Empty<DriverTimingEvent>(),
        };
        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(
            timeline,
            new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });

        Assert.InRange(build.Diagnostics.SelectedBpm!.Value, 148.5, 150.5);
        // The 16th-note hi-hat stream is physically identical at the double
        // tempo (32nds at ~298.8), so the octave family stays within the
        // ambiguity band (tempo margin ~0.031 < 0.04): the rhythm roles pick
        // the middle alias and the family ambiguity is surfaced honestly —
        // consistent with Symbolic_BeatLockedRhythm_SurfacesAccentEvidence_
        // ResolvesCentralOctave.
        Assert.True(build.Diagnostics.TempoAmbiguous);
        Assert.Equal(4, build.Diagnostics.TatumsPerBeat);
        Assert.True(build.Diagnostics.DownbeatKnown);
        Assert.Equal(new Meter(4, 4), build.Map.Meter);
        Assert.NotNull(build.Map.FirstDownbeatQuarter);
    }

}
