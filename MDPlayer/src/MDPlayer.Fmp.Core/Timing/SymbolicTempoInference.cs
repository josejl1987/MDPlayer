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
    private const double SigmaQuarters = 0.08;  // comb tolerance

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
            MaxResidualQuarters = 0,
            RmsResidualQuarters = 0,
        };

        // Option 1: resolve half/double tempo ambiguity via note-duration
        // quarter-coherence. Onset spacing alone cannot distinguish 56 vs 112 BPM
        // (a 16th-note texture is invariant), but note lengths are not: at the true
        // tempo most notes span near-integer quarters. Prefer the candidate whose
        // durations are most grid-aligned, only when the onset ambiguity is real.
        bool resolvedAmbiguity = false;
        if (candidates.Count > 1)
        {
            var (resolved, alternativeStrong) = ResolveDurationCoherence(timeline, bestBpm, candidates, timeline.SampleRate);
            if (alternativeStrong)
            {
                resolvedAmbiguity = true;
                diagnostics.Warnings.Add(
                    $"duration-coherence selected {resolved.Bpm:0.#} BPM over its half/double tempo alternative");
            }
            if (Math.Abs(resolved.Bpm - bestBpm) > 0.01)
            {
                bestBpm = resolved.Bpm;
                bestPhaseSample = resolved.PhaseSample;
                bestScore = resolved.Score;
            }
        }

        diagnostics.TempoSource = TimingSource.SymbolicInference;
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.AnchorCount = onsets.Length;
        diagnostics.Warnings.Add(
            $"symbolic inference: tempo {bestBpm:0.##} BPM, phase sample {bestPhaseSample} " +
            $"(score {bestScore:0.###}); sample-derived timing generally preferred if available");

        if (options.StrictTiming)
        {
            throw new MusicalTimingException(
                "strict-timing: symbolic tempo inference is an inferred fallback; " +
                "supply driver timing or an explicit --bpm/--beat-offset-samples");
        }

        double spq = timeline.SampleRate * 60.0 / bestBpm;
        double quarterAtStart;
        if (bestPhaseSample > 0)
            quarterAtStart = (bestPhaseSample / (double)timeline.SampleRate) * (bestBpm / 60.0);
        else
            quarterAtStart = 0;
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
            ConfidenceFromScore(bestScore));

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

    private static double ScoreForPhase(long[] samples, double[] weights, double spq, double phaseSamples)
    {
        double score = 0;
        double invSigma2 = 1.0 / (2.0 * SigmaQuarters * SigmaQuarters);
        for (int i = 0; i < samples.Length; i++)
        {
            double quarter = (samples[i] - phaseSamples) / spq;
            double residual = quarter - Math.Round(quarter); // distance to nearest grid line
            if (residual == 0.5)
                residual = -0.5;
            score += weights[i] * Math.Exp(-residual * residual * invSigma2);
        }
        return score;
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

    private static double ConfidenceFromScore(double score) => Math.Clamp(score, 0, 1);

    /// <summary>
    /// Resolves half/double-tempo ambiguity: among the best-scoring candidates and
    /// any candidate at 1/2 or 2x the best tempo, pick the one whose note durations
    /// are most quarter-coherent (note length ≈ an integer number of quarters).
    /// Onset spacing cannot tell 56 from 112 BPM, but real note lengths can.
    /// </summary>
    private static (TempoCandidate resolved, bool alternativeStrong) ResolveDurationCoherence(
        VisualizationTimeline timeline,
        double bestBpm,
        List<TempoCandidate> candidates,
        int sampleRate)
    {
        // Drop zero-score candidates (never a real option).
        var pool = candidates
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Take(4)
            .ToList();
        if (pool.Count <= 1)
            return (pool.Count == 1 ? pool[0] : new TempoCandidate(bestBpm, 0, 0, TempoAmbiguity.None),
                alternativeStrong: false);

        long[] durations = (timeline.Notes ?? Array.Empty<NoteEvent>())
            .Where(n => n.EndSample > n.StartSample)
            .Select(n => n.EndSample - n.StartSample)
            .ToArray();

        // Rank by note-duration quarter-coherence alone. Onset scores are unreliable
        // ties here (16th-note textures are tempo-invariant), but real note lengths
        // strongly prefer the true tempo over its 2x/0.5x rival.
        var ranked = pool
            .Select(candidate => (Candidate: candidate,
                Score: durations.Length == 0 ? 0 : DurationCoherenceScore(durations, candidate.Bpm, sampleRate)))
            .OrderByDescending(entry => entry.Score)
            .ToList();

        TempoCandidate best = ranked[0].Candidate;
        // The ambiguity is resolved whenever the duration-coherence winner clearly
        // dominates its nearest rival — a real note-length preference between the
        // half/double tempo candidates. Onset scores are inert here.
        bool alternativeStrong = ranked.Count >= 2
            && ranked[0].Score > ranked[1].Score;
        return (best, alternativeStrong);
    }

    /// <summary>Mean likelihood that each note length lands on an integer quarter.</summary>
    private static double DurationCoherenceScore(long[] durations, double bpm, int sampleRate)
    {
        double spq = sampleRate * 60.0 / bpm;
        if (spq <= 0)
            return 0;
        double score = 0;
        foreach (long duration in durations)
        {
            double quarters = duration / spq;
            double residual = quarters - Math.Round(quarters);
            if (residual == 0.5)
                residual = -0.5;
            score += Math.Exp(-residual * residual / (2.0 * 0.15 * 0.15));
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
