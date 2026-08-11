#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Optional counters describing the work a tempo-inference run performed.
/// Instrumented via the <see cref="SymbolicTempoInference.Build"/> overload that
/// reports them; the default <see cref="SymbolicTempoInference.Build"/> path does
/// not allocate or count. Counters are additive across the whole search
/// (coarse phases, fine refinement, and the half/double metrical-family probes).
/// </summary>
internal readonly record struct TempoInferenceCounters(
    int OnsetCount,
    long ScoreForPhaseCalls,
    long SubdivisionFitEvals);

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

    /// <summary>Relative tie epsilon for phase-selection comparisons (TI-HOIST).
    /// The hoisted factoring (<c>normalized[i] - phaseQuarters</c> vs the original
    /// <c>(samples[i] - phaseSamples) / spq</c>) moves last-ulp rounding, and the
    /// 96-step phase grid produces exact-arithmetic plateaus (e.g. phases a half
    /// quarter apart can score identically); which plateau member wins must not
    /// depend on float rounding. Treating scores within this relative band as a
    /// tie keeps the FIRST-encountered phase — the pre-hoist behavior — instead of
    /// letting rounding flip the decision. 1e-12 is ~1000x above accumulation
    /// noise (~1e-15) and ~8 orders below any musically meaningful gap (~1e-4).
    /// Internal so the pruning equivalence test can replicate the update rules
    /// exactly (TI-PRUNE).</summary>
    internal const double ScoreTieEpsilon = 1e-12;

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

    /// <summary>Default build: no instrumentation, no counter allocation.</summary>
    internal static MusicalTimeMapBuildResult Build(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        double? beatOffsetQuarter) =>
        Build(timeline, options, beatOffsetQuarter, out _);

    /// <summary>
    /// Build overload with opt-in counters (TI-INSTRUMENT). The default path
    /// (<see cref="Build(VisualizationTimeline,MusicalTimeMapOptions,double?)"/>)
    /// routes here with <c>out _</c>, so instrumentation adds no allocation and
    /// no observable change to inference output. Only callers that opt in (the
    /// benchmark harness / tests via InternalsVisibleTo) pay for the counter.
    /// </summary>
    internal static MusicalTimeMapBuildResult Build(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        double? beatOffsetQuarter,
        out TempoInferenceCounters counters)
    {
        var acc = new CounterAccumulator();
        Onset[] onsets = CollectOnsets(timeline);
        acc.OnsetCount = onsets.Length;
        MusicalTimeMapBuildResult result = BuildCore(timeline, options, beatOffsetQuarter, onsets, acc);
        counters = new TempoInferenceCounters(acc.OnsetCount, acc.ScoreForPhaseCalls, acc.SubdivisionFitEvals);
        return result;
    }

    private static MusicalTimeMapBuildResult BuildCore(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        double? beatOffsetQuarter,
        Onset[] onsets,
        CounterAccumulator? acc)
    {
        if (onsets.Length == 0)
        {
            throw new MusicalTimingException(
                "no symbolic onsets or driver timing available to infer tempo");
        }

        (double bestBpm, long bestPhaseSample, double bestScore, List<TempoCandidate> candidates) =
            Search(onsets, timeline.SampleRate, acc);

        var diagnostics = new TimingDiagnostics
        {
            AnchorCount = onsets.Length,
            TempoSource = TimingSource.SymbolicInference,
            PhaseSource = TimingSource.SymbolicInference,
            PhaseSample = bestPhaseSample,
            MaxResidualQuarters = 0,
            RmsResidualQuarters = 0,
        };

        // Half/double compare: evaluate the full power-of-two metrical family by the
        // combined octave-fair scoring (onsets + durations + accents + tempo prior)
        // and prefer the best, surfacing the alternative when it is close
        // (Patch D.2/D.3/D.4).
        if (candidates.Count > 1)
        {
            var (resolved, alternative, resolvedByScore, altScore) = ResolveHalfDouble(
                timeline, bestBpm, candidates, onsets, timeline.SampleRate, acc);
            // The ResolveHalfDouble combined score is the decision metric, so it is
            // the SelectedScore reported for BOTH the selected and (below) the
            // alternative — the two are directly comparable.
            bestScore = resolvedByScore;
            if (alternative is double altBpm && altBpm > 0)
            {
                diagnostics.AlternativeBpm = altBpm;
                diagnostics.AlternativeScore = altScore;
            }
            if (Math.Abs(resolved.Bpm - bestBpm) > 0.01)
            {
                bestBpm = resolved.Bpm;
                bestPhaseSample = resolved.PhaseSample;
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
            diagnostics.AlternativeBpm is double a && IsMetricalFamilyRatio(bestBpm / a)
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
        double quarterAtStart = (timeline.StartSample - bestPhaseSample) / (double)timeline.SampleRate * (bestBpm / 60.0);
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
            ConfidenceFromScore(bestScore, diagnostics.AlternativeBpm is double amb && IsMetricalFamilyRatio(bestBpm / amb)
                ? Math.Abs((diagnostics.SelectedScore ?? 0) - (diagnostics.AlternativeScore ?? 0)) : 1.0));

        var map = new MusicalTimeMap(
            timeline.SampleRate,
            timeline.StartSample,
            new[] { segment },
            options.Meter,
            null);
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.SampleZeroQuarter = quarterAtStart;
        // Metrical-family ambiguity is surfaced whenever ResolveHalfDouble found a
        // musically equivalent alternative, whether or not the octave was changed:
        // resolving the octave does not make the alternative disappear (D.4).
        diagnostics.TempoAmbiguous = diagnostics.AlternativeBpm is double;
        if (diagnostics.TempoAmbiguous)
        {
            diagnostics.Warnings.Add(
                $"{bestBpm:0.#}/{diagnostics.AlternativeBpm!.Value:0.#} BPM ambiguity; " +
                "treat phase/tempo as inferred");
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

    private static (double bpm, long phaseSample, double score, List<TempoCandidate>) Search(
        Onset[] onsets,
        int sampleRate,
        CounterAccumulator? acc)
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
        // TI-HOIST: suffix sums over onset weights (weightSuffix[i] = sum of
        // weights[j] for j >= i) are tempo- and phase-independent, so they are
        // built once here. Consumed by the next optimization task (TI-PRUNE);
        // built now even if unused.
        double[] weightSuffix = new double[weights.Length];
        double suffixAccum = 0;
        for (int i = weights.Length - 1; i >= 0; i--)
        {
            suffixAccum += weights[i];
            weightSuffix[i] = suffixAccum;
        }

        for (int b = 0; b <= steps; b++)
        {
            double bpm = MinBpm + b * BpmStep;
            double spq = sampleRate * 60.0 / bpm;
            if (spq <= 0)
                continue;
            // TI-HOIST: onset samples normalized to quarter units once per tempo;
            // the phase loop then only subtracts (phaseSamples / spq).
            double[] normalized = new double[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                normalized[i] = samples[i] / spq;
            // Sample-weighted phase search over one quarter period.
            double localBestScore = -1;
            double localBestPhase = 0;
            for (int p = 0; p < PhaseSteps; p++)
            {
                double phaseSamples = spq * p / PhaseSteps; // phase as sample offset in [0,spq)
                double phaseQuarters = phaseSamples / spq;
                // TI-PRUNE: incumbent = the per-BPM best achieved so far
                // (localBestScore is always <= the global bestScore, so skipping
                // phases that cannot beat it cannot affect the global winner).
                double score = ScoreForPhase(normalized, weights, weightSuffix, phaseQuarters, localBestScore, acc);
                if (score > localBestScore * (1 + ScoreTieEpsilon))
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
                localBestScore > 0 && IsMetricalFamilyRatio(bestBpm / bpm) && Math.Abs(bestBpm / bpm - 0.5) < 0.01 ? TempoAmbiguity.HalfTempo
                    : localBestScore > 0 && IsMetricalFamilyRatio(bestBpm / bpm) && Math.Abs(bestBpm / bpm - 2.0) < 0.01 ? TempoAmbiguity.DoubleTempo
                    : TempoAmbiguity.None));
        }

        // Improve the phase resolution within the winning tempo directly via onsets.
        double winSpq = sampleRate * 60.0 / bestBpm;
        long bestPhaseSample = RefinePhase(samples, weights, winSpq, bestPhase, sampleRate, acc);

        return (bestBpm, bestPhaseSample, bestScore, candidates);
    }

    /// <summary>Subdivision-aware, weight-normalized phase score (Patch D.1/D.2): each
    /// onset is scored against the highest-weight subdivision it aligns with, and the
    /// sum is normalized by the total onset weight so the result is naturally 0..1.
    /// TI-HOIST: the tempo-dependent onset normalization is hoisted out of the phase
    /// loop — callers precompute <paramref name="normalized"/> (onset sample over the
    /// fixed samples-per-quarter) once per tempo and pass the phase as
    /// <paramref name="phaseQuarters"/> (phase sample over the same spq), so the
    /// per-onset division becomes a subtraction. Winner-identity factoring:
    /// low-bit differences vs. the original <c>(sample - phaseSamples) / spq</c> are
    /// expected and accepted.
    /// TI-PRUNE: <paramref name="weightSuffix"/> (built once per Search) enables an
    /// exact suffix-sum prune — the only behavioral difference from the unpruned
    /// reference is that some phases return early with score 0 instead of their true
    /// score. Every phase that is NOT pruned returns a bit-identical score, and a
    /// pruned phase can never have beaten <paramref name="incumbentScore"/> (see the
    /// in-loop comment for the proof).</summary>
    internal static double ScoreForPhase(double[] normalized, double[] weights, double[]? weightSuffix,
        double phaseQuarters, double incumbentScore, CounterAccumulator? acc)
    {
        if (acc is not null) acc.ScoreForPhaseCalls++;
        // Precompute the total weight ONCE in forward order (the same order the
        // original incremental accumulation used, so the final division is
        // bit-identical); the prune bound needs it before the loop ends.
        double totalWeight = 0;
        for (int i = 0; i < weights.Length; i++)
            totalWeight += weights[i];
        // TI-PRUNE: relative safety margin for the prune bound (see below).
        // u = double.Epsilon = 2^-52; 8·n·u generously covers the worst-case
        // floating-point discrepancy between the forward contribution sum and
        // the reverse-built suffix sum (~(2n+6)·u per Higham's summation bound,
        // n = onset count).
        double boundMargin = 8.0 * normalized.Length * double.Epsilon;
        double weightedFit = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            // TI-PRUNE: before onset i, onsets 0..i-1 are already accumulated, so
            // weightSuffix[i] is the total weight of the onsets still to come.
            // Each remaining onset contributes at most weights[j] * 1.0 (the max
            // SubdivisionFit is exactly the quarter's 1.00 weight, achieved at zero
            // deviation), so (weightedFit + weightSuffix[i]) / totalWeight is the
            // tightest attainable bound on the final normalized score. If even that
            // bound cannot exceed the incumbent (Search passes the running per-BPM
            // localBestScore — always &lt;= the global bestScore), the phase can at
            // best TIE it, and the search's update rules are strict '&gt;' with a
            // relative ScoreTieEpsilon margin, so a tying phase NEVER changes any
            // search state (the first-encountered winner is kept). The margin also
            // makes the prune exact in floating point: the true final score is at
            // most boundMargin above this bound, so clearing the incumbent by the
            // margin guarantees the true score cannot trigger ANY update — local or
            // global. An exact tie (bound == incumbent) is therefore NOT pruned and
            // is evaluated like the unpruned reference. '&lt;=' with the margin is
            // the correct choice: pruning on the equality would be harmless, but
            // evaluating it is what the unpruned run does, so this maximizes
            // fidelity while remaining provably argmax-exact.
            if (weightSuffix is not null && totalWeight > 0
                && (weightedFit + weightSuffix[i]) / totalWeight * (1 + boundMargin) <= incumbentScore)
            {
                return 0; // pruned: cannot beat incumbent; 0 never triggers the strict-'>' updates
            }
            double quarter = normalized[i] - phaseQuarters;
            double residual = quarter - Math.Round(quarter);
            if (residual == 0.5) residual = -0.5;
            double bestFit = SubdivisionFit(residual, acc);
            weightedFit += weights[i] * bestFit;
        }
        return totalWeight > 0 ? weightedFit / totalWeight : 0;
    }

    /// <summary>Localized fit of one onset's phase residual against the subdivision
    /// lattice: the highest weighted subdivision the residual lands within a small
    /// tolerance of an integer multiple, else a decaying fit to the quarter grid.</summary>
    private static double SubdivisionFit(double normalizedResidualQuarter)
        => SubdivisionFit(normalizedResidualQuarter, acc: null);

    private static double SubdivisionFit(double normalizedResidualQuarter, CounterAccumulator? acc)
    {
        double best = 0;
        foreach ((double units, double weight) in Subdivisions)
        {
            if (acc is not null) acc.SubdivisionFitEvals++;
            // Is normalizedResidualQuarter a multiple of `units` (within a sliver)?
            double scaled = normalizedResidualQuarter / units;
            double deviation = scaled - Math.Round(scaled);
            double fit = weight * Math.Exp(-deviation * deviation / (2.0 * 0.08 * 0.08));
            if (fit > best) best = fit;
        }
        return best;
    }

    private static long RefinePhase(long[] samples, double[] weights, double spq, double bestPhase, int sampleRate)
        => RefinePhase(samples, weights, spq, bestPhase, sampleRate, acc: null);

    private static long RefinePhase(long[] samples, double[] weights, double spq, double bestPhase, int sampleRate, CounterAccumulator? acc)
    {
        double bestScore = -1;
        long bestSample = (long)bestPhase;
        // TI-HOIST: per-tempo onset normalization, as in Search.
        double[] normalized = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            normalized[i] = samples[i] / spq;
        // Fine phase scan around the coarse winner.
        int fine = 200;
        for (int i = 0; i <= fine; i++)
        {
            double candidate = bestPhase - spq / 2 + spq * i / (double)fine;
            if (candidate < 0)
                candidate = 0;
            double phaseQuarters = candidate / spq;
            // TI-PRUNE: refinement intentionally unpruned (null suffix) — TI-PRUNE
            // scope is the Search phase grid; keeping RefinePhase/ResolveHalfDouble
            // bit-identical guarantees the fixture output is byte-identical.
            double score = ScoreForPhase(normalized, weights, null, phaseQuarters, 0, acc);
            if (score > bestScore * (1 + ScoreTieEpsilon))
            {
                bestScore = score;
                bestSample = (long)Math.Round(candidate);
            }
        }
        _ = sampleRate;
        return Math.Max(0, bestSample);
    }

    /// <summary>
    /// Resolves power-of-two tempo ambiguity within the metrical family of the
    /// winning tempo. Phase alignment alone cannot pick the octave — the same
    /// onset lattice is a valid subdivision at every family tempo — so the octave
    /// is chosen by evidence that is deliberately scale-fair:
    ///   - durations and accents are scored by the octave-invariant DYADIC lattice
    ///     fit (<see cref="DyadicFit"/>), so an octave can never win by turning
    ///     the pulse into a coarser or finer subdivision;
    ///   - a broad musical tempo prior breaks the remaining tie toward the common
    ///     beat band instead of the extremes of the search range.
    /// Returns the winning candidate, its nearest family alternative, and the
    /// combined scores of each — the margin between them feeds confidence.
    /// </summary>
    private static (TempoCandidate resolved, double? alternativeBpm, double resolvedScore, double? altScore)
        ResolveHalfDouble(VisualizationTimeline timeline, double bestBpm, List<TempoCandidate> candidates,
            Onset[] onsets, int sampleRate, CounterAccumulator? acc)
    {
        // The full power-of-two metrical family around the winning tempo, so the
        // exact half/double is always represented even if its raw grid score was
        // muted by the density of the other tempo.
        double[] ratios = { 0.25, 0.5, 1.0, 2.0, 4.0 };
        var scored = new List<(TempoCandidate Candidate, double Score)>();
        foreach (double ratio in ratios)
        {
            double bpm = bestBpm / ratio;
            if (bpm < MinBpm || bpm > MaxBpm)
                continue;
            long[] samples = onsets.Select(o => o.Sample).Distinct().OrderBy(s => s).ToArray();
            double[] weights = onsets.GroupBy(o => o.Sample).Select(g => g.Sum(o => o.Weight)).ToArray();
            double spq = sampleRate * 60.0 / bpm;
            // TI-HOIST: per-tempo onset normalization for the coarse phase scan.
            double[] normalized = new double[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                normalized[i] = samples[i] / spq;
            // Best phase via a coarse scan.
            double bestPhase = -1, bestPhaseScore = -1;
            for (int p = 0; p < PhaseSteps; p++)
            {
                double phaseSamples = spq * p / PhaseSteps;
                double phaseQuarters = phaseSamples / spq;
                // TI-PRUNE: unpruned (null suffix), see RefinePhase comment.
                double score = ScoreForPhase(normalized, weights, null, phaseQuarters, 0, acc);
                if (score > bestPhaseScore * (1 + ScoreTieEpsilon)) { bestPhaseScore = score; bestPhase = phaseSamples; }
            }
            long phaseSample = RefinePhase(samples, weights, spq, bestPhase, sampleRate, acc);
            double durationScore = DurationSubdivisionScore(timeline, bpm, sampleRate);
            double accentScore = AccentFitScore(timeline, bpm, sampleRate);
            double prior = TempoPrior(bpm);
            double combined = 0.55 * bestPhaseScore + 0.20 * durationScore + 0.10 * accentScore + 0.15 * prior;
            scored.Add((new TempoCandidate(bpm, phaseSample, combined,
                IsMetricalFamilyRatio(ratio) && Math.Abs(ratio - 0.5) < 0.01 ? TempoAmbiguity.HalfTempo
                    : IsMetricalFamilyRatio(ratio) && Math.Abs(ratio - 2.0) < 0.01 ? TempoAmbiguity.DoubleTempo
                    : TempoAmbiguity.None), combined));
        }

        TempoCandidate bestCandidate = scored.OrderByDescending(s => s.Score).First().Candidate;
        double bestScore = scored.Max(s => s.Score);
        // Nearest half/double alternative from the pool.
        var alternatives = scored
            .Where(s => Math.Abs(s.Score - bestScore) > 1e-9
                && IsMetricalFamilyRatio(bestCandidate.Bpm / s.Candidate.Bpm))
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
        return (bestCandidate, altBpm, bestScore, altScore);
    }

    private static bool IsMetricalFamilyRatio(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0)
            return false;
        double nearestPowerOfTwo = Math.Pow(2, Math.Round(Math.Log2(ratio)));
        return Math.Abs(ratio - nearestPowerOfTwo) < 0.01;
    }

    /// <summary>
    /// Mean subdivision-aware likelihood that each note length spans an integer
    /// count of some subdivision (Patch D.1), scored by a DYADIC lattice fit
    /// (<see cref="DyadicFit"/>). Doubling the tempo halves <c>quarters</c> and the
    /// <c>k+1</c> grid compensates exactly, so the score is octave-invariant BY
    /// CONSTRUCTION — a tempo can never win here by turning the pulse into a
    /// coarser or finer subdivision. Octave choice is delegated to
    /// <see cref="TempoPrior"/>.
    /// </summary>
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
            score += DyadicFit(duration / spq, sigma: 0.15);
        return score / durations.Length;
    }

    /// <summary>
    /// Octave-invariant dyadic lattice fit (Patch D.4): how close <c>quarters</c>
    /// is to an integer multiple of a power-of-two subdivision (…, 1/16, 1/8,
    /// 1/4, 1/2, 1, 2, … quarters). Measured as <c>quarters · 2^k</c> against the
    /// nearest integer for every <c>k</c>, so doubling the tempo halves
    /// <c>quarters</c> and the <c>k+1</c> grid returns the identical deviation —
    /// the fit is exactly the same at 56, 112 and 224 BPM for the same physical
    /// durations. No finite subdivision set can achieve this (unit 1 doubles to 2,
    /// which is outside every finite set); this formulation has no boundary.
    /// </summary>
    private static double DyadicFit(double quarters, double sigma)
    {
        double best = 0;
        for (int k = 2; k <= 14; k++)
        {
            double scaled = quarters * Math.Pow(2, k);
            double deviation = scaled - Math.Round(scaled);
            if (deviation == 0.5) deviation = -0.5;
            double fit = Math.Exp(-deviation * deviation / (2.0 * sigma * sigma));
            if (fit > best) best = fit;
        }
        return best;
    }

    // ---- Octave disambiguation (Patch D.4) -----------------------------------

    /// <summary>Center and width of the broad tempo prior, in BPM. Intentionally
    /// wide: it only breaks near-ties inside a metrical family, never overrides
    /// strong onset evidence on its own.</summary>
    private const double PriorCenterBpm = 115.0;
    private const double PriorSigmaBpm = 70.0;

    /// <summary>
    /// Broad musical tempo prior (Patch D.4): gently prefers the common beat band
    /// and penalizes the extremes of the [MinBpm, MaxBpm] search range. This is the
    /// final disambiguator among octave-equivalent subdivisions — e.g. the same
    /// ~134 ms unit can be a 32nd at 56 BPM, a 16th at 112 BPM, or an 8th at
    /// 224 BPM, and none of the lattice fits can tell them apart. The prior breaks
    /// that tie toward the central octave without assuming any particular meter.
    /// </summary>
    private static double TempoPrior(double bpm)
    {
        double deviation = (bpm - PriorCenterBpm) / PriorSigmaBpm;
        return Math.Exp(-0.5 * deviation * deviation);
    }

    /// <summary>
    /// Beat-level accent evidence (Patch D.4): rhythm and aggregate hits (the
    /// accented onsets) are scored against the metrical subdivision lattice with
    /// the same octave-invariant <see cref="DyadicFit"/> used for durations, so the
    /// term can never hand the win to a coarser or finer octave. It still
    /// contributes real evidence: a drum pattern that sits off the lattice at one
    /// family tempo but cleanly on it at another shifts the family comparison.
    /// Returns a neutral 0.5 when no accent onsets exist, so a song without
    /// percussion is neither favored nor penalized.
    /// </summary>
    private static double AccentFitScore(VisualizationTimeline timeline, double bpm, int sampleRate)
    {
        var accented = new List<Onset>();
        var seen = new HashSet<long>();
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null || !seen.Add(rhythm.SamplePosition))
                continue;
            accented.Add(new Onset(rhythm.SamplePosition, WeightFor(rhythm.Strength, high: true)));
        }
        foreach (AggregateHitEvent hit in timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
        {
            if (hit is null || !seen.Add(hit.SamplePosition))
                continue;
            accented.Add(new Onset(hit.SamplePosition, 1.0));
        }
        if (accented.Count == 0)
            return 0.5;

        double spq = sampleRate * 60.0 / bpm;
        double weightedFit = 0;
        double totalWeight = 0;
        foreach (Onset onset in accented)
        {
            weightedFit += onset.Weight * DyadicFit(onset.Sample / spq, sigma: 0.08);
            totalWeight += onset.Weight;
        }
        return totalWeight > 0 ? weightedFit / totalWeight : 0.5;
    }

    internal static Onset[] CollectOnsets(VisualizationTimeline timeline)
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

    internal readonly record struct Onset(long Sample, double Weight);

    /// <summary>Mutable accumulator for the opt-in instrumentation counters. Only
    /// touched (and therefore only allocates) on the instrumented
    /// <see cref="SymbolicTempoInference.Build"/> overload. Internal because the
    /// TI-PRUNE test harness calls the instrumented <see cref="ScoreForPhase"/>.</summary>
    internal sealed class CounterAccumulator
    {
        public int OnsetCount;
        public long ScoreForPhaseCalls;
        public long SubdivisionFitEvals;
    }
}
