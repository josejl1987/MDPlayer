#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Derives coarse musical form — bars → labeled sections → repeated-loop bounds — from a
/// decoded timeline and its established <see cref="MusicalTimeMap"/>. Consumes only
/// note-ons and the meter/quarter grid, so every decoded chip family shares this stage.
/// </summary>
internal static class MusicalStructureAnalyzer
{
    private const double SimilarityThreshold = 0.85;
    private const int MaxRepeatPeriodBars = 64;

    private readonly record struct RhythmRoleEvidence(
        double Fit,
        bool Strong,
        int KnownRoleHits,
        int TotalHits);

    private readonly record struct RepeatedContentEvidence(
        double Fit,
        double Coverage);

    private readonly record struct GridScore(
        double Score,
        double? SourcePeriodFit,
        double? RepeatedContentFit,
        double? RepeatedCoverage,
        RhythmRoleEvidence RhythmRole);

    private readonly record struct ScoredGrid(
        MusicalGridCandidate Candidate,
        GridScore Score);

    /// <summary>
    /// Scores retained timing grids against source-loop and repeated-content evidence.
    /// Only symbolic maps are eligible; authoritative driver/user timing is unchanged.
    /// </summary>
    internal static MusicalTimeMapBuildResult SelectGrid(
        MusicalTimeMapBuildResult initial,
        VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(timeline);
        MusicalTimeMap map = initial.Map;
        if (map.Segments.Count != 1 || map.GridCandidates.Count == 0
            || map.Segments[0].Source != TimingSource.SymbolicInference)
            return initial;

        bool hasMaterial = (timeline.Notes ?? Array.Empty<NoteEvent>()).Any(note => note is not null)
            || (timeline.Rhythm ?? Array.Empty<RhythmEvent>()).Any(hit => hit is not null)
            || (timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
                .Any(hit => hit is not null);
        if (!hasMaterial)
            return initial;

        SourceLoopEvidence sourceEvidence = CaptureSourceLoopEvidence(timeline);
        ScoredGrid[] ranked = map.GridCandidates
            .Select(candidate =>
            {
                MusicalTimeMap variant = VariantMap(map, candidate);
                return new ScoredGrid(
                    candidate,
                    GridStructuralScore(variant, timeline, sourceEvidence));
            })
            .Where(value => double.IsFinite(value.Score.Score))
            .OrderByDescending(value => value.Score.Score)
            .ToArray();
        if (ranked.Length == 0)
            return initial;

        ScoredGrid[] rankedFamilies = ranked
            .GroupBy(value => (Bpm: Math.Round(value.Candidate.Bpm, 6), value.Candidate.Meter))
            .Select(group => group.First())
            .OrderByDescending(value => value.Score.Score)
            .ToArray();
        ScoredGrid winner = rankedFamilies[0];
        double margin = rankedFamilies.Length > 1
            ? winner.Score.Score - rankedFamilies[1].Score.Score
            : double.PositiveInfinity;
        bool sourceGate = winner.Score.SourcePeriodFit is >= 0.85;
        bool repeatedGate = winner.Score.RepeatedContentFit is >= 0.90
            && winner.Score.RepeatedCoverage is >= 0.50;
        bool roleGate = winner.Score.RhythmRole.Strong;
        if (winner.Score.Score < 0.70 || margin < 0.05
            || (!sourceGate && !repeatedGate && !roleGate))
        {
            initial.Diagnostics.Warnings.Add(
                $"joint structural timing unresolved ({winner.Score.Score:0.###} score, " +
                $"{margin:0.###} margin; source={winner.Score.SourcePeriodFit?.ToString("0.###") ?? "none"}, " +
                $"{nameof(winner.Score.RepeatedContentFit)}={winner.Score.RepeatedContentFit?.ToString("0.###") ?? "none"}, " +
                $"rhythm={winner.Score.RhythmRole.Fit:0.###}; candidates=" +
                $"{string.Join(",", rankedFamilies.Take(8).Select(value =>
                    $"{value.Candidate.Bpm:0.###}/{value.Candidate.FirstDownbeatQuarter:0.###}/" +
                    $"{value.Score.Score:0.###}/{value.Score.RhythmRole.Fit:0.###}"))}");
            return initial;
        }

        ScoredGrid? alternative = rankedFamilies.Length > 1 ? rankedFamilies[1] : null;
        double? alternativeBpm = alternative is ScoredGrid other
            && Math.Abs(other.Candidate.Bpm - winner.Candidate.Bpm) > 1e-9
            ? other.Candidate.Bpm
            : null;
        double? alternativeScore = alternativeBpm is not null
            ? alternative!.Value.Score.Score
            : null;
        bool tempoAmbiguous = alternativeBpm is not null
            && alternativeScore is double competingScore
            && winner.Score.Score - competingScore < 0.05;
        MusicalTimeMap selected = VariantMap(
            map,
            winner.Candidate,
            Math.Clamp(winner.Score.Score, 0.0, 1.0),
            alternativeBpm,
            tempoAmbiguous);
        TimingDiagnostics diagnostics = initial.Diagnostics;
        diagnostics.SelectedBpm = selected.Segments[0].BeatsPerMinute;
        diagnostics.SelectedScore = winner.Score.Score;
        diagnostics.AlternativeBpm = alternativeBpm;
        diagnostics.AlternativeScore = alternativeScore;
        diagnostics.TempoConfidence = Math.Clamp(winner.Score.Score, 0.0, 1.0);
        diagnostics.TempoAmbiguous = tempoAmbiguous;
        diagnostics.MeterKnown = selected.Meter is not null;
        diagnostics.DownbeatKnown = selected.FirstDownbeatQuarter is not null;
        diagnostics.SampleZeroQuarter = selected.Segments[0].QuarterPositionAtStart;
        diagnostics.TempoMicrosecondsPerQuarter =
            selected.Segments[0].MicrosecondsPerQuarter;
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.PhaseUnknown = selected.FirstDownbeatQuarter is null;
        diagnostics.Warnings.Add(
            $"joint structural timing selected {selected.Segments[0].BeatsPerMinute:0.###} BPM " +
            $"({winner.Score.Score:0.###} score, {winner.Candidate.Meter} grid)");
        return new MusicalTimeMapBuildResult { Map = selected, Diagnostics = diagnostics };
    }

    /// <summary>Compatibility view for existing analyzer tests; production callers
    /// must use the map-plus-diagnostics boundary above.</summary>
    internal static MusicalTimeMap SelectGrid(
        MusicalTimeMap map,
        VisualizationTimeline timeline)
    {
        var diagnostics = new TimingDiagnostics
        {
            TempoSource = map.Segments[0].Source,
            PhaseSource = map.Segments[0].Source,
            SelectedBpm = map.Segments[0].BeatsPerMinute,
            TempoConfidence = map.Confidence,
            SampleZeroQuarter = map.Segments[0].QuarterPositionAtStart,
            TempoMicrosecondsPerQuarter = map.Segments[0].MicrosecondsPerQuarter,
            MeterKnown = map.Meter is not null,
            DownbeatKnown = map.FirstDownbeatQuarter is not null,
        };
        return SelectGrid(new MusicalTimeMapBuildResult
        {
            Map = map,
            Diagnostics = diagnostics,
        }, timeline).Map;
    }

    public static MusicalStructure Analyze(MusicalTimeMap map, VisualizationTimeline timeline)
    {
        if (map.Meter is not Meter meter || meter.QuartersPerBar <= 0
            || map.FirstDownbeatQuarter is null
            || map.Confidence < 0.60)
            return MusicalStructure.Empty;

        double quartersPerBar = meter.QuartersPerBar;
        double barOrigin = map.FirstDownbeatQuarter.Value;
        double firstQuarter = map.SampleToQuarterPosition(map.FirstSample);
        double lastQuarter = map.SampleToQuarterPosition(map.EndSample);
        int barCount = Math.Max(1, (int)Math.Ceiling((lastQuarter - firstQuarter) / quartersPerBar));

        BarFeature[] bars = BuildBars(
            map, timeline.Notes, timeline.Rhythm, barOrigin, quartersPerBar, barCount);
        SourceLoopEvidence sourceEvidence = CaptureSourceLoopEvidence(timeline);
        RepeatedBlock[] loops = DetectSourceLoop(map, sourceEvidence, bars)
            ?? DetectRepeatedBlocks(bars);
        MusicalSection[] sections = SegmentSections(bars, loops);

        return new MusicalStructure
        {
            Bars = bars,
            Sections = sections,
            Loops = loops,
            PrimaryLoop = loops.Length > 0 ? loops[0] : null,
        };
    }

    private static MusicalTimeMap VariantMap(
        MusicalTimeMap source,
        MusicalGridCandidate candidate,
        double? confidence = null,
        double? alternateBpm = null,
        bool? tempoAmbiguous = null)
    {
        double samplesPerQuarter = source.SampleRate * 60.0 / candidate.Bpm;
        TempoSegment segment = source.Segments[0] with
        {
            QuarterPositionAtStart = candidate.QuarterAtSourceStart,
            SamplesPerQuarter = samplesPerQuarter,
            BeatsPerMinute = candidate.Bpm,
        };
        return new MusicalTimeMap(
            source.SampleRate,
            source.StartSample,
            new[] { segment },
            candidate.Meter,
            candidate.FirstDownbeatQuarter,
            confidence ?? source.Confidence,
            alternateBpm ?? source.AlternateBpm,
            tempoAmbiguous ?? source.IsTempoAmbiguous,
            source.GridCandidates);
    }

    private static GridScore GridStructuralScore(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        SourceLoopEvidence sourceEvidence)
    {
        if (map.Meter is not Meter meter || map.FirstDownbeatQuarter is not double downbeat)
            return new GridScore(double.NegativeInfinity, null, null, null, default);

        double quartersPerBar = meter.QuartersPerBar;
        double firstQuarter = map.SampleToQuarterPosition(map.FirstSample);
        double lastQuarter = map.SampleToQuarterPosition(map.EndSample);
        int barCount = Math.Max(
            1,
            (int)Math.Ceiling((lastQuarter - firstQuarter) / quartersPerBar));
        BarFeature[] bars = BuildBars(
            map,
            timeline.Notes,
            timeline.Rhythm,
            downbeat,
            quartersPerBar,
            barCount);

        var signals = new List<(double Weight, double Value)>();
        void Add(double? value, double weight)
        {
            if (value is double finite && double.IsFinite(finite))
                signals.Add((weight, Math.Clamp(finite, 0.0, 1.0)));
        }

        Add(OnsetGridFit(map, timeline, downbeat), 0.25);
        RhythmRoleEvidence rhythm = RhythmRoleFit(map, timeline, downbeat, quartersPerBar);
        Add(rhythm.TotalHits > 0 ? rhythm.Fit : null, 0.15);
        double? sourceFit = SourcePeriodFit(map, sourceEvidence, quartersPerBar);
        Add(sourceFit, 0.35);
        RepeatedContentEvidence repeated = RepeatedContentFit(bars);
        Add(repeated.Fit > 0 ? repeated.Fit : null, 0.15);
        double? phraseFit = PhraseRegularityFit(bars);
        Add(phraseFit, 0.10);
        if (signals.Count == 0)
            return new GridScore(double.NegativeInfinity, sourceFit, repeated.Fit > 0 ? repeated.Fit : null,
                repeated.Coverage, rhythm);

        double totalWeight = signals.Sum(signal => signal.Weight);
        double score = signals.Sum(signal => signal.Weight * signal.Value) / totalWeight;
        return new GridScore(
            score,
            sourceFit,
            repeated.Fit > 0 ? repeated.Fit : null,
            repeated.Coverage,
            rhythm);
    }

    private static double? OnsetGridFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat)
    {
        var positions = new List<long>();
        positions.AddRange((timeline.Notes ?? Array.Empty<NoteEvent>())
            .Where(note => note is not null)
            .Select(note => note.StartSample));
        positions.AddRange((timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .Where(hit => hit is not null)
            .Select(hit => hit.SamplePosition));
        if (positions.Count == 0)
            return null;

        double total = 0;
        foreach (long sample in positions)
        {
            double quarter = map.SampleToQuarterPosition(sample);
            double grid = (quarter - downbeat) * 4.0;
            double residual = Math.Abs(grid - Math.Round(grid));
            total += 1.0 - Math.Min(0.5, residual) / 0.5;
        }
        return total / positions.Count;
    }

    private static RhythmRoleEvidence RhythmRoleFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat,
        double quartersPerBar)
    {
        RhythmEvent[] hits = (timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .Where(hit => hit is not null)
            .ToArray();
        if (hits.Length == 0)
            return default;

        double total = 0;
        int knownRoleHits = 0;
        foreach (RhythmEvent hit in hits)
        {
            double beat = PositiveModulo(
                map.SampleToQuarterPosition(hit.SamplePosition) - downbeat,
                quartersPerBar);
            RhythmRole role = InferRhythmRole(hit);
            double fit = role switch
            {
                RhythmRole.Bd => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (0.0, 1.0),
                    (2.0, 0.70)),
                RhythmRole.Sd => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (1.0, 1.0),
                    (3.0, 0.85)),
                RhythmRole.Hh => SubdivisionAlignment(beat, 0.25),
                RhythmRole.Tom or RhythmRole.Top or RhythmRole.Rim
                    => SubdivisionAlignment(beat, 0.5),
                _ => SubdivisionAlignment(beat, 1.0),
            };
            if (role != RhythmRole.Unknown)
                knownRoleHits++;
            total += fit;
        }

        double fitScore = total / hits.Length;
        bool strong = knownRoleHits >= 4 && fitScore >= 0.85;
        return new RhythmRoleEvidence(fitScore, strong, knownRoleHits, hits.Length);
    }

    private static double WeightedRoleAlignment(
        double beat,
        double quartersPerBar,
        (double Position, double Weight) first,
        (double Position, double Weight) second)
    {
        double firstDistance = CircularDistance(beat, first.Position, quartersPerBar);
        double secondDistance = CircularDistance(beat, second.Position, quartersPerBar);
        double firstFit = 1.0 - Math.Min(0.5, firstDistance) / 0.5;
        double secondFit = 1.0 - Math.Min(0.5, secondDistance) / 0.5;
        return Math.Max(firstFit * first.Weight, secondFit * second.Weight);
    }

    private static double SubdivisionAlignment(double beat, double subdivision)
    {
        double residual = Math.Abs(beat - Math.Round(beat / subdivision) * subdivision);
        return 1.0 - Math.Min(subdivision / 2.0, residual) / (subdivision / 2.0);
    }

    private static double CircularDistance(double value, double target, double period)
    {
        double delta = Math.Abs(value - target) % period;
        return Math.Min(delta, period - delta);
    }

    private static double? SourcePeriodFit(
        MusicalTimeMap map,
        SourceLoopEvidence evidence,
        double quartersPerBar)
    {
        IReadOnlyList<(long Start, long End)> periods = SourcePeriods(evidence);
        if (periods.Count == 0)
            return null;

        double total = 0;
        int count = 0;
        foreach ((long start, long end) in periods)
        {
            double bars = (map.SampleToQuarterPosition(end)
                - map.SampleToQuarterPosition(start)) / quartersPerBar;
            if (bars < 2 || bars > MaxRepeatPeriodBars)
                continue;
            double error = Math.Abs(bars - Math.Round(bars));
            total += Math.Max(0, 1.0 - Math.Min(1.0, error));
            count++;
        }
        return count == 0 ? null : total / count;
    }

    private static RepeatedContentEvidence RepeatedContentFit(BarFeature[] bars)
    {
        if (bars.Length < 4)
            return default;
        RepeatedBlock? best = null;
        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? block = FindBestRepeatedBlock(bars, period);
            if (block is null)
                continue;
            if (best is null
                || block.Similarity * block.Coverage > best.Similarity * best.Coverage
                || (block.Similarity * block.Coverage == best.Similarity * best.Coverage
                    && (block.LengthBars > best.LengthBars
                        || (block.LengthBars == best.LengthBars
                            && block.StartBar < best.StartBar))))
                best = block;
        }
        return best is null
            ? default
            : new RepeatedContentEvidence(best.Similarity, best.Coverage);
    }

    private static double? PhraseRegularityFit(BarFeature[] bars)
    {
        if (bars.Length < 8)
            return null;
        double total = 0;
        int count = 0;
        for (int start = 0; start + 8 <= bars.Length; start += 4)
        {
            total += bars[start..(start + 4)].Average(
                bar => bar.Similarity(bars[start + 4 + (bar.BarIndex - start)]));
            count++;
        }
        return count == 0 ? null : total / count;
    }

    private static RhythmRole InferRhythmRole(RhythmEvent hit)
    {
        string value = string.Join(
            " ",
            hit.Voice,
            hit.ChannelId,
            hit.InstrumentId,
            hit.Domain?.VoiceFamily.ToString() ?? string.Empty).ToLowerInvariant();
        if (value.Contains("bd") || value.Contains("kick") || value.Contains("bassdrum"))
            return RhythmRole.Bd;
        if (value.Contains("sd") || value.Contains("snare"))
            return RhythmRole.Sd;
        if (value.Contains("hh") || value.Contains("hihat") || value.Contains("hat"))
            return RhythmRole.Hh;
        if (value.Contains("tom"))
            return RhythmRole.Tom;
        if (value.Contains("top"))
            return RhythmRole.Top;
        if (value.Contains("rim"))
            return RhythmRole.Rim;
        return RhythmRole.Unknown;
    }

    private enum RhythmRole
    {
        Unknown,
        Bd,
        Sd,
        Hh,
        Tom,
        Top,
        Rim,
    }

    private static IReadOnlyList<(long Start, long End)> SourcePeriods(
        SourceLoopEvidence evidence)
    {
        var result = new List<(long Start, long End)>();
        if (evidence.EntrySample is long entry && evidence.RestartSamples.Count > 0)
            result.Add((entry, evidence.RestartSamples[0]));
        for (int index = 1; index < evidence.RestartSamples.Count; index++)
            result.Add((evidence.RestartSamples[index - 1], evidence.RestartSamples[index]));
        return result;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static double BlockSimilarity(BarFeature[] bars, int start, int length)
    {
        if (start < 0 || start + length * 2 > bars.Length)
            return 0;
        double total = 0;
        for (int offset = 0; offset < length; offset++)
            total += bars[start + offset].Similarity(bars[start + length + offset]);
        return total / length;
    }


    private static BarFeature[] BuildBars(
        MusicalTimeMap map,
        IReadOnlyList<NoteEvent>? notes,
        IReadOnlyList<RhythmEvent>? rhythm,
        double barOrigin,
        double quartersPerBar,
        int barCount)
    {
        var bars = new BarFeature[barCount];
        for (int i = 0; i < barCount; i++)
            bars[i] = new BarFeature(
                i,
                barOrigin + i * quartersPerBar,
                barOrigin + (i + 1) * quartersPerBar);

        if (notes is not null)
        {
            foreach (NoteEvent note in notes)
            {
                if (note is null || note.EndSample <= note.StartSample)
                    continue;

                long segmentStart = note.StartSample;
                double midiNote = note.InitialMidiNote;
                foreach (PitchChange change in (note.Pitch ?? Array.Empty<PitchChange>())
                    .OrderBy(change => change.SamplePosition))
                {
                    long segmentEnd = Math.Clamp(
                        change.SamplePosition, note.StartSample, note.EndSample);
                    if (segmentEnd > segmentStart)
                    {
                        AddNoteSpan(
                            map,
                            bars,
                            barOrigin,
                            quartersPerBar,
                            segmentStart,
                            segmentEnd,
                            PitchClass(midiNote),
                            note,
                            isAttack: segmentStart == note.StartSample);
                    }

                    if (change.SamplePosition >= note.EndSample)
                    {
                        segmentStart = note.EndSample;
                        break;
                    }
                    midiNote = change.MidiNote;
                    segmentStart = Math.Max(segmentStart, change.SamplePosition);
                }

                if (segmentStart < note.EndSample)
                {
                    AddNoteSpan(
                        map,
                        bars,
                        barOrigin,
                        quartersPerBar,
                        segmentStart,
                        note.EndSample,
                        PitchClass(midiNote),
                        note,
                        isAttack: segmentStart == note.StartSample);
                }
            }
        }

        if (rhythm is not null)
        {
            foreach (RhythmEvent hit in rhythm)
            {
                if (hit is null)
                    continue;
                double quarter = map.SampleToQuarterPosition(hit.SamplePosition);
                int barIndex = (int)Math.Floor((quarter - barOrigin) / quartersPerBar);
                if (barIndex < 0 || barIndex >= barCount)
                    continue;
                double barStart = barOrigin + barIndex * quartersPerBar;
                int onset = (int)Math.Floor(
                    Math.Clamp((quarter - barStart) / quartersPerBar * 16.0, 0, 15));
                bars[barIndex].AddRhythmOnset(onset, hit.ChannelId, hit.Domain);
            }
        }

        foreach (BarFeature bar in bars)
            bar.FinalizeFeatures();
        return bars;
    }

    private static void AddNoteSpan(
        MusicalTimeMap map,
        BarFeature[] bars,
        double barOrigin,
        double quartersPerBar,
        long startSample,
        long endSample,
        int pitchClass,
        NoteEvent note,
        bool isAttack)
    {
        double start = map.SampleToQuarterPosition(startSample);
        double end = map.SampleToQuarterPosition(endSample);
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            return;

        int firstBar = Math.Max(0, (int)Math.Floor((start - barOrigin) / quartersPerBar));
        int lastBar = Math.Min(
            bars.Length - 1,
            (int)Math.Floor((end - barOrigin - double.Epsilon) / quartersPerBar));
        int attackBar = Math.Clamp(firstBar, 0, bars.Length - 1);
        for (int barIndex = firstBar; barIndex <= lastBar; barIndex++)
        {
            double barStart = barOrigin + barIndex * quartersPerBar;
            double overlap = Math.Min(end, barStart + quartersPerBar)
                - Math.Max(start, barStart);
            if (overlap <= 0)
                continue;
            bool attack = isAttack && barIndex == attackBar;
            int onset = attack
                ? (int)Math.Floor(
                    Math.Clamp((start - barStart) / quartersPerBar * 16.0, 0, 15))
                : 0;
            bars[barIndex].AddNoteSegment(
                pitchClass,
                overlap,
                onset,
                note.ChannelId,
                note.Domain,
                attack);
        }
    }

    private static int PitchClass(double midiNote)
    {
        int rounded = (int)Math.Round(midiNote);
        int pitchClass = rounded % 12;
        return pitchClass < 0 ? pitchClass + 12 : pitchClass;
    }

    private static MusicalSection[] SegmentSections(
        BarFeature[] bars,
        IReadOnlyList<RepeatedBlock> loops)
    {
        if (bars.Length == 0)
            return Array.Empty<MusicalSection>();

        RepeatedBlock? primary = loops.FirstOrDefault();
        int[] starts = BuildPhraseStarts(bars.Length, primary);
        var representatives = new List<PhraseFeature>();
        var longRepresentatives = new List<PhraseFeature>();
        var sections = new List<MusicalSection>();

        foreach (int start in starts)
        {
            int available = bars.Length - start;
            bool turnaround = IsLoopTurnaround(start, primary);
            int length = turnaround ? 1 : Math.Min(4, available);
            if (length < 4 && !turnaround)
                continue;

            var phrase = new PhraseFeature(start, bars.Skip(start).Take(length).ToArray());
            PhraseFeature? longPhrase = available >= 8
                ? new PhraseFeature(start, bars.Skip(start).Take(8).ToArray())
                : null;

            string label;
            if (turnaround)
            {
                label = "TURNAROUND";
            }
            else
            {
                int repeatOf = representatives.FindIndex(
                    prior => prior.Similarity(phrase) >= SimilarityThreshold);

                // Compare complete eight-bar phrases in corresponding-bar order as
                // a tie-breaker. Four-bar PHRASE_* labels remain the public
                // segmentation unit while recognizing repeated 8-bar forms.
                if (repeatOf < 0 && longPhrase is not null)
                {
                    int longRepeat = longRepresentatives.FindIndex(
                        prior => prior.Similarity(longPhrase) >= SimilarityThreshold);
                    if (longRepeat >= 0 && longRepeat < representatives.Count)
                        repeatOf = longRepeat;
                }

                if (repeatOf < 0)
                {
                    repeatOf = representatives.Count;
                    representatives.Add(phrase);
                    if (longPhrase is not null)
                        longRepresentatives.Add(longPhrase);
                }
                label = PhraseLabel(repeatOf);
            }

            if (sections.Count > 0
                && sections[^1].Label == label
                && sections[^1].EndBar == start)
            {
                MusicalSection previous = sections[^1];
                sections[^1] = previous with { EndBar = start + length };
            }
            else
            {
                sections.Add(new MusicalSection(start, start + length, label));
            }
        }

        if (sections.Count > 0
            && sections[^1].Label != "TURNAROUND"
            && sections[^1].EndBar < bars.Length)
        {
            MusicalSection previous = sections[^1];
            sections[^1] = previous with { EndBar = bars.Length };
        }

        // Very short captures have no normal four-bar phrase to compare, but they
        // still need a deterministic section rather than an empty analysis.
        if (sections.Count == 0 && bars.Length < 4)
            sections.Add(new MusicalSection(0, bars.Length, PhraseLabel(0)));

        return sections.ToArray();
    }

    private static int[] BuildPhraseStarts(int barCount, RepeatedBlock? primary)
    {
        var starts = new HashSet<int>();
        if (primary is not { LengthBars: > 0 } loop)
        {
            for (int start = 0; start < barCount; start += 4)
                starts.Add(start);
            return starts.OrderBy(start => start).ToArray();
        }

        for (int start = 0; start < Math.Min(barCount, loop.StartBar); start += 4)
            starts.Add(start);

        for (int occurrence = loop.StartBar; occurrence < barCount; occurrence += loop.LengthBars)
        {
            int end = Math.Min(barCount, occurrence + loop.LengthBars);
            for (int start = occurrence; start < end; start += 4)
                starts.Add(start);
            if (end < barCount)
                starts.Add(end);
            if (occurrence + loop.LengthBars <= occurrence)
                break;
        }

        for (int start = loop.StartBar + loop.LengthBars * Math.Max(
                     1, (barCount - loop.StartBar + loop.LengthBars - 1) / loop.LengthBars);
             start < barCount;
             start += 4)
            starts.Add(start);

        return starts.OrderBy(start => start).ToArray();
    }

    private static bool IsLoopTurnaround(int start, RepeatedBlock? primary)
    {
        if (primary is not { LengthBars: > 0 } loop)
            return false;
        return start >= loop.StartBar + loop.LengthBars - 1
            && (start - loop.StartBar + 1) % loop.LengthBars == 0;
    }

    private static SourceLoopEvidence CaptureSourceLoopEvidence(
        VisualizationTimeline timeline)
    {
        LoopMarker[] markers = (timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            .Where(marker => marker is not null)
            .OrderBy(marker => marker.SamplePosition)
            .ThenBy(marker => marker.Iteration)
            .ToArray();
        long? entry = markers
            .FirstOrDefault(marker => marker.Kind == LoopMarkerKind.Start)
            ?.SamplePosition;
        long[] restarts = markers
            .Where(marker => marker.Kind == LoopMarkerKind.Restart)
            .Select(marker => marker.SamplePosition)
            .Distinct()
            .ToArray();
        return new SourceLoopEvidence(entry, restarts);
    }

    private static RepeatedBlock[]? DetectSourceLoop(
        MusicalTimeMap map,
        SourceLoopEvidence evidence,
        BarFeature[] bars)
    {
        if (bars.Length < 4)
            return null;

        double quartersPerBar = bars[0].QuarterEnd - bars[0].QuarterStart;
        var candidates = new List<RepeatedBlock>();
        foreach ((long start, long end) in SourcePeriods(evidence))
        {
            double periodExact = PeriodBarsExact(map, start, end, bars);
            int period = (int)Math.Round(periodExact, MidpointRounding.AwayFromZero);
            int startBar = (int)Math.Floor(
                (map.SampleToQuarterPosition(start) - bars[0].QuarterStart)
                / quartersPerBar);
            if (period < 2 || period > MaxRepeatPeriodBars
                || startBar < 0
                || startBar + period > bars.Length
                || Math.Abs(periodExact - period) > 0.25)
            {
                continue;
            }

            // A source entry/restart pair can describe a loop whose second
            // occurrence is outside the capture. Preserve that authoritative
            // boundary even when content comparison has no adjacent block.
            candidates.Add(new RepeatedBlock(
                startBar,
                period,
                Similarity: 1.0,
                Coverage: 1.0,
                SourceSupported: true));
        }

        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        (long Start, long End)[] sourcePeriods = SourcePeriods(evidence).ToArray();
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? candidate = FindBestRepeatedBlock(bars, period);
            if (candidate is null)
                continue;
            if (sourcePeriods.Length == 0)
            {
                candidates.Add(candidate);
                continue;
            }
            double sourceDistance = sourcePeriods
                .Select(value => PeriodBarsExact(map, value.Start, value.End, bars))
                .Min(value => Math.Abs(value - period));
            if (sourceDistance <= 1.0)
                candidates.Add(candidate with { SourceSupported = true });
        }

        return candidates
            .OrderByDescending(loop => loop.SourceSupported)
            .ThenByDescending(loop => loop.Similarity * loop.Coverage)
            .ThenByDescending(loop => loop.LengthBars)
            .ThenBy(loop => loop.StartBar)
            .Take(1)
            .ToArray();
    }

    private static double PeriodBarsExact(
        MusicalTimeMap map,
        long startSample,
        long endSample,
        BarFeature[] bars)
    {
        if (endSample <= startSample || bars.Length == 0)
            return 0;
        double quarters = map.SampleToQuarterPosition(endSample)
            - map.SampleToQuarterPosition(startSample);
        double quartersPerBar = bars[0].QuarterEnd - bars[0].QuarterStart;
        return quartersPerBar > 0 ? quarters / quartersPerBar : 0;
    }

    private static RepeatedBlock[] DetectRepeatedBlocks(BarFeature[] bars)
    {
        var loops = new List<RepeatedBlock>();
        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? candidate = FindBestRepeatedBlock(bars, period);
            if (candidate is not null)
                loops.Add(candidate);
        }

        return loops
            .OrderByDescending(loop => loop.SourceSupported)
            .ThenByDescending(loop => loop.Similarity * loop.Coverage)
            .ThenByDescending(loop => loop.LengthBars)
            .ThenBy(loop => loop.StartBar)
            .ToArray();
    }

    private static RepeatedBlock? FindBestRepeatedBlock(BarFeature[] bars, int period)
    {
        if (period < 2 || bars.Length < period * 2)
            return null;

        RepeatedBlock? best = null;
        for (int start = 0; start + period * 2 <= bars.Length; start++)
        {
            double similarity = BlockSimilarity(bars, start, start + period, period);
            double coverage = BlockCoverage(bars, start, start + period, period);
            if (similarity < SimilarityThreshold || coverage <= 0)
                continue;
            var candidate = new RepeatedBlock(start, period, similarity, coverage);
            if (best is null
                || similarity * coverage > best.Similarity * best.Coverage
                || (similarity * coverage == best.Similarity * best.Coverage
                    && (period > best.LengthBars
                        || (period == best.LengthBars && start < best.StartBar))))
                best = candidate;
        }
        return best;
    }

    private static double BlockCoverage(
        BarFeature[] bars,
        int left,
        int right,
        int length)
    {
        int material = 0;
        for (int offset = 0; offset < length; offset++)
        {
            if (!bars[left + offset].IsRest || !bars[right + offset].IsRest)
                material++;
        }
        return (double)material / length;
    }

    private static double BlockSimilarity(BarFeature[] bars, int left, int right, int length)
    {
        double total = 0;
        bool hasMaterial = false;
        for (int offset = 0; offset < length; offset++)
        {
            BarFeature first = bars[left + offset];
            BarFeature second = bars[right + offset];
            if (!first.IsRest || !second.IsRest)
                hasMaterial = true;
            total += first.Similarity(second);
        }
        return hasMaterial ? total / length : 0;
    }

    private static string PhraseLabel(int distinctIndex)
        => distinctIndex < 26
            ? $"PHRASE_{(char)('A' + distinctIndex)}"
            : $"PHRASE_S{distinctIndex - 26}";
}