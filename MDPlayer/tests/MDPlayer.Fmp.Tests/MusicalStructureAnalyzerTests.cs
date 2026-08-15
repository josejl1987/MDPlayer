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
        Assert.Empty(structure.Phrases);
        Assert.Empty(structure.Sections);
        Assert.Empty(structure.Loops);
        Assert.Null(structure.PrimaryLoop);
        Assert.Null(structure.Pickup);
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

        // Phrases are the 4-bar segmentation units with full PHRASE_* labels.
        Assert.Equal(4, structure.Phrases.Count);
        Assert.Equal(new MusicalPhrase(0, 4, "PHRASE_A", 1.0), structure.Phrases[0]);
        Assert.Equal(new MusicalPhrase(4, 8, "PHRASE_B", 1.0), structure.Phrases[1]);
        Assert.Equal(new MusicalPhrase(8, 12, "PHRASE_A", 1.0), structure.Phrases[2]);
        Assert.Equal(new MusicalPhrase(12, 16, "PHRASE_B", 1.0), structure.Phrases[3]);

        // Sections merge adjacent same-label phrases and carry the bare name;
        // here each phrase stands alone so sections mirror the phrases.
        Assert.Equal(4, structure.Sections.Count);
        Assert.Equal(new MusicalSection(0, 4, "A", 1.0), structure.Sections[0]);
        Assert.Equal(new MusicalSection(4, 8, "B", 1.0), structure.Sections[1]);
        Assert.Equal(new MusicalSection(8, 12, "A", 1.0), structure.Sections[2]);
        Assert.Equal(new MusicalSection(12, 16, "B", 1.0), structure.Sections[3]);

        // A+B repeats, so the fundamental loop is the 8-bar A-B span.
        Assert.True(structure.HasLoop);
        Assert.NotNull(structure.PrimaryLoop);
        Assert.Equal(8, structure.PrimaryLoop!.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop.StartBar);
    }

    [Fact]
    public void Analyze_AllRest_ReturnsNoLoop()
    {
        // No notes: every bar is a rest. Rest↔rest must not fabricate a loop,
        // and rest-only material has no musical form — no phrases, no sections
        // (spec P1-12: silence produces no PHRASE markers).
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(Map(64, new Meter(4, 4)), Timeline());

        Assert.False(structure.HasLoop);
        Assert.Empty(structure.Loops);
        Assert.Null(structure.PrimaryLoop);
        Assert.Empty(structure.Phrases);
        Assert.Empty(structure.Sections);
        Assert.Null(structure.Pickup);
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
        // The 1-bar remainder of each 33-bar cycle is a phrase-level TURNAROUND
        // (never a normal section); every normal section is at least 4 bars.
        Assert.Contains(structure.Phrases, phrase => phrase.Label == "TURNAROUND");
        Assert.All(structure.Phrases, phrase =>
            Assert.True((phrase.StartBar / 33) * 33 + 33 >= phrase.EndBar,
                $"phrase [{phrase.StartBar},{phrase.EndBar}) crosses the 33-bar loop boundary"));
        Assert.All(structure.Sections, section =>
            Assert.True(section.EndBar - section.StartBar >= 4));
    }

    [Fact]
    public void Analyze_LateDownbeat_AnchorsBarsAtDownbeatAndPreservesPickup()
    {
        // Source starts at quarter 0 but the first downbeat lands at quarter 2:
        // bar 0 must be anchored at the downbeat (spec P0-8) and the pre-downbeat
        // note (quarter 0.5) must be preserved as the Pickup — never mislabeled as
        // bar 0 material and never dropped. 32 quarters of material after the
        // downbeat ⇒ 8 bars.
        NoteEvent[] notes = Enumerable.Range(0, 8)
            .Select(bar => Note(60, 2 + 4 * bar))
            .Prepend(Note(55, 0)) // pickup note at quarter 0
            .ToArray();
        long end = (long)Math.Round(34 * Spq);
        var map = new MusicalTimeMap(
            Sr, 0, new[]
            {
                new TempoSegment(0, end, 0.0, Spq, 120, TimingSource.UserOverride, 1.0),
            },
            meter: new Meter(4, 4),
            firstDownbeatQuarter: 2.0);
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = end,
            Notes = notes,
        };

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);

        Assert.Equal(8, structure.Bars.Count);
        Assert.Equal(2.0, structure.Bars[0].QuarterStart, precision: 9);
        Assert.Equal(6.0, structure.Bars[0].QuarterEnd, precision: 9);

        Assert.NotNull(structure.Pickup);
        Assert.Equal(-2.0, structure.Pickup!.QuarterStart, precision: 9);
        Assert.Equal(2.0, structure.Pickup.QuarterEnd, precision: 9);
        Assert.Equal(1, structure.Pickup.NoteOnCount); // exactly the pickup note
        // Bar 0 carries only its own attack (quarter 2), not the pickup note.
        Assert.Equal(1, structure.Bars[0].NoteOnCount);
    }

    [Fact]
    public void Analyze_FifteenBarPiece_AttachesThreeBarRemainder()
    {
        // 12 bars of distinct 4-bar phrasing plus a 3-bar coda: the 3-bar
        // remainder must be attached to the preceding phrase, never emitted as a
        // normal phrase < 4 (spec P1-12 "15-bar -> attach 3-bar").
        NoteEvent[] notes = Enumerable.Range(0, 15)
            .Select(bar => Note(48 + bar, 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(15 * 4, new Meter(4, 4)), Timeline(notes));

        Assert.Equal(3, structure.Phrases.Count);
        Assert.Equal(new MusicalPhrase(0, 4, "PHRASE_A", 1.0), structure.Phrases[0]);
        Assert.Equal(new MusicalPhrase(4, 8, "PHRASE_B", 1.0), structure.Phrases[1]);
        Assert.Equal(8, structure.Phrases[2].StartBar);
        Assert.Equal(15, structure.Phrases[2].EndBar); // 3-bar remainder attached
        Assert.All(structure.Phrases, phrase =>
            Assert.True(phrase.EndBar - phrase.StartBar >= 4));
        Assert.DoesNotContain(structure.Phrases, phrase => phrase.Label == "TURNAROUND");
    }

    [Fact]
    public void Analyze_ThirteenBarPiece_EmitsTurnaround()
    {
        // 12 bars of distinct 4-bar phrasing plus a 1-bar coda: the 1-bar
        // remainder becomes a phrase-level TURNAROUND (spec P1-12).
        NoteEvent[] notes = Enumerable.Range(0, 13)
            .Select(bar => Note(48 + bar, 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(13 * 4, new Meter(4, 4)), Timeline(notes));

        Assert.Equal(4, structure.Phrases.Count);
        Assert.Equal(new MusicalPhrase(12, 13, "TURNAROUND", 1.0), structure.Phrases[^1]);
        Assert.DoesNotContain(structure.Sections, section => section.Label == "TURNAROUND");
    }

    [Fact]
    public void Analyze_AggregateHits_FeedBarMasksAndActivity()
    {
        // Aggregate hits (percussion/noise aggregate streams) must aggregate into
        // bar rhythm masks and physical activity like every other hit family —
        // one hit per bar at quarter 0 of each 4/4 bar.
        var hits = Enumerable.Range(0, 8)
            .Select(bar => new AggregateHitEvent(
                VoiceId: "noise",
                SubVoiceId: "bus",
                Label: "noise",
                SamplePosition: (long)Math.Round(bar * 4.0 * Spq),
                Strength: 1f,
                Pan: 0f,
                AssetId: "a"))
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(8 * 4 * Spq),
            AggregateHits = hits,
        };

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(8 * 4, new Meter(4, 4)), timeline);

        Assert.Equal(8, structure.Bars.Count);
        Assert.All(structure.Bars, bar =>
        {
            Assert.Equal(1, bar.RhythmOnsetCount);
            Assert.Equal((ushort)(1 << 0), bar.OnsetMask); // onset at the downbeat slot
            Assert.NotEqual(0UL, bar.ActiveVoiceMask); // physical activity from VoiceId
        });
    }

    [Fact]
    public void SelectGrid_UsesAggregateHitsAsOnsetEvidence()
    {
        // Aggregate hits alone (no notes, no rhythm events) must drive the onset
        // fit: every hit lands exactly on the candidate's downbeat grid.
        var hits = new[]
        {
            new AggregateHitEvent("noise", "bus", "noise",
                (long)Math.Round(0.0 * Spq), 1f, 0f, "a"),
            new AggregateHitEvent("noise", "bus", "noise",
                (long)Math.Round(4.0 * Spq), 1f, 0f, "a"),
            new AggregateHitEvent("noise", "bus", "noise",
                (long)Math.Round(8.0 * Spq), 1f, 0f, "a"),
            new AggregateHitEvent("noise", "bus", "noise",
                (long)Math.Round(12.0 * Spq), 1f, 0f, "a"),
        };
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(16 * Spq),
            AggregateHits = hits,
        };

        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResult(
                16,
                120,
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.5)),
            timeline);

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.Equal(1.0, selection.Breakdown!.OnsetFit, precision: 9);
    }

    [Fact]
    public void Analyze_RepeatedBlock_ReportsRepeatCountAndCoverages()
    {
        // 24 bars of a 6-bar phrase repeated four times, with bar 2 of each
        // phrase left as a rest: content validates four occurrences, so
        // RepeatCount must be 4, SpanCoverage = 4*6/24 = 1.0, and
        // MaterialCoverage = (24 - 4 rest bars) / 24.
        NoteEvent[] notes = Enumerable.Range(0, 24)
            .Where(bar => bar % 6 != 2)
            .Select(bar => Note(48 + bar % 6, 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(24 * 4, new Meter(4, 4)), Timeline(notes));

        Assert.True(structure.HasLoop);
        Assert.NotNull(structure.PrimaryLoop);
        RepeatedBlock loop = structure.PrimaryLoop!;
        Assert.Equal(0, loop.StartBar);
        Assert.Equal(6, loop.LengthBars);
        Assert.Equal(4, loop.RepeatCount);
        Assert.Equal(1.0, loop.Similarity, precision: 9);
        Assert.Equal(1.0, loop.SpanCoverage, precision: 9);
        Assert.Equal(20.0 / 24.0, loop.MaterialCoverage, precision: 9);
        Assert.True(loop.ContentValidated);
    }

    [Fact]
    public void Analyze_FundamentalPeriodPreference_ChoosesShorterPeriod()
    {
        // 32 bars of an 8-bar phrase repeated four times. Period 8 (RepeatCount 4)
        // and period 16 (RepeatCount 2) both score 1.0: one is a multiple of the
        // other with scores within tolerance, so the fundamental 8-bar period must
        // win instead of the doubled 16-bar period.
        NoteEvent[] notes = Enumerable.Range(0, 32)
            .Select(bar => Note(48 + bar % 8, 4 * bar))
            .ToArray();

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(32 * 4, new Meter(4, 4)), Timeline(notes));

        Assert.True(structure.HasLoop);
        Assert.NotNull(structure.PrimaryLoop);
        RepeatedBlock loop = structure.PrimaryLoop!;
        Assert.Equal(8, loop.LengthBars);
        Assert.Equal(4, loop.RepeatCount);
        Assert.DoesNotContain(structure.Loops, candidate => candidate.LengthBars == 16);
    }

    [Fact]
    public void Analyze_SparseMaterial_RejectsLowMaterialCoverage()
    {
        // One hit every four bars (bars 0, 4, 8, 12): the pattern repeats with
        // similarity 1.0, but only 2 of 8 compared bars carry material
        // (MatCov 0.25 < 0.40) — the loop must be rejected, never fabricated.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(16 * 4 * Spq),
            Notes = new[]
            {
                Note(60, 0),
                Note(60, 16),
                Note(60, 32),
                Note(60, 48),
            },
        };

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(16 * 4, new Meter(4, 4)), timeline);

        Assert.False(structure.HasLoop);
        Assert.Empty(structure.Loops);
        Assert.Null(structure.PrimaryLoop);
    }

    [Fact]
    public void Analyze_LowSpanCoverage_RejectsLoop()
    {
        // A 4-bar pattern repeated twice inside a 64-bar capture (rest after bar 7):
        // similarity is 1.0 but the repeated span covers only 8/64 = 0.125 < 0.25 —
        // SpanCoverage is below the spec gate, so no loop may be emitted.
        NoteEvent[] notes = Enumerable.Range(0, 8)
            .Select(bar => Note(48 + bar % 4, 4 * bar))
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(64 * 4 * Spq),
            Notes = notes,
        };

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(
            Map(64 * 4, new Meter(4, 4)), timeline);

        Assert.False(structure.HasLoop);
        Assert.Empty(structure.Loops);
        Assert.Null(structure.PrimaryLoop);
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
        VisualizationTimeline timeline = HalfTempoTimeline();
        MusicalTimeMap selected = MusicalStructureAnalyzer.SelectGrid(
            HalfTempoBuildResult().Map, timeline);
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(selected, timeline);

        Assert.Equal(149.4, selected.Segments[0].BeatsPerMinute, precision: 6);
        Assert.Equal(33, structure.PrimaryLoop?.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop?.StartBar);
    }

    [Fact]
    public void Analyze_SingleRestartMarker_InfersNonPowerOfTwoLoopPeriod()
    {
        // 24 bars of 16th-note material whose pitch class repeats every 12 bars
        // (12 is not a power of two), with ONE restart marker at bar 12 and no
        // entry marker. The restart marker alone never establishes a loop: content
        // on both sides of the boundary must validate the inferred period.
        const int bars = 24;
        long end = (long)Math.Round(bars * 4 * Spq);
        NoteEvent[] notes = Enumerable.Range(0, bars * 16)
            .Select(index =>
            {
                long start = (long)Math.Round(index * 0.25 * Spq);
                return new NoteEvent(
                    ChannelId: "v",
                    StartSample: start,
                    EndSample: start + (long)Math.Round(0.125 * Spq),
                    InitialFrequencyHz: 440,
                    InitialMidiNote: 48 + (index / 16 % 12),
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
                new LoopMarker((long)Math.Round(12 * 4 * Spq), LoopMarkerKind.Restart, 1),
            },
        };
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, end, 0.0, Spq, 120, TimingSource.SymbolicInference, 0.6),
            },
            meter: new Meter(4, 4),
            firstDownbeatQuarter: 0.0,
            confidence: 0.6,
            gridCandidates: new[]
            {
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 1),
            });

        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(map, timeline);

        Assert.True(structure.HasLoop);
        Assert.NotNull(structure.PrimaryLoop);
        Assert.Equal(12, structure.PrimaryLoop!.LengthBars);
        Assert.Equal(0, structure.PrimaryLoop.StartBar);
        Assert.True(structure.PrimaryLoop.SourceSupported);
        Assert.True(structure.PrimaryLoop.ContentValidated);
    }

    /// <summary>
    /// Build result for the 149.4/298.8 half-tempo scenario: symbolic map with a
    /// 33-bar source-loop period (restart markers), two grid candidates, and an
    /// initial diagnostics carrying the standard symbolic-inference sources.
    /// </summary>
    private static MusicalTimeMapBuildResult HalfTempoBuildResult()
    {
        const double bpm = 149.4;
        double spq = Sr * 60.0 / bpm;
        const int bars = 66;
        long end = (long)Math.Round(bars * 4 * spq);
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
        return new MusicalTimeMapBuildResult
        {
            Map = map,
            Diagnostics = new TimingDiagnostics
            {
                TempoSource = TimingSource.SymbolicInference,
                PhaseSource = TimingSource.SymbolicInference,
            },
        };
    }

    /// <summary>Timeline for the half-tempo scenario: 16th-note material on the
    /// 149.4 BPM grid with restart markers establishing a 33-bar source period.</summary>
    private static VisualizationTimeline HalfTempoTimeline()
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
        return new VisualizationTimeline
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
    }

    /// <summary>Build result for a single symbolic grid candidate over a short span.</summary>
    private static MusicalTimeMapBuildResult SymbolicBuildResult(
        int quarters,
        double bpm,
        params MusicalGridCandidate[] candidates)
    {
        long end = (long)Math.Round(quarters * Spq);
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, end, 0, Sr * 60.0 / bpm, bpm,
                    TimingSource.SymbolicInference, 0.2),
            },
            meter: new Meter(4, 4),
            firstDownbeatQuarter: 0,
            confidence: 0.2,
            gridCandidates: candidates);
        return new MusicalTimeMapBuildResult
        {
            Map = map,
            Diagnostics = new TimingDiagnostics
            {
                TempoSource = TimingSource.SymbolicInference,
                PhaseSource = TimingSource.SymbolicInference,
            },
        };
    }

    private static NoteEvent NoteAt(double quarter)
    {
        long start = (long)Math.Round(quarter * Spq);
        return new NoteEvent(
            ChannelId: "v",
            StartSample: start,
            EndSample: start + (long)Math.Round(0.5 * Spq),
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());
    }

    [Fact]
    public void GridSelectionDiagnostics_PreserveRawInference()
    {
        MusicalTimeMapBuildResult initial = HalfTempoBuildResult();
        initial.Diagnostics.PhaseSample = 999_000;
        initial.Diagnostics.BeatPhaseSample = 888_000;
        initial.Diagnostics.BeatDurationSamples = 11_000.5;
        initial.Diagnostics.TatumDurationSamples = 2_750.0;
        initial.Diagnostics.TatumsPerBeat = 4;
        initial.Diagnostics.DownbeatPhase = 1;
        initial.Diagnostics.MetricalConfidence = 0.42;

        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            initial, HalfTempoTimeline());

        // Raw onset/hierarchy inference must survive selection untouched.
        Assert.Equal(999_000, result.Diagnostics.PhaseSample);
        Assert.Equal(888_000, result.Diagnostics.BeatPhaseSample);
        Assert.Equal(11_000.5, result.Diagnostics.BeatDurationSamples);
        Assert.Equal(2_750.0, result.Diagnostics.TatumDurationSamples);
        Assert.Equal(4, result.Diagnostics.TatumsPerBeat);
        Assert.Equal(1, result.Diagnostics.DownbeatPhase);
        Assert.Equal(0.42, result.Diagnostics.MetricalConfidence);
        Assert.Equal(149.4, result.Diagnostics.SelectedBpm!.Value, precision: 6);
        Assert.NotNull(result.Diagnostics.GridSelection);
        Assert.True(result.Diagnostics.GridSelection!.Attempted);
        Assert.True(result.Diagnostics.GridSelection.TempoResolved);
    }

    [Fact]
    public void GridSelectionDiagnostics_ReportWinnerAndMargins()
    {
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            HalfTempoBuildResult(), HalfTempoTimeline());

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.True(selection.TempoResolved);
        Assert.True(selection.MeterResolved);
        Assert.True(selection.DownbeatResolved);
        Assert.Equal(149.4, selection.SelectedBpm!.Value, precision: 6);
        Assert.Equal(new Meter(4, 4), selection.SelectedMeter);
        Assert.Equal(0.0, selection.SelectedDownbeatQuarter!.Value);
        Assert.True(selection.WinnerScore >= 0.70);
        Assert.True(selection.TempoMargin >= 0.05);
        Assert.NotNull(selection.Breakdown);
        Assert.Equal(selection.WinnerScore, selection.Breakdown!.CombinedScore, precision: 9);
        Assert.NotEmpty(selection.TopCandidates);
        Assert.True(selection.TopCandidates.Count <= 5);
        Assert.Equal(149.4, selection.TopCandidates[0].Bpm, precision: 6);
        Assert.NotNull(selection.TopCandidates[0].Breakdown);
        // Patch 3 phase ranking: one phase candidate in the winning family means
        // no phase ambiguity — margin is +Infinity, same semantic as TempoMargin.
        Assert.Equal(double.PositiveInfinity, selection.DownbeatMargin);
    }

    [Fact]
    public void RejectedSelection_HasMachineReadableReason()
    {
        // Notes deliberately misaligned to the 120 BPM grid, with no loop markers,
        // no repeated content (2 bars), and no rhythm: the joint structural gate
        // must reject and record a machine-readable reason.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(8 * Spq),
            Notes = new[] { NoteAt(0.5), NoteAt(1.125), NoteAt(3.2) },
        };
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResult(
                8,
                120,
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.5)),
            timeline);

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.False(selection.TempoResolved);
        Assert.False(selection.MeterResolved);
        Assert.False(selection.DownbeatResolved);
        Assert.False(string.IsNullOrEmpty(selection.RejectionReason));
        Assert.StartsWith("reject:", selection.RejectionReason);
        Assert.NotEmpty(selection.TopCandidates);
        Assert.Contains(
            result.Diagnostics.Warnings,
            warning => warning.Contains("joint structural timing unresolved"));
    }

    // ---- Patch 3 required tests -------------------------------------------

    /// <summary>Build result with an explicit source meter and downbeat (default
    /// 4/4/0), for candidates that propose their own grid.</summary>
    private static MusicalTimeMapBuildResult SymbolicBuildResultMeter(
        int quarters,
        double bpm,
        Meter? meter,
        double? firstDownbeatQuarter,
        params MusicalGridCandidate[] candidates)
    {
        long end = (long)Math.Round(quarters * Spq);
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, end, 0, Sr * 60.0 / bpm, bpm,
                    TimingSource.SymbolicInference, 0.2),
            },
            meter: meter,
            firstDownbeatQuarter: firstDownbeatQuarter,
            confidence: 0.2,
            gridCandidates: candidates);
        return new MusicalTimeMapBuildResult
        {
            Map = map,
            Diagnostics = new TimingDiagnostics
            {
                TempoSource = TimingSource.SymbolicInference,
                PhaseSource = TimingSource.SymbolicInference,
            },
        };
    }

    /// <summary>Half-tempo (149.4/298.8) build result over custom candidates.</summary>
    private static MusicalTimeMapBuildResult HalfTempoBuildResultWith(
        params MusicalGridCandidate[] candidates)
    {
        const double bpm = 149.4;
        double spq = Sr * 60.0 / bpm;
        const int bars = 66;
        long end = (long)Math.Round(bars * 4 * spq);
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
            gridCandidates: candidates);
        return new MusicalTimeMapBuildResult
        {
            Map = map,
            Diagnostics = new TimingDiagnostics
            {
                TempoSource = TimingSource.SymbolicInference,
                PhaseSource = TimingSource.SymbolicInference,
            },
        };
    }

    /// <summary>Drum hit at a quarter position on the given samples-per-quarter grid.</summary>
    private static RhythmEvent RhythmHit(string voice, double quarter, double samplesPerQuarter)
    {
        long start = (long)Math.Round(quarter * samplesPerQuarter);
        return new RhythmEvent(voice, "v", start, 1f, 0f);
    }

    /// <summary>Timeline carrying only drum hits over the given 120-BPM span.</summary>
    private static VisualizationTimeline RhythmTimeline(int quarters, params RhythmEvent[] hits) => new()
    {
        SampleRate = Sr,
        StartSample = 0,
        EndSample = (long)Math.Round(quarters * Spq),
        Rhythm = hits,
    };

    [Fact]
    public void SelectGrid_NoMeter_ConsidersFourFourThreeFourAndSixEight()
    {
        MusicalTimeMapBuildResult initial = SymbolicBuildResultMeter(
            16,
            120,
            meter: null,
            firstDownbeatQuarter: null,
            new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.5),
            new MusicalGridCandidate(120, new Meter(3, 4), 0, 0, 0.5),
            new MusicalGridCandidate(120, new Meter(6, 8), 0, 0, 0.5));

        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            initial,
            Timeline(Note(60, 0), Note(60, 4), Note(60, 8), Note(60, 12)));

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.Contains(new Meter(4, 4), selection.TopCandidates.Select(candidate => candidate.Meter));
        Assert.Contains(new Meter(3, 4), selection.TopCandidates.Select(candidate => candidate.Meter));
        Assert.Contains(new Meter(6, 8), selection.TopCandidates.Select(candidate => candidate.Meter));
    }

    [Fact]
    public void SelectGrid_UsesCandidatePrior()
    {
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResult(
                32,
                120,
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.9),
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.1)),
            Timeline(Enumerable.Range(0, 32).Select(quarter => Note(60, quarter)).ToArray()));

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.True(selection.TempoResolved);
        Assert.True(selection.DownbeatResolved);
        Assert.Equal(120, selection.SelectedBpm!.Value, precision: 6);
        Assert.Equal(0.9, selection.TopCandidates[0].CandidatePrior, precision: 9);
        Assert.Equal(0.9, selection.Breakdown!.CandidatePrior, precision: 9);
    }

    [Fact]
    public void SelectGrid_SeparatesFamilyMarginFromPhaseMargin()
    {
        const double bpm = 149.4;
        double spq149 = Sr * 60.0 / bpm;
        var hits = new List<RhythmEvent>();
        for (int bar = 0; bar < 66; bar++)
        {
            hits.Add(RhythmHit("bassdrum", bar * 4.0, spq149));
            hits.Add(RhythmHit("snare", bar * 4.0 + 1.0, spq149));
            hits.Add(RhythmHit("hi-hat", bar * 4.0 + 2.0, spq149));
        }
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = (long)Math.Round(66 * 4 * spq149),
            Rhythm = hits.ToArray(),
        };

        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            HalfTempoBuildResultWith(
                new MusicalGridCandidate(149.4, new Meter(4, 4), 0, 0, 1.0),
                new MusicalGridCandidate(149.4, new Meter(4, 4), 0, 1, 1.0),
                new MusicalGridCandidate(298.8, new Meter(4, 4), 0, 0, 0.3)),
            timeline);

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.True(selection.TempoResolved);
        Assert.True(selection.MeterResolved);
        Assert.True(selection.DownbeatResolved);
        Assert.Equal(149.4, selection.SelectedBpm!.Value, precision: 6);
        Assert.Equal(0.0, selection.SelectedDownbeatQuarter!.Value, precision: 9);
        Assert.True(selection.TempoMargin > 0.05);
        Assert.True(selection.DownbeatMargin > 0.05);
        Assert.NotEqual(selection.TempoMargin, selection.DownbeatMargin, precision: 6);
    }

    [Fact]
    public void SelectGrid_TempoMayResolveWhileDownbeatRemainsUnknown()
    {
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            HalfTempoBuildResultWith(
                new MusicalGridCandidate(149.4, new Meter(4, 4), 0, null, 1.0),
                new MusicalGridCandidate(298.8, new Meter(4, 4), 0, null, 0.5)),
            HalfTempoTimeline());

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.True(selection.TempoResolved);
        Assert.True(selection.MeterResolved);
        Assert.False(selection.DownbeatResolved);
        Assert.Equal(149.4, selection.SelectedBpm!.Value, precision: 6);
        Assert.Null(selection.SelectedDownbeatQuarter);
        Assert.Null(result.Map.FirstDownbeatQuarter);
        Assert.True(result.Diagnostics.PhaseUnknown);
        Assert.Contains(
            result.Diagnostics.Warnings,
            warning => warning.Contains("joint structural timing selected"));
    }

    [Fact]
    public void SelectGrid_FourKnownDrumHitsDoNotResolveGrid()
    {
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResult(
                8,
                120,
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.5)),
            RhythmTimeline(
                8,
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("snare", 1, Spq),
                RhythmHit("bassdrum", 4, Spq),
                RhythmHit("snare", 5, Spq)));

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        // Patch 8B: a clear tempo (high score, infinite tempo margin) resolves on
        // the no-strong-evidence path, but meter and downbeat never resolve
        // without strong evidence — the grid stays unresolved.
        Assert.True(selection.TempoResolved);
        Assert.False(selection.MeterResolved);
        Assert.False(selection.DownbeatResolved);
        Assert.False(string.IsNullOrEmpty(selection.RejectionReason));
        Assert.StartsWith("reject:", selection.RejectionReason);
        Assert.Contains("meter-unresolved", selection.RejectionReason);
    }

    [Fact]
    public void SelectGrid_SixteenKnownHitsAndTwoRolesMayResolveGrid()
    {
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResult(
                16,
                120,
                new MusicalGridCandidate(120, new Meter(4, 4), 0, 0, 0.5)),
            RhythmTimeline(
                16,
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("bassdrum", 4, Spq),
                RhythmHit("bassdrum", 4, Spq),
                RhythmHit("bassdrum", 4, Spq),
                RhythmHit("bassdrum", 8, Spq),
                RhythmHit("bassdrum", 8, Spq),
                RhythmHit("bassdrum", 8, Spq),
                RhythmHit("bassdrum", 12, Spq),
                RhythmHit("bassdrum", 12, Spq),
                RhythmHit("bassdrum", 12, Spq),
                RhythmHit("snare", 1.225, Spq),
                RhythmHit("snare", 5.225, Spq),
                RhythmHit("snare", 9.225, Spq),
                RhythmHit("snare", 13.225, Spq)));

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.True(selection.TempoResolved);
        Assert.True(selection.MeterResolved);
        Assert.Equal(120, selection.SelectedBpm!.Value, precision: 6);
        Assert.Equal(0.8875, selection.Breakdown!.RhythmRoleFit!.Value, precision: 4);
        Assert.Equal(16, selection.Breakdown.KnownRhythmRoleHits);
    }

    [Fact]
    public void SelectGrid_ThreeFourDoesNotUseFourFourBackbeatTemplate()
    {
        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResultMeter(
                8,
                120,
                new Meter(3, 4),
                0,
                new MusicalGridCandidate(120, new Meter(3, 4), 0, 0, 0.5)),
            RhythmTimeline(
                8,
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("bassdrum", 0, Spq),
                RhythmHit("snare", 1, Spq),
                RhythmHit("snare", 1, Spq),
                RhythmHit("hi-hat", 2, Spq),
                RhythmHit("hi-hat", 2, Spq),
                RhythmHit("bassdrum", 3, Spq),
                RhythmHit("bassdrum", 3, Spq),
                RhythmHit("bassdrum", 3, Spq),
                RhythmHit("bassdrum", 3, Spq),
                RhythmHit("snare", 4, Spq),
                RhythmHit("snare", 4, Spq),
                RhythmHit("hi-hat", 5, Spq),
                RhythmHit("hi-hat", 5, Spq)));

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        // Patch 8B: the 3/4 grid does not borrow the 4/4 backbeat template — it
        // wins on its own merit (top candidate is 3/4) but, lacking strong
        // evidence, meter never resolves; only tempo resolves on margin.
        Assert.True(selection.TempoResolved);
        Assert.False(selection.MeterResolved);
        Assert.False(selection.DownbeatResolved);
        Assert.NotNull(selection.RejectionReason);
        Assert.Contains("meter-unresolved", selection.RejectionReason);
        Assert.Equal(new Meter(3, 4), selection.TopCandidates[0].Meter);
    }

    [Fact]
    public void SelectGrid_SixEightUsesDottedQuarterGrouping()
    {
        var hits = new List<RhythmEvent>();
        for (int bar = 0; bar < 6; bar++)
        {
            hits.Add(RhythmHit("bassdrum", bar * 3.0, Spq));
            hits.Add(RhythmHit("bassdrum", bar * 3.0 + 1.5, Spq));
            hits.Add(RhythmHit("snare", bar * 3.0 + 1.5, Spq));
        }

        MusicalTimeMapBuildResult result = MusicalStructureAnalyzer.SelectGrid(
            SymbolicBuildResultMeter(
                18,
                120,
                new Meter(6, 8),
                0,
                new MusicalGridCandidate(120, new Meter(6, 8), 0, 0, 0.5)),
            RhythmTimeline(18, hits.ToArray()));

        GridSelectionDiagnostics? selection = result.Diagnostics.GridSelection;
        Assert.NotNull(selection);
        Assert.True(selection!.Attempted);
        Assert.True(selection.TempoResolved);
        Assert.True(selection.MeterResolved);
        Assert.Equal(new Meter(6, 8), selection.SelectedMeter);
        Assert.Equal(0.95, selection.Breakdown!.RhythmRoleFit!.Value, precision: 4);
        Assert.Equal(18, selection.Breakdown.KnownRhythmRoleHits);
    }
}
