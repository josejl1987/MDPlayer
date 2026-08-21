using Fmp.Core.Analysis;
using Fmp.Core.Visualization;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Analysis;

public sealed class AnalysisCoreTests
{
    [Fact]
    public void Normalizer_IsDeterministicAndKeepsOnlyPitchedSymbolicNotes()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        AnalysisInput first = FmpSymbolicNormalizer.Normalize(timeline);
        AnalysisInput second = FmpSymbolicNormalizer.Normalize(timeline);

        Assert.Equal(AnalysisJson.Serialize(first, indented: false), AnalysisJson.Serialize(second, indented: false));
        Assert.StartsWith("sha256:", first.AnalysisId);
        Assert.DoesNotContain("/", first.Track.SourcePathHint);
        Assert.Contains(first.Channels, channel => channel.Notes.Any(note => note.IsPitched));
        Assert.Contains(first.Channels.SelectMany(channel => channel.Notes), note => note.IsNoise && !note.IsPitched);
        AnalysisChannel operatorChannel = Assert.Single(
            first.Channels,
            channel => channel.Id.EndsWith("fm3.op.1", StringComparison.Ordinal));
        Assert.Equal("fm3-operator", operatorChannel.Kind);
        Assert.Equal(0.35, operatorChannel.AnalysisWeight);
        Assert.All(first.Channels.SelectMany(channel => channel.Notes), note =>
            Assert.InRange(note.StartSample, timeline.StartSample, timeline.EndSample));
    }

    [Fact]
    public void Normalizer_PreservesSampleActivityAndValidatedTempoWithoutMakingNoiseTonal()
    {
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            StartSample = 0,
            EndSample = 2_000,
            Ppz8 =
            [
                new Ppz8Event(0, 100, 600, 0, 4, 0x8000, 1.0, 16_000, null, 1, 0, false),
                new Ppz8Event(1, 700, 900, 0, 5, null, 1.0, 16_000, null, 1, 0, false),
            ],
            AdpcmB =
            [new AdpcmBEvent(1_000, 1_400, 0, 1, 2, null, 1, 0, false)],
            Rhythm = [new RhythmEvent("bd", "ym2608.0.rhythm.bd", 1_500, 1, 0)],
            Timing = [new DriverTimingEvent(0, 0, 156)],
        };

        AnalysisInput input = FmpSymbolicNormalizer.Normalize(timeline);

        Assert.DoesNotContain(input.Channels, channel => channel.Kind == "ppz8");
        Assert.Contains(input.Channels, channel => channel.Kind == "adpcm");
        AnalysisChannel rhythm = Assert.Single(input.Channels, channel => channel.Kind == "rhythm");
        Assert.All(rhythm.Notes, note => Assert.False(note.IsPitched));
        Assert.Single(input.Timing.TempoEvents);
        Assert.Equal(156, input.Timing.TempoEvents[0].Bpm);
    }

    [Fact]
    public void Normalizer_UsesOnlyMonotonicBeatMapsForMusicalTime()
    {
        VisualizationTimeline valid = new()
        {
            SampleRate = 1_000,
            StartSample = 0,
            EndSample = 2_000,
            Beats =
            [
                new BeatEvent(0, 0),
                new BeatEvent(1_000, 1),
                new BeatEvent(2_000, 2),
            ],
        };
        AnalysisInput validInput = FmpSymbolicNormalizer.Normalize(valid);
        Assert.Equal("beats", validInput.Timing.TimingMode);
        Assert.Equal(3, validInput.Timing.Beats.Count);

        VisualizationTimeline invalid = new()
        {
            SampleRate = 1_000,
            StartSample = 0,
            EndSample = 2_000,
            Beats = [new BeatEvent(0, 0), new BeatEvent(1_000, 1), new BeatEvent(1_000, 2)],
        };
        AnalysisInput invalidInput = FmpSymbolicNormalizer.Normalize(invalid);
        Assert.Equal("seconds", invalidInput.Timing.TimingMode);
        Assert.Empty(invalidInput.Timing.Beats);

        VisualizationTimeline tempoMismatch = new()
        {
            SampleRate = 1_000,
            StartSample = 0,
            EndSample = 2_000,
            Timing = [new DriverTimingEvent(0, 0, 120)],
            Beats = [new BeatEvent(0, 0), new BeatEvent(1_000, 1), new BeatEvent(2_000, 2)],
        };
        Assert.Equal("seconds", FmpSymbolicNormalizer.Normalize(tempoMismatch).Timing.TimingMode);
    }

    [Fact]
    public void CacheKey_ChangesWithAnalysisOptionsButNotVideoGeometry()
    {
        AnalysisInput input = FmpSymbolicNormalizer.Normalize(VisualizationTimelineFixture.Create());
        string standard = AnalysisCacheKey.Compute(input, "1", "1.0.0", "10.5.0", "", "standard");
        string minimal = AnalysisCacheKey.Compute(input, "1", "1.0.0", "10.5.0", "", "minimal");
        string sameStandard = AnalysisCacheKey.Compute(input, "1", "1.0.0", "10.5.0", "", "standard");

        Assert.Equal(standard, sameStandard);
        Assert.NotEqual(standard, minimal);
    }

    [Fact]
    public void CacheKey_UsesCanonicalChannelAndNoteOrdering()
    {
        AnalysisNote note = new()
        {
            Id = "n",
            StartSample = 0,
            EndSample = 100,
            StructuralMidiPitch = 60,
            PitchClass = 0,
            IsPitched = true,
        };
        AnalysisInput first = new()
        {
            SampleRate = 1000,
            EndSample = 100,
            Channels =
            [
                new AnalysisChannel { Id = "b", Notes = [note] },
                new AnalysisChannel { Id = "a", Notes = [] },
            ],
        };
        AnalysisInput second = new()
        {
            SampleRate = 1000,
            EndSample = 100,
            Channels =
            [
                new AnalysisChannel { Id = "a", Notes = [] },
                new AnalysisChannel { Id = "b", Notes = [note] },
            ],
        };

        Assert.Equal(
            AnalysisCacheKey.Compute(first, "1", "1.0.0", "10.5.0", "", "standard"),
            AnalysisCacheKey.Compute(second, "1", "1.0.0", "10.5.0", "", "standard"));
    }

    [Fact]
    public void StructuralPitchExtractor_SuppressesVibratoAndKeepsDestinationBend()
    {
        NoteEvent note = new(
            "voice",
            0,
            1000,
            440,
            69,
            "instrument",
            VisualizationNoteMode.Fm,
            false,
            [
                new PitchChange(100, 440, 69.20),
                new PitchChange(200, 440, 68.80),
                new PitchChange(300, 440, 71.00),
                new PitchChange(500, 440, 71.00),
            ]);

        StructuralPitchResult result = StructuralPitchExtractor.Extract(note, 1000);

        Assert.Equal(2, result.Regions.Count);
        Assert.Equal(71, result.PrincipalPitch, 3);
        Assert.False(result.Microtonal);
    }

    [Fact]
    public void StructuralPitchExtractor_FlatPitchProducesOnePerfectRegion()
    {
        StructuralPitchResult result = StructuralPitchExtractor.Extract(Note(0, 1_000, 60), 1_000);

        var region = Assert.Single(result.Regions);
        Assert.Equal(60, result.PrincipalPitch);
        Assert.Equal(0, region.StartSample);
        Assert.Equal(1_000, region.EndSample);
        Assert.Equal(60, region.MidiPitch);
        Assert.Equal(1, region.Stability);
        Assert.False(result.Microtonal);
        Assert.False(result.Gliding);
    }

    [Fact]
    public void StructuralPitchExtractor_NarrowVibratoDoesNotSplitTheNote()
    {
        StructuralPitchResult result = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(100, 0, 60.20),
                new PitchChange(200, 0, 59.80),
                new PitchChange(300, 0, 60.20),
                new PitchChange(400, 0, 59.80),
                new PitchChange(500, 0, 60.20),
                new PitchChange(600, 0, 59.80),
                new PitchChange(700, 0, 60.20),
                new PitchChange(800, 0, 59.80),
                new PitchChange(900, 0, 60.00)),
            1_000);

        Assert.Single(result.Regions);
        Assert.Equal(60, result.PrincipalPitch, 3);
        Assert.False(result.Microtonal);
        Assert.False(result.Gliding);
    }

    [Fact]
    public void StructuralPitchExtractor_WideVibratoLowersStabilityWithoutInventingNotes()
    {
        StructuralPitchResult result = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(100, 0, 60.40),
                new PitchChange(200, 0, 59.60),
                new PitchChange(300, 0, 60.40),
                new PitchChange(400, 0, 59.60),
                new PitchChange(500, 0, 60.40),
                new PitchChange(600, 0, 59.60),
                new PitchChange(700, 0, 60.40),
                new PitchChange(800, 0, 59.60)),
            1_000);

        Assert.Single(result.Regions);
        Assert.Equal(60, result.PrincipalPitch, 3);
        Assert.InRange(result.Regions[0].Stability, 0, 0.9);
        Assert.False(result.Gliding);
    }

    [Fact]
    public void StructuralPitchExtractor_TransientExcursionDoesNotSplitTheNote()
    {
        StructuralPitchResult result = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(500, 0, 61.50),
                new PitchChange(540, 0, 60.00)),
            1_000);

        Assert.Single(result.Regions);
        Assert.Equal(60, result.PrincipalPitch, 3);
        Assert.False(result.Gliding);
    }

    [Fact]
    public void StructuralPitchExtractor_PortamentoRemainsAContinuum()
    {
        StructuralPitchResult result = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(200, 0, 61),
                new PitchChange(400, 0, 62),
                new PitchChange(600, 0, 63),
                new PitchChange(800, 0, 64)),
            1_000);

        Assert.InRange(result.Regions.Count, 1, 2);
        Assert.True(result.Gliding);
        Assert.DoesNotContain(result.Regions, region => region.MidiPitch is 61 or 62 or 63);
    }

    [Fact]
    public void StructuralPitchExtractor_IsInvariantToAbsoluteSamplePosition()
    {
        StructuralPitchResult first = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(400, 0, 60.10),
                new PitchChange(500, 0, 62),
                new PitchChange(800, 0, 62)),
            1_000);
        StructuralPitchResult shifted = StructuralPitchExtractor.Extract(
            Note(10_000_000, 10_001_000, 60,
                new PitchChange(10_000_400, 0, 60.10),
                new PitchChange(10_000_500, 0, 62),
                new PitchChange(10_000_800, 0, 62)),
            1_000);

        Assert.Equal(first.PrincipalPitch, shifted.PrincipalPitch);
        Assert.Equal(first.Microtonal, shifted.Microtonal);
        Assert.Equal(first.Gliding, shifted.Gliding);
        Assert.Equal(
            first.Regions.Select(region => (region.EndSample - region.StartSample, region.MidiPitch, region.Stability)),
            shifted.Regions.Select(region => (region.EndSample - region.StartSample, region.MidiPitch, region.Stability)));
    }

    [Fact]
    public void StructuralPitchExtractor_IgnoresInvalidAndOrdersDuplicatePitchPointsDeterministically()
    {
        StructuralPitchResult invalidInitial = StructuralPitchExtractor.Extract(
            Note(0, 1_000, double.NaN,
                new PitchChange(100, 0, 60)),
            1_000);
        StructuralPitchResult invalidChanges = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(300, 0, double.NaN),
                new PitchChange(200, 0, double.PositiveInfinity),
                new PitchChange(100, 0, -4)),
            1_000);
        StructuralPitchResult duplicateChanges = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(600, 0, 60.2),
                new PitchChange(200, 0, 60.1),
                new PitchChange(200, 0, 60.1)),
            1_000);

        Assert.Equal(-1, invalidInitial.PrincipalPitch);
        Assert.Empty(invalidInitial.Regions);
        Assert.Equal(60, invalidChanges.PrincipalPitch);
        Assert.Single(invalidChanges.Regions);
        StructuralPitchResult reorderedDuplicateChanges = StructuralPitchExtractor.Extract(
            Note(0, 1_000, 60,
                new PitchChange(200, 0, 60.1),
                new PitchChange(200, 0, 60.1),
                new PitchChange(600, 0, 60.2)),
            1_000);
        Assert.Equal(duplicateChanges.PrincipalPitch, reorderedDuplicateChanges.PrincipalPitch);
        Assert.Equal(duplicateChanges.Microtonal, reorderedDuplicateChanges.Microtonal);
        Assert.Equal(duplicateChanges.Gliding, reorderedDuplicateChanges.Gliding);
        Assert.Equal(
            duplicateChanges.Regions.Select(region => (region.StartSample, region.EndSample, region.MidiPitch, region.Stability)),
            reorderedDuplicateChanges.Regions.Select(region => (region.StartSample, region.EndSample, region.MidiPitch, region.Stability)));
    }

    private static NoteEvent Note(long start, long end, double pitch, params PitchChange[] changes)
        => new(
            "voice",
            start,
            end,
            440,
            pitch,
            "instrument",
            VisualizationNoteMode.Fm,
            false,
            changes);

    [Fact]
    public void Validator_RejectsInvalidInputEvidence()
    {
        AnalysisInput input = new()
        {
            SampleRate = 1000,
            EndSample = 100,
            Channels = [new AnalysisChannel { Id = "voice", AnalysisWeight = 2 }],
            ArpeggioEvidence = [new ArpeggioEvidence
            {
                ChannelId = "missing",
                StartSample = 0,
                EndSample = 100,
                PeriodSamples = 0,
                PitchClasses = [12],
                Regularity = double.NaN,
            }],
        };
        AnalysisOutput output = new() { AnalysisId = input.AnalysisId };

        IReadOnlyList<string> errors = AnalysisResultValidator.Validate(input, output);

        Assert.Contains(errors, error => error.Contains("analysisWeight", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown channel", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("period must be positive", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("pitch class outside", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("not finite", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsBoundaryOutsideTimeline()
    {
        AnalysisInput input = FmpSymbolicNormalizer.Normalize(VisualizationTimelineFixture.Create());
        AnalysisOutput output = new()
        {
            AnalysisId = input.AnalysisId,
            Engine = new AnalysisEngine { Music21Version = "10.5.0" },
            Boundaries = [new AnalysisBoundary { Sample = input.EndSample + 1 }],
        };

        IReadOnlyList<string> errors = AnalysisResultValidator.Validate(input, output);

        Assert.Contains(errors, error => error.Contains("boundary sample range", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfidencePolicy_LeavesModerateCandidatesTentative()
    {
        Assert.Equal("withheld", AnalysisDisplayPolicy.Certainty(0.54));
        Assert.Equal("tentative", AnalysisDisplayPolicy.Certainty(0.60));
        Assert.Equal("strong", AnalysisDisplayPolicy.Certainty(0.80));
        Assert.True(AnalysisDisplayPolicy.DisplayKey(
            new AnalysisConfidence { Score = 0.80, Certainty = "strong" },
            AnalysisOverlayMode.Minimal));
        Assert.False(AnalysisDisplayPolicy.DisplayHarmony(
            new AnalysisConfidence { Score = 0.99, Certainty = "strong" },
            AnalysisOverlayMode.Standard));
        Assert.False(AnalysisDisplayPolicy.DisplayRoman(
            new AnalysisConfidence { Score = 0.99, Certainty = "strong" },
            new AnalysisConfidence { Score = 0.99, Certainty = "strong" },
            new AnalysisConfidence { Score = 0.99, Certainty = "strong" },
            AnalysisOverlayMode.Standard));
    }

    [Fact]
    public void Validator_RejectsUnknownChannelAndOutOfRangeConfidence()
    {
        AnalysisInput input = FmpSymbolicNormalizer.Normalize(VisualizationTimelineFixture.Create());
        var output = new AnalysisOutput
        {
            AnalysisId = input.AnalysisId,
            Engine = new AnalysisEngine { Music21Version = "10.5.0" },
            Channels = [new ChannelAnalysis { ChannelId = "missing", PitchClassDistribution = new double[12] }],
            Global = new AnalysisGlobal
            {
                Key = new KeyInterpretation
                {
                    Confidence = new AnalysisConfidence { Score = 2, Certainty = "strong" },
                },
            },
        };

        IReadOnlyList<string> errors = AnalysisResultValidator.Validate(input, output);

        Assert.Contains(errors, error => error.Contains("unknown channel", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("outside [0,1]", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsMalformedNoteRegionsAndDuplicateOutputChannels()
    {
        AnalysisInput input = new()
        {
            SampleRate = 1_000,
            EndSample = 1_000,
            Channels = [new AnalysisChannel { Id = "voice" }],
        };
        AnalysisOutput output = new()
        {
            AnalysisId = input.AnalysisId,
            Engine = new AnalysisEngine { Music21Version = "10.5.0" },
            Channels =
            [
                new ChannelAnalysis { ChannelId = "voice", PitchClassDistribution = new double[12] },
                new ChannelAnalysis { ChannelId = "voice", PitchClassDistribution = new double[12] },
            ],
            Harmony =
            [new HarmonySegment
            {
                StartSample = 0,
                EndSample = 100,
                BassPitchClass = -2,
                Confidence = AnalysisConfidence.Withheld("test"),
            }],
        };
        input = new AnalysisInput
        {
            SampleRate = input.SampleRate,
            EndSample = input.EndSample,
            Channels =
            [new AnalysisChannel
            {
                Id = "voice",
                Notes =
                [new AnalysisNote
                {
                    Id = "n",
                    StartSample = 100,
                    EndSample = 200,
                    StructuralMidiPitch = 60,
                    PitchClass = -2,
                    IsPitched = true,
                    PitchRegions = [new AnalysisPitchRegion(0, 300, 60, 1)],
                }],
            }],
        };

        IReadOnlyList<string> errors = AnalysisResultValidator.Validate(input, output);

        Assert.Contains(errors, error => error.Contains("pitchClass is outside -1..11", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("pitch region is outside its note", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("output channel id is missing or duplicated", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("bassPitchClass is outside -1..11", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsNoiseMarkedAsPitchedAndEmptyMotifId()
    {
        AnalysisInput input = new()
        {
            SampleRate = 1_000,
            EndSample = 1_000,
            Channels = [new AnalysisChannel
            {
                Id = "voice",
                Notes = [new AnalysisNote
                {
                    Id = "n",
                    StartSample = 0,
                    EndSample = 100,
                    StructuralMidiPitch = 60,
                    PitchClass = 0,
                    IsPitched = true,
                    IsNoise = true,
                }],
            }],
        };
        AnalysisOutput output = new()
        {
            AnalysisId = input.AnalysisId,
            Engine = new AnalysisEngine { Music21Version = "10.5.0" },
            Motifs = [new MotifOccurrence
            {
                ChannelId = "voice",
                StartSample = 0,
                EndSample = 100,
                Similarity = 1,
            }],
        };

        IReadOnlyList<string> errors = AnalysisResultValidator.Validate(input, output);

        Assert.Contains(errors, error => error.Contains("both noise and pitched", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("motifId is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_AcceptsACompleteVersionedResult()
    {
        AnalysisInput input = FmpSymbolicNormalizer.Normalize(VisualizationTimelineFixture.Create());
        double[] distribution = new double[12];
        var output = new AnalysisOutput
        {
            AnalysisId = input.AnalysisId,
            Engine = new AnalysisEngine { Music21Version = "10.5.0" },
            Global = new AnalysisGlobal
            {
                Key = new KeyInterpretation
                {
                    Primary = new KeyCandidate { TonicPitchClass = 0, Tonic = "C", Mode = "major", Confidence = 0.8 },
                    Confidence = new AnalysisConfidence { Score = 0.8, Certainty = "strong" },
                },
                Pitch = new PitchStatistics { PitchClassDistribution = distribution },
            },
            Channels = [new ChannelAnalysis
            {
                ChannelId = input.Channels.First(channel => channel.Notes.Any(note => note.IsPitched)).Id,
                PitchClassDistribution = distribution,
            }],
        };

        AnalysisOutput roundTrip = AnalysisOutputJson.Deserialize(AnalysisOutputJson.Serialize(output));

        AnalysisResultValidator.EnsureValid(input, roundTrip);
    }
}
