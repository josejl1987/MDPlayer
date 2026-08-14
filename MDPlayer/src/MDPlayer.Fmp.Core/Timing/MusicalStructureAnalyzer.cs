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

    /// <summary>
    /// Scores retained timing grids against source-loop and repeated-content evidence.
    /// Only symbolic maps are eligible; authoritative driver/user timing is unchanged.
    /// </summary>
    internal static MusicalTimeMap SelectGrid(
        MusicalTimeMap map,
        VisualizationTimeline timeline)
    {
        if (map.Segments.Count != 1 || map.GridCandidates.Count == 0
            || map.Segments[0].Source != TimingSource.SymbolicInference)
            return map;

        bool hasMaterial = (timeline.Notes ?? Array.Empty<NoteEvent>()).Any(note => note is not null)
            || (timeline.Rhythm ?? Array.Empty<RhythmEvent>()).Any(hit => hit is not null)
            || (timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
                .Any(hit => hit is not null);
        if (!hasMaterial)
            return map;

        MusicalGridCandidate? bestCandidate = null;
        double bestScore = double.NegativeInfinity;
        foreach (MusicalGridCandidate candidate in map.GridCandidates)
        {
            MusicalTimeMap variant = VariantMap(map, candidate);
            double score = GridStructuralScore(variant, timeline);
            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is not MusicalGridCandidate selected
            || !double.IsFinite(bestScore))
            return map;

        // Structural evidence is allowed to resolve a low-confidence symbolic
        // alias. Keep the original confidence when it is stronger, but make a
        // positive joint score visible to Analyze so a validated grid is usable.
        double confidence = Math.Clamp(Math.Max(map.Confidence, bestScore), 0.0, 1.0);
        return VariantMap(map, selected, confidence);
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
        RepeatedBlock[] loops = DetectSourceLoop(
            map, CaptureSourceLoopEvidence(timeline), bars)
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
        double? confidence = null)
    {
        double samplesPerQuarter = source.SampleRate * 60.0 / candidate.Bpm;
        TempoSegment segment = source.Segments[0] with
        {
            QuarterPositionAtStart = candidate.BeatPhase,
            SamplesPerQuarter = samplesPerQuarter,
            BeatsPerMinute = candidate.Bpm,
        };
        return new MusicalTimeMap(
            source.SampleRate,
            source.StartSample,
            new[] { segment },
            candidate.Meter,
            candidate.BeatPhase,
            confidence ?? source.Confidence,
            source.AlternateBpm,
            source.IsTempoAmbiguous,
            source.GridCandidates);
    }


    private static double GridStructuralScore(
        MusicalTimeMap map,
        VisualizationTimeline timeline)
    {
        if (map.Meter is not Meter meter || map.FirstDownbeatQuarter is not double downbeat)
            return double.NegativeInfinity;

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

        double onsetFit = OnsetGridFit(map, timeline, downbeat);
        double rhythmFit = RhythmRoleFit(map, timeline, downbeat, quartersPerBar);
        double sourceFit = SourcePeriodFit(map, timeline, downbeat, quartersPerBar);
        double repeatedFit = RepeatedContentFit(bars);
        double phraseFit = PhraseRegularityFit(bars);
        return 0.30 * onsetFit
            + 0.20 * rhythmFit
            + 0.20 * sourceFit
            + 0.20 * repeatedFit
            + 0.10 * phraseFit;
    }

    private static double OnsetGridFit(
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
            return 0;

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

    private static double RhythmRoleFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat,
        double quartersPerBar)
    {
        RhythmEvent[] hits = (timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .Where(hit => hit is not null)
            .ToArray();
        if (hits.Length == 0)
            return 0.5;

        double total = 0;
        foreach (RhythmEvent hit in hits)
        {
            double beat = PositiveModulo(
                map.SampleToQuarterPosition(hit.SamplePosition) - downbeat,
                quartersPerBar);
            double nearestBeat = Math.Round(beat);
            double residual = Math.Abs(beat - nearestBeat);
            total += 1.0 - Math.Min(0.5, residual) / 0.5;
        }
        return total / hits.Length;
    }

    private static double SourcePeriodFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat,
        double quartersPerBar)
    {
        long[] restarts = (timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            .Where(marker => marker is not null && marker.Kind == LoopMarkerKind.Restart)
            .OrderBy(marker => marker.SamplePosition)
            .Select(marker => marker.SamplePosition)
            .ToArray();
        if (restarts.Length < 2)
            return 0.5;

        double total = 0;
        int count = 0;
        for (int index = 1; index < restarts.Length; index++)
        {
            double bars = (map.SampleToQuarterPosition(restarts[index])
                - map.SampleToQuarterPosition(restarts[index - 1])) / quartersPerBar;
            if (bars < 2 || bars > MaxRepeatPeriodBars)
                continue;
            double error = Math.Abs(bars - Math.Round(bars));
            total += Math.Max(0, 1.0 - Math.Min(1.0, error));
            count++;
        }
        return count == 0 ? 0.5 : total / count;
    }

    private static double RepeatedContentFit(BarFeature[] bars)
    {
        if (bars.Length < 4)
            return 0;
        double best = 0;
        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? block = FindBestRepeatedBlock(bars, period);
            if (block is not null)
                best = Math.Max(best, BlockSimilarity(bars, block.StartBar, period));
        }
        return best;
    }

    private static double PhraseRegularityFit(BarFeature[] bars)
    {
        if (bars.Length < 8)
            return 0.5;
        double total = 0;
        int count = 0;
        for (int start = 0; start + 8 <= bars.Length; start += 4)
        {
            total += bars[start..(start + 4)].Average(
                bar => bar.Similarity(bars[start + 4 + (bar.BarIndex - start)]));
            count++;
        }
        return count == 0 ? 0.5 : total / count;
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
                if (note.EndSample <= note.StartSample)
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
                // a tie-breaker. Four-bar labels remain the public segmentation unit,
                // preserving A/B/A/B labels while recognizing repeated 8-bar forms.
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
                label = SectionLabel(repeatOf);
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
            sections.Add(new MusicalSection(0, bars.Length, "A"));

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

        long[] restarts = evidence.RestartSamples.ToArray();
        var sourcePeriods = new List<double>();
        for (int index = 1; index < restarts.Length; index++)
        {
            double period = PeriodBarsExact(
                map, restarts[index - 1], restarts[index], bars);
            if (period >= 2)
                sourcePeriods.Add(period);
        }

        if (evidence.EntrySample is long entry && restarts.Length > 0)
        {
            double period = PeriodBarsExact(map, entry, restarts[0], bars);
            if (period >= 2)
                sourcePeriods.Add(period);
        }

        // A single restart without an entry or a second restart is intentionally
        // insufficient source evidence. Analyze() falls back to repeated content.
        if (sourcePeriods.Count == 0)
            return null;

        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        RepeatedBlock? best = null;
        double bestScore = double.NegativeInfinity;
        double bestDistance = double.PositiveInfinity;
        double bestSimilarity = double.NegativeInfinity;
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? candidate = FindBestRepeatedBlock(bars, period);
            if (candidate is null)
                continue;

            double similarity = BlockSimilarity(
                bars, candidate.StartBar, candidate.StartBar + candidate.LengthBars,
                candidate.LengthBars);
            double sourceDistance = sourcePeriods.Min(value => Math.Abs(value - period));
            if (sourceDistance > 1.0)
                continue;

            // Source timing wins ties and near misses; content remains a required
            // validation gate through FindBestRepeatedBlock.
            double score = similarity + 2.0 * (1.0 - sourceDistance);
            if (score > bestScore
                || (score == bestScore && sourceDistance < bestDistance)
                || (score == bestScore && sourceDistance == bestDistance
                    && similarity > bestSimilarity)
                || (score == bestScore && sourceDistance == bestDistance
                    && similarity == bestSimilarity
                    && (best is null || period < best.LengthBars)))
            {
                best = candidate;
                bestScore = score;
                bestDistance = sourceDistance;
                bestSimilarity = similarity;
            }
        }

        return best is null ? null : new[] { best };
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

    private static int PeriodBars(
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
        return (int)Math.Round(quarters / quartersPerBar, MidpointRounding.AwayFromZero);
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
            .OrderByDescending(loop => loop.LengthBars)
            .ThenBy(loop => loop.StartBar)
            .ToArray();
    }

    private static RepeatedBlock? FindBestRepeatedBlock(BarFeature[] bars, int period)
    {
        if (period < 2 || bars.Length < period * 2)
            return null;

        RepeatedBlock? best = null;
        double bestSimilarity = SimilarityThreshold;
        for (int start = 0; start + period * 2 <= bars.Length; start++)
        {
            double similarity = BlockSimilarity(bars, start, start + period, period);
            if (similarity >= bestSimilarity)
            {
                bestSimilarity = similarity;
                best = new RepeatedBlock(start, period);
            }
        }
        return best;
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

    private static string SectionLabel(int distinctIndex)
        => distinctIndex < 26 ? ((char)('A' + distinctIndex)).ToString() : $"S{distinctIndex - 26}";
}