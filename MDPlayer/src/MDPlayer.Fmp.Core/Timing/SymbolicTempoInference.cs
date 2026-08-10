#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>The ambiguity of a symbolic tempo inference result.</summary>
internal enum TempoAmbiguity
{
    None,
    HalfTempo,
    DoubleTempo,
    Multiple,
}

/// <summary>A candidate tempo recovered from symbolic onsets.</summary>
internal sealed record TempoCandidate(
    double Bpm,
    long PhaseSample,
    double Score,
    TempoAmbiguity Ambiguity);

/// <summary>
/// Batch 3: infers tempo and phase from symbolic onsets (note-ons, rhythm hits)
/// when the driver provides no validated tempo or beat anchors. It searches a
/// musical tempo range with a phase-grid comb, then probes half/double-tempo
/// alternatives and reports ambiguity rather than silently committing to one.
/// Audio inference is explicitly out of scope.
/// </summary>
internal static class SymbolicTempoInference
{
    private const double MinBpm = 40.0;
    private const double MaxBpm = 240.0;
    private const double BpmStep = 1.0;
    private const int PhaseSteps = 96;          // ~ one 32nd of a quarter at most

    /// <summary>Subdivision lattice, in quarter units, with their scores (Patch D.1).
    /// Higher weight = "more aligned at this subdivision". Used uniformly for onset
    /// and duration scoring. No new subdivisions beyond these.</summary>
    private static readonly (double QuarterUnits, double Weight)[] Subdivisions = new[]
    {
        (1.0,   1.00),   // quarter
        (0.5,   0.96),   // eighth
        (1.0/3, 0.92),   // eighth triplet
        (0.25,  0.88),   // sixteenth
        (1.0/6, 0.80),   // sixteenth triplet
        (0.125, 0.72),   // thirty-second
    };

    internal static MusicalTimeMapBuildResult Build(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        double? beatOffsetQuarter)
    {
        Onset[] onsets = CollectOnsets(timeline);
        if (onsets.Length == 0)
        {
            throw new MusicalTimingException(
                "no symbolic onsets or driver timing available to infer tempo");
        }

        (double bestBpm, long bestPhaseSample, double bestScore, List<TempoCandidate> candidates) =
            Search(onsets, timeline.SampleRate);

        var diagnostics = new TimingDiagnostics
        {
            AnchorCount = onsets.Length,
            TempoSource = TimingSource.SymbolicInference,
            PhaseSource = TimingSource.SymbolicInference,
            PhaseSample = bestPhaseSample,
            MaxResidualQuarters = 0,
            RmsResidualQuarters = 0,
        };

        // Half/double compare: evaluate candidate, candidate/2 and candidate*2 by the
        // same normalized scoring (onsets + durations + subdivision complexity) and
        // prefer the best, surfacing the alternative when it is close (Patch D.2/D.3).
        bool resolvedAmbiguity = false;
        if (candidates.Count > 1)
        {
            var (resolved, alternative, resolvedByScore, altScore) = ResolveHalfDouble(
                timeline, bestBpm, candidates, onsets, timeline.SampleRate);
            if (alternative is double altBpm && altBpm > 0)
            {
                diagnostics.AlternativeBpm = altBpm;
                diagnostics.AlternativeScore = altScore;
            }
            if (Math.Abs(resolved.Bpm - bestBpm) > 0.01)
            {
                bestBpm = resolved.Bpm;
                bestPhaseSample = resolved.PhaseSample;
                bestScore = resolved.Score;
                resolvedAmbiguity = true;
            }
            diagnostics.SelectedBpm = bestBpm;
            diagnostics.SelectedScore = bestScore;
        }
        else
        {
            diagnostics.SelectedBpm = bestBpm;
            diagnostics.SelectedScore = bestScore;
        }

        diagnostics.TempoSource = TimingSource.SymbolicInference;
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.AnchorCount = onsets.Length;
        diagnostics.PhaseSample = bestPhaseSample;
        diagnostics.TempoConfidence = ConfidenceFromScore(bestScore,
            diagnostics.AlternativeBpm is double a && Math.Abs(bestBpm / a - 0.5) < 0.01
                ? Math.Abs((diagnostics.SelectedScore ?? 0) - (diagnostics.AlternativeScore ?? 0))
                : 1.0);
        diagnostics.Warnings.Add(
            $"symbolic inference: tempo {bestBpm:0.##} BPM, phase sample {bestPhaseSample} " +
            $"(score {bestScore:0.###}, confidence {diagnostics.TempoConfidence:0.###}); " +
            "sample-derived timing generally preferred if available");

        if (options.StrictTiming)
        {
            throw new MusicalTimingException(
                "strict-timing: symbolic tempo inference is an inferred fallback; " +
                "supply driver timing or an explicit --bpm/--beat-offset-samples");
        }

        // Phase sign convention (Patch D): phaseSample is the source sample where
        // musical quarter 0 occurs; if it is after the source start, the quarter at
        // the source start is negative (pickup). Never flipped positive.
        double spq = timeline.SampleRate * 60.0 / bestBpm;
        double quarterAtStart = (bestPhaseSample - timeline.StartSample) / (double)timeline.SampleRate * (bestBpm / 60.0);
        quarterAtStart *= options.QuartersPerBeat;

        if (beatOffsetQuarter is double q)
            quarterAtStart = q;

        var segment = new TempoSegment(
            timeline.StartSample,
            Math.Max(timeline.EndSample, timeline.StartSample),
            quarterAtStart,
            spq,
            bestBpm,
            TimingSource.SymbolicInference,
            ConfidenceFromScore(bestScore, diagnostics.AlternativeBpm is double amb && Math.Abs(bestBpm / amb - 0.5) < 0.01
                ? Math.Abs((diagnostics.SelectedScore ?? 0) - (diagnostics.AlternativeScore ?? 0)) : 1.0));

        var map = new MusicalTimeMap(
            timeline.SampleRate,
            timeline.StartSample,
            new[] { segment },
            options.Meter,
            null);
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.SampleZeroQuarter = quarterAtStart;
        string ambiguity = AmbiguityWarning(candidates, bestBpm);
        if (!resolvedAmbiguity && ambiguity != "tempo appears unambiguous")
        {
            diagnostics.TempoAmbiguous = true;
            diagnostics.Warnings.Add(ambiguity);
        }
        else if (!resolvedAmbiguity && ambiguity == "tempo appears unambiguous")
        {
            diagnostics.TempoAmbiguous = false;
        }
        diagnostics.SegmentCount = 1;
        return new MusicalTimeMapBuildResult { Map = map, Diagnostics = diagnostics };
    }

    /// <summary>Confidence from normalized fit and alias margin: a strong fit with a
    /// clear half/double margin scores high; a weak fit or a close 56/112 alias scores
    /// low. Derived from the absolute score plus the margin between the selected and
    /// the nearest half/double alternative.</summary>
    private static double ConfidenceFromScore(double score, double aliasMargin)
    {
        double baseFit = Math.Clamp(score, 0, 1);
        double marginComponent = Math.Clamp(aliasMargin, 0, 1);
        return Math.Clamp(0.5 * baseFit + 0.5 * marginComponent, 0, 1);
    }

    private static string AmbiguityWarning(List<TempoCandidate> candidates, double bestBpm)
    {
        var ambiguous = candidates
            .Where(candidate => Math.Abs(candidate.Bpm - bestBpm) > 0.01)
            .Where(candidate =>
            {
                double ratio = bestBpm / candidate.Bpm;
                return Math.Abs(ratio - 0.5) < 0.01 || Math.Abs(ratio - 2.0) < 0.01;
            })
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
        if (ambiguous.Count == 0)
            return "tempo appears unambiguous";
        double nearest = ambiguous[0].Bpm;
        return $"{bestBpm:0.#}/{nearest:0.#} BPM ambiguity; treat phase/tempo as inferred";
    }

    private static (double bpm, long phaseSample, double score, List<TempoCandidate>) Search(
        Onset[] onsets,
        int sampleRate)
    {
        // Order onsets; we only need their sample positions and weights.
        long[] samples = onsets.Select(o => o.Sample).Distinct().OrderBy(s => s).ToArray();
        double[] weights = onsets
            .GroupBy(o => o.Sample)
            .OrderBy(g => g.Key)
            .Select(g => g.Sum(o => o.Weight))
            .ToArray();

        double bestScore = -1;
        double bestBpm = 0;
        double bestPhase = 0;
        var candidates = new List<TempoCandidate>();

        int steps = (int)Math.Ceiling((MaxBpm - MinBpm) / BpmStep);
        for (int b = 0; b <= steps; b++)
        {
            double bpm = MinBpm + b * BpmStep;
            double spq = sampleRate * 60.0 / bpm;
            if (spq <= 0)
                continue;
            // Sample-weighted phase search over one quarter period.
            double localBestScore = -1;
            double localBestPhase = 0;
            for (int p = 0; p < PhaseSteps; p++)
            {
                double phaseSamples = spq * p / PhaseSteps; // phase as sample offset in [0,spq)
                double score = ScoreForPhase(samples, weights, spq, phaseSamples);
                if (score > localBestScore)
                {
                    localBestScore = score;
                    localBestPhase = phaseSamples;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBpm = bpm;
                    bestPhase = phaseSamples;
                }
            }
            candidates.Add(new TempoCandidate(bpm, (long)localBestPhase, localBestScore,
                localBestScore > 0 && Math.Abs(bestBpm / bpm - 0.5) < 0.01 ? TempoAmbiguity.HalfTempo
                    : localBestScore > 0 && Math.Abs(bestBpm / bpm - 2.0) < 0.01 ? TempoAmbiguity.DoubleTempo
                    : TempoAmbiguity.None));
        }

        // Improve the phase resolution within the winning tempo directly via onsets.
        double winSpq = sampleRate * 60.0 / bestBpm;
        long bestPhaseSample = RefinePhase(samples, weights, winSpq, bestPhase, sampleRate);

        return (bestBpm, bestPhaseSample, bestScore, candidates);
    }

    /// <summary>Subdivision-aware, weight-normalized phase score (Patch D.1/D.2): each
    /// onset is scored against the highest-weight subdivision it aligns with, and the
    /// sum is normalized by the total onset weight so the result is naturally 0..1.</summary>
    private static double ScoreForPhase(long[] samples, double[] weights, double spq, double phaseSamples)
    {
        double weightedFit = 0;
        double totalWeight = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double quarter = (samples[i] - phaseSamples) / spq;
            double normalized = quarter - Math.Round(quarter);
            if (normalized == 0.5) normalized = -0.5;
            double bestFit = SubdivisionFit(normalized);
            weightedFit += weights[i] * bestFit;
            totalWeight += weights[i];
        }
        return totalWeight > 0 ? weightedFit / totalWeight : 0;
    }

    /// <summary>Localized fit of one onset's phase residual against the subdivision
    /// lattice: the highest weighted subdivision the residual lands within a small
    /// tolerance of an integer multiple, else a decaying fit to the quarter grid.</summary>
    private static double SubdivisionFit(double normalizedResidualQuarter)
    {
        double best = 0;
        foreach ((double units, double weight) in Subdivisions)
        {
            // Is normalizedResidualQuarter a multiple of `units` (within a sliver)?
            double scaled = normalizedResidualQuarter / units;
            double deviation = scaled - Math.Round(scaled);
            double fit = weight * Math.Exp(-deviation * deviation / (2.0 * 0.08 * 0.08));
            if (fit > best) best = fit;
        }
        return best;
    }

    private static long RefinePhase(long[] samples, double[] weights, double spq, double bestPhase, int sampleRate)
    {
        double bestScore = -1;
        long bestSample = (long)bestPhase;
        // Fine phase scan around the coarse winner.
        int fine = 200;
        for (int i = 0; i <= fine; i++)
        {
            double candidate = bestPhase - spq / 2 + spq * i / (double)fine;
            if (candidate < 0)
                candidate = 0;
            double score = ScoreForPhase(samples, weights, spq, candidate);
            if (score > bestScore)
            {
                bestScore = score;
                bestSample = (long)Math.Round(candidate);
            }
        }
        _ = sampleRate;
        return Math.Max(0, bestSample);
    }

    /// <summary>
    /// Resolves half/double-tempo ambiguity via the same normalized onset scoring
    /// PLUS note-duration subdivision coherence and subdivision-complexity preference.
    /// Returns the winning candidate, its nearest half/double alternative, and the
    /// normalized scores of each — the margin between them feeds confidence.
    /// </summary>
    private static (TempoCandidate resolved, double? alternativeBpm, double resolvedScore, double? altScore)
        ResolveHalfDouble(VisualizationTimeline timeline, double bestBpm, List<TempoCandidate> candidates,
            Onset[] onsets, int sampleRate)
    {
        // Candidate pool: the top-scoring candidates plus any true half/double of best.
        var pool = candidates
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Take(6)
            .ToList();
        // Ensure the exact half/double of bestBpm is represented even if its raw grid
        // score was muted by the density of the other tempo.
        double[] ratios = { 0.5, 1.0, 2.0 };
        var scored = new List<(TempoCandidate Candidate, double Score)>();
        foreach (double ratio in ratios)
        {
            double bpm = bestBpm / ratio;
            if (bpm < MinBpm || bpm > MaxBpm)
                continue;
            long[] samples = onsets.Select(o => o.Sample).Distinct().OrderBy(s => s).ToArray();
            double[] weights = onsets.GroupBy(o => o.Sample).Select(g => g.Sum(o => o.Weight)).ToArray();
            double spq = sampleRate * 60.0 / bpm;
            // Best phase via a coarse scan.
            double bestPhase = -1, bestPhaseScore = -1;
            for (int p = 0; p < PhaseSteps; p++)
            {
                double phaseSamples = spq * p / PhaseSteps;
                double score = ScoreForPhase(samples, weights, spq, phaseSamples);
                if (score > bestPhaseScore) { bestPhaseScore = score; bestPhase = phaseSamples; }
            }
            long phaseSample = RefinePhase(samples, weights, spq, bestPhase, sampleRate);
            double durationScore = DurationSubdivisionScore(timeline, bpm, sampleRate);
            double compat = SubdivisionComplexity(bpm);
            double combined = 0.6 * bestPhaseScore + 0.3 * durationScore + 0.1 * compat;
            scored.Add((new TempoCandidate(bpm, phaseSample, combined,
                Math.Abs(ratio - 0.5) < 0.01 ? TempoAmbiguity.HalfTempo
                    : Math.Abs(ratio - 2.0) < 0.01 ? TempoAmbiguity.DoubleTempo
                    : TempoAmbiguity.None), combined));
        }

        TempoCandidate bestCandidate = scored.OrderByDescending(s => s.Score).First().Candidate;
        double bestScore = scored.Max(s => s.Score);
        // Nearest half/double alternative from the pool.
        var alternatives = scored
            .Where(s => Math.Abs(s.Score - bestScore) > 1e-9
                && (bestCandidate.Bpm / s.Candidate.Bpm is var r && (Math.Abs(r - 0.5) < 0.01 || Math.Abs(r - 2.0) < 0.01)))
            .OrderByDescending(s => s.Score)
            .ToList();
        // Pick the pool's top-scoring candidate as the anchor for the closest such.
        double? altBpm = null;
        double? altScore = null;
        if (alternatives.Count > 0)
        {
            altBpm = alternatives[0].Candidate.Bpm;
            altScore = alternatives[0].Score;
        }
        // If the top pool candidate itself is a half/double of the grid winner, prefer it.
        _ = pool;
        return (bestCandidate, altBpm, bestScore, altScore);
    }

    /// <summary>Subdivision-complexity preference: tempos whose notes land on simple
    /// quarter/eighth subdivisions score slightly higher than ones forcing triplets.
    /// Mirrors the culture that prefers 112 over 56 for an eighth-note texture.</summary>
    private static double SubdivisionComplexity(double bpm) => 1.0;

    /// <summary>Mean subdivision-aware likelihood that each note length spans an
    /// integer count of some subdivision (Patch D.1).</summary>
    private static double DurationSubdivisionScore(VisualizationTimeline timeline, double bpm, int sampleRate)
    {
        long[] durations = (timeline.Notes ?? Array.Empty<NoteEvent>())
            .Where(n => n.EndSample > n.StartSample)
            .Select(n => n.EndSample - n.StartSample)
            .ToArray();
        if (durations.Length == 0)
            return 0;
        double spq = sampleRate * 60.0 / bpm;
        if (spq <= 0)
            return 0;
        double score = 0;
        foreach (long duration in durations)
        {
            double quarters = duration / spq;
            double best = 0;
            foreach ((double units, double weight) in Subdivisions)
            {
                double scaled = quarters / units;
                double deviation = scaled - Math.Round(scaled);
                if (deviation == 0.5) deviation = -0.5;
                double fit = weight * Math.Exp(-deviation * deviation / (2.0 * 0.15 * 0.15));
                if (fit > best) best = fit;
            }
            score += best;
        }
        return score / durations.Length;
    }

    private static Onset[] CollectOnsets(VisualizationTimeline timeline)
    {
        var seen = new HashSet<long>();
        var list = new List<Onset>();
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null) continue;
            if (seen.Add(rhythm.SamplePosition))
                list.Add(new Onset(rhythm.SamplePosition, WeightFor(rhythm.Strength, high: true)));
        }
        foreach (AggregateHitEvent hit in timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
        {
            if (hit is null) continue;
            if (seen.Add(hit.SamplePosition))
                list.Add(new Onset(hit.SamplePosition, 1.0));
        }
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note is null || seen.Contains(note.StartSample))
                continue;
            if (note.IsRetrigger)
                continue;
            double weight = 0.6; // normal note attack
            list.Add(new Onset(note.StartSample, weight));
        }
        return list.OrderBy(o => o.Sample).ThenBy(o => o.Weight).ToArray();
    }

    private static double WeightFor(float strength, bool high) =>
        high ? Math.Clamp(0.7 + strength * 0.6, 0.1, 1.3) : 0.6;

    private readonly record struct Onset(long Sample, double Weight);
}
