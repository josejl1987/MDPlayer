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
    /// <summary>
    /// Cosine-similarity floor at which two bars (or a bar and a section representative)
    /// are treated as the same harmonic/textural material.
    /// </summary>
    private const double SimilarityThreshold = 0.85;

    private static readonly int[] RepeatPeriods = { 4, 8, 16, 24 };

    public static MusicalStructure Analyze(MusicalTimeMap map, VisualizationTimeline timeline)
    {
        if (map.Meter is not Meter meter || meter.QuartersPerBar <= 0
            || map.FirstDownbeatQuarter is null
            || map.IsTempoAmbiguous
            || map.Confidence < 0.60)
            return MusicalStructure.Empty;

        double quartersPerBar = meter.QuartersPerBar;
        double barOrigin = map.FirstDownbeatQuarter.Value;
        double firstQuarter = map.SampleToQuarterPosition(map.FirstSample);
        double lastQuarter = map.SampleToQuarterPosition(map.EndSample);

        int barCount = Math.Max(1, (int)Math.Ceiling((lastQuarter - firstQuarter) / quartersPerBar));
        BarFeature[] bars = BuildBars(
            map, timeline.Notes, timeline.Rhythm, barOrigin, quartersPerBar, barCount);
        MusicalSection[] sections = SegmentSections(bars);
        RepeatedBlock[] loops = DetectSourceLoop(map, timeline, bars)
            ?? DetectRepeatedBlocks(bars);

        return new MusicalStructure
        {
            Bars = bars,
            Sections = sections,
            Loops = loops,
            PrimaryLoop = loops.Length > 0 ? loops[0] : null,
        };
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
        {
            bars[i] = new BarFeature(i, barOrigin + i * quartersPerBar, barOrigin + (i + 1) * quartersPerBar);
        }

        if (notes is not null)
        {
            foreach (NoteEvent note in notes)
            {
                double start = map.SampleToQuarterPosition(note.StartSample);
                double end = map.SampleToQuarterPosition(note.EndSample);
                int barIndex = (int)Math.Floor((start - barOrigin) / quartersPerBar);
                if (barIndex < 0)
                    barIndex = 0;
                if (barIndex >= barCount)
                    continue;
                double barStart = barOrigin + barIndex * quartersPerBar;
                double overlap = Math.Max(0.001, Math.Min(end, barStart + quartersPerBar) - Math.Max(start, barStart));
                int onset = (int)Math.Floor(Math.Clamp((start - barStart) / quartersPerBar * 16.0, 0, 15));
                bars[barIndex].AddNoteOn(
                    PitchClass(note.InitialMidiNote), overlap, onset, note.ChannelId);
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
                int onset = (int)Math.Floor(Math.Clamp((quarter - barStart) / quartersPerBar * 16.0, 0, 15));
                bars[barIndex].AddRhythmOnset(onset);
            }
        }

        foreach (BarFeature bar in bars)
            bar.FinalizeFeatures();

        return bars;
    }

    private static int PitchClass(double midiNote)
    {
        int rounded = (int)Math.Round(midiNote);
        int pitchClass = rounded % 12;
        return pitchClass < 0 ? pitchClass + 12 : pitchClass;
    }

    private static MusicalSection[] SegmentSections(BarFeature[] bars)
    {
        if (bars.Length == 0)
            return Array.Empty<MusicalSection>();

        var starts = new List<int> { 0 };
        for (int bar = 4; bar < bars.Length; bar += 4)
            starts.Add(bar);

        var representatives = new List<BarFeature>();
        var sections = new List<MusicalSection>();
        BarFeature? previousUnit = null;
        for (int index = 0; index < starts.Count; index++)
        {
            int start = starts[index];
            int end = index + 1 < starts.Count ? starts[index + 1] : bars.Length;
            BarFeature representative = bars[start];
            if (sections.Count > 0
                && previousUnit is not null
                && representative.Similarity(previousUnit) >= SimilarityThreshold)
            {
                MusicalSection previous = sections[^1];
                sections[^1] = previous with { EndBar = end };
                previousUnit = representative;
                continue;
            }

            int repeatOf = representatives.FindIndex(
                prior => representative.Similarity(prior) >= SimilarityThreshold);
            if (repeatOf < 0)
            {
                repeatOf = representatives.Count;
                representatives.Add(representative);
            }
            sections.Add(new MusicalSection(start, end, SectionLabel(repeatOf)));
            previousUnit = representative;
        }
        return sections.ToArray();
    }
    private static RepeatedBlock[]? DetectSourceLoop(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        BarFeature[] bars)
    {
        LoopMarker? start = timeline.LoopMarkers?
            .FirstOrDefault(marker => marker.Kind == LoopMarkerKind.Start);
        LoopMarker? restart = timeline.LoopMarkers?
            .Where(marker => marker.Kind == LoopMarkerKind.Restart)
            .OrderBy(marker => marker.SamplePosition)
            .FirstOrDefault();
        if (start is null || restart is null)
            return null;

        double startQuarter = map.SampleToQuarterPosition(start.SamplePosition);
        double endQuarter = map.SampleToQuarterPosition(restart.SamplePosition);
        int startBar = NearestBar(bars, startQuarter);
        int endBar = NearestBar(bars, endQuarter);
        if (startBar < 0 || endBar <= startBar || endBar > bars.Length)
            return null;
        if (Math.Abs(bars[startBar].QuarterStart - startQuarter) > 0.25
            || Math.Abs(bars[endBar].QuarterStart - endQuarter) > 0.25)
            return null;
        return new[] { new RepeatedBlock(startBar, endBar - startBar) };
    }

    private static int NearestBar(BarFeature[] bars, double quarter)
    {
        int best = -1;
        double distance = double.MaxValue;
        for (int index = 0; index < bars.Length; index++)
        {
            double candidate = Math.Abs(bars[index].QuarterStart - quarter);
            if (candidate < distance)
            {
                distance = candidate;
                best = index;
            }
        }
        return best;
    }

    private static string SectionLabel(int distinctIndex)
        => distinctIndex < 26 ? ((char)('A' + distinctIndex)).ToString() : $"S{distinctIndex - 26}";

    private static RepeatedBlock[] DetectRepeatedBlocks(BarFeature[] bars)
    {
        var loops = new List<RepeatedBlock>();
        foreach (int period in RepeatPeriods)
        {
            if (bars.Length < 2 * period)
                continue;

            int bestStart = -1;
            int bestRun = 0;
            int runStart = 0;
            int run = 0;
            for (int i = 0; i + period < bars.Length; i++)
            {
                if (!bars[i].IsRest && bars[i].Similarity(bars[i + period]) >= SimilarityThreshold)
                {
                    if (run == 0)
                        runStart = i;
                    run++;
                }
                else
                {
                    run = 0;
                }

                if (run > bestRun)
                {
                    bestRun = run;
                    bestStart = runStart;
                }
            }

            // A full period must repeat at least once for a genuine loop.
            if (bestRun >= period)
                loops.Add(new RepeatedBlock(bestStart, period));
        }

        // Fundamental (shortest) period first, then larger structural repeats.
        return loops.OrderBy(loop => loop.LengthBars).ThenBy(loop => loop.StartBar).ToArray();
    }
}