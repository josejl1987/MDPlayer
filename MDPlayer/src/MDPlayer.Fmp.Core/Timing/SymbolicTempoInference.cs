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
    int UniqueSampleCount,
    long ScoreForPhaseCalls,
    long SubdivisionFitEvals,
    long ScorePhasesPruned,
    long OnsetEvaluationsAvoided);

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
    TempoAmbiguity Ambiguity,
    double PhaseOffsetSamples = double.NaN);

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
    private static readonly double LogEighthWeight = Math.Log(0.96);
    private static readonly double LogTripletEighthWeight = Math.Log(0.92);
    private static readonly double LogSixteenthWeight = Math.Log(0.88);
    private static readonly double LogTripletSixteenthWeight = Math.Log(0.80);
    private static readonly double LogThirtySecondWeight = Math.Log(0.72);
    private static readonly double[] PhaseQuarters = BuildPhaseQuarters();

    private static double[] BuildPhaseQuarters()
    {
        var phases = new double[PhaseSteps];
        for (int index = 0; index < phases.Length; index++)
            phases[index] = index / (double)PhaseSteps;
        return phases;
    }

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

    /// <summary>Default build: no instrumentation, no counter allocation, no
    /// counter increments on the hot path (acc is null throughout).</summary>
    internal static MusicalTimeMapBuildResult Build(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        double? beatOffsetQuarter)
    {
        Onset[] onsets = CollectOnsets(timeline);
        return BuildCore(timeline, options, beatOffsetQuarter, onsets, acc: null);
    }

    /// <summary>
    /// Build overload with opt-in counters (TI-INSTRUMENT). Only callers that opt
    /// in (the benchmark harness / tests via InternalsVisibleTo) allocate a
    /// CounterAccumulator and pay the per-call counter increments; the default
    /// <see cref="Build(VisualizationTimeline,MusicalTimeMapOptions,double?)"/>
    /// path runs with acc = null and performs NO counter work.
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
        counters = new TempoInferenceCounters(
            acc.OnsetCount, acc.UniqueSampleCount,
            acc.ScoreForPhaseCalls, acc.SubdivisionFitEvals,
            acc.ScorePhasesPruned, acc.OnsetEvaluationsAvoided);
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
            Search(onsets, timeline.SampleRate, acc, out long[] samples, out double[] weights);

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
            long[] durations = CollectDurations(timeline);
            Onset[] accents = CollectAccents(timeline);
            var (resolved, alternative, resolvedByScore, altScore) = ResolveHalfDouble(
                bestBpm, candidates, samples, weights,
                timeline.SampleRate, durations, accents,
                bestPhaseSample, bestScore, acc);
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
        CounterAccumulator? acc,
        out long[] samples,
        out double[] weights)
    {
        // CollectOnsets already returns (sample, weight)-ordered onsets. Fold the
        // contiguous sample runs directly instead of materializing Select,
        // Distinct, GroupBy, and OrderBy pipelines for every export.
        int uniqueSampleCount = 0;
        long previousSample = long.MinValue;
        for (int index = 0; index < onsets.Length; index++)
        {
            long sample = onsets[index].Sample;
            if (uniqueSampleCount == 0 || sample != previousSample)
            {
                uniqueSampleCount++;
                previousSample = sample;
            }
        }
        samples = new long[uniqueSampleCount];
        weights = new double[uniqueSampleCount];
        if (acc is not null)
            acc.UniqueSampleCount = uniqueSampleCount;
        int uniqueIndex = -1;
        for (int index = 0; index < onsets.Length; index++)
        {
            Onset onset = onsets[index];
            if (uniqueIndex < 0 || samples[uniqueIndex] != onset.Sample)
            {
                uniqueIndex++;
                samples[uniqueIndex] = onset.Sample;
            }
            weights[uniqueIndex] += onset.Weight;
        }

        double bestScore = -1;
        double bestBpm = 0;
        double bestPhase = 0;

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
        // TI-PRUNE fix: total weight is tempo- and phase-independent; compute it
        // ONCE here and pass it down so ScoreForPhase never re-sums per phase.
        double totalWeight = 0;
        for (int i = 0; i < weights.Length; i++)
            totalWeight += weights[i];

        // Phase scoring depends only on the onset's fractional quarter position.
        // Keep that modulo-one value instead of repeating Math.Round on the full
        // normalized sample for every phase.
        double[] fractionalQuarters = new double[samples.Length];
        for (int b = 0; b <= steps; b++)
        {
            double bpm = MinBpm + b * BpmStep;
            double spq = sampleRate * 60.0 / bpm;
            if (spq <= 0)
                continue;
            // Normalize and reduce each onset once per tempo. The phase loop then
            // only subtracts the phase offset and wraps into [-0.5, 0.5].
            for (int i = 0; i < samples.Length; i++)
            {
                double normalized = samples[i] / spq;
                fractionalQuarters[i] = normalized - Math.Floor(normalized);
            }
            // Sample-weighted phase search over one quarter period.
            for (int p = 0; p < PhaseSteps; p++)
            {
                double phaseSamples = spq * p / PhaseSteps; // phase as sample offset in [0,spq)
                double phaseQuarters = PhaseQuarters[p];
                // TI-GLOBAL-PRUNE: only the global winner is needed during the
                // broad scan. Using the global incumbent makes the suffix bound
                // useful for every later BPM instead of resetting it to zero at
                // each BPM. Exact coarse scores are reconstructed only for the
                // bounded half/double family after the winner is known.
                double score = ScoreForFractionalPhase(
                    fractionalQuarters, weights, weightSuffix, phaseQuarters,
                    bestScore, totalWeight, acc);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBpm = bpm;
                    bestPhase = phaseSamples;
                }
            }
        }

        // ResolveHalfDouble needs the exact coarse winner for at most four
        // neighboring BPMs. Rebuild only that bounded family rather than keeping
        // a score for every BPM searched above.
        var candidates = BuildFamilyCandidates(
            bestBpm, samples, weights, weightSuffix, totalWeight, sampleRate, acc);

        // Improve the phase resolution within the winning tempo directly via onsets.
        double winSpq = sampleRate * 60.0 / bestBpm;
        (long bestPhaseSample, double refinedScore) = RefinePhase(samples, weights, winSpq, bestPhase, sampleRate, acc);

        // Report the REFINED phase's score as the winner's score (previously the
        // coarse-grid score was reported alongside the refined phase — incoherent).
        return (bestBpm, bestPhaseSample, refinedScore, candidates);
    }

    private static List<TempoCandidate> BuildFamilyCandidates(
        double bestBpm,
        long[] samples,
        double[] weights,
        double[] weightSuffix,
        double totalWeight,
        int sampleRate,
        CounterAccumulator? acc)
    {
        double[] ratios = { 0.25, 0.5, 1.0, 2.0, 4.0 };
        var candidates = new List<TempoCandidate>(ratios.Length);
        double[] fractionalQuarters = new double[samples.Length];
        foreach (double ratio in ratios)
        {
            double bpm = bestBpm / ratio;
            if (bpm < MinBpm || bpm > MaxBpm)
                continue;

            double spq = sampleRate * 60.0 / bpm;
            for (int i = 0; i < samples.Length; i++)
            {
                double normalized = samples[i] / spq;
                fractionalQuarters[i] = normalized - Math.Floor(normalized);
            }

            double localBestScore = -1;
            double localBestPhase = 0;
            for (int p = 0; p < PhaseSteps; p++)
            {
                double phaseSamples = spq * p / PhaseSteps;
                double phaseQuarters = PhaseQuarters[p];
                double score = ScoreForFractionalPhase(
                    fractionalQuarters, weights, weightSuffix, phaseQuarters,
                    localBestScore, totalWeight, acc);
                if (score > localBestScore * (1 + ScoreTieEpsilon))
                {
                    localBestScore = score;
                    localBestPhase = phaseSamples;
                }
            }

            candidates.Add(new TempoCandidate(
                bpm, (long)localBestPhase, localBestScore, TempoAmbiguity.None, localBestPhase));
        }

        return candidates;
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
        double phaseQuarters, double incumbentScore, double totalWeight, CounterAccumulator? acc)
    {
        if (acc is null)
        {
            return weightSuffix is null
                ? ScoreForPhaseUnprunedNoCounters(normalized, weights, phaseQuarters, totalWeight)
                : ScoreForPhasePrunedNoCounters(
                    normalized, weights, weightSuffix, phaseQuarters, incumbentScore, totalWeight);
        }

        acc.ScoreForPhaseCalls++;
        if (weightSuffix is null)
            return ScoreForPhaseUnprunedCounted(normalized, weights, phaseQuarters, totalWeight, acc);

        return ScoreForPhasePrunedCounted(
            normalized, weights, weightSuffix, phaseQuarters,
            incumbentScore, totalWeight, acc);
    }

    private static double ScoreForFractionalPhase(
        double[] fractionalQuarters,
        double[] weights,
        double[]? weightSuffix,
        double phaseQuarters,
        double incumbentScore,
        double totalWeight,
        CounterAccumulator? acc)
    {
        if (acc is not null)
            acc.ScoreForPhaseCalls++;
        if (totalWeight <= 0)
            return 0;

        double weightedFit = 0;
        double threshold = weightSuffix is null
            ? double.NegativeInfinity
            : incumbentScore * totalWeight /
                (1 + 8.0 * fractionalQuarters.Length * double.Epsilon);
        int fitEvaluations = 0;
        for (int i = 0; i < fractionalQuarters.Length; i++)
        {
            if (weightSuffix is not null
                && weightedFit + weightSuffix[i] <= threshold)
            {
                if (acc is not null)
                {
                    acc.SubdivisionFitEvals += fitEvaluations;
                    acc.ScorePhasesPruned++;
                    acc.OnsetEvaluationsAvoided += fractionalQuarters.Length - i;
                }
                return 0;
            }

            double residual = FractionalResidual(fractionalQuarters[i], phaseQuarters);
            double bestFit = acc is null
                ? SubdivisionFit(residual)
                : SubdivisionFitCounted(residual, ref fitEvaluations);
            weightedFit += weights[i] * bestFit;
        }

        if (acc is not null)
            acc.SubdivisionFitEvals += fitEvaluations;
        return weightedFit / totalWeight;
    }

    private static double FractionalResidual(double fractionalQuarter, double phaseQuarters)
    {
        double residual = fractionalQuarter - phaseQuarters;
        if (residual < -0.5)
            residual += 1.0;
        else if (residual > 0.5)
            residual -= 1.0;
        return residual == 0.5 ? -0.5 : residual;
    }

    private static double ScoreForPhasePrunedCounted(
        double[] normalized, double[] weights, double[] weightSuffix,
        double phaseQuarters, double incumbentScore, double totalWeight,
        CounterAccumulator acc)
    {
        if (totalWeight <= 0)
            return 0;

        // TI-PRUNE fix: totalWeight is precomputed by the caller (once per Search /
        // refine scan), never re-summed per phase. The prune bound becomes a single
        // threshold computed once per phase: threshold = incumbentScore * totalWeight
        // / (1 + boundMargin). The per-onset check is then a pure add + compare —
        // NO division inside the hot loop (the original per-onset division that the
        // hoist removed is NOT reintroduced here).
        double boundMargin = 8.0 * normalized.Length * double.Epsilon;
        double threshold = incumbentScore * totalWeight / (1 + boundMargin);
        double weightedFit = 0;
        int fitEvaluations = 0;
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
            if (weightedFit + weightSuffix[i] <= threshold)
            {
                acc.SubdivisionFitEvals += fitEvaluations;
                acc.ScorePhasesPruned++;
                acc.OnsetEvaluationsAvoided += normalized.Length - i;
                return 0; // pruned: cannot beat incumbent; 0 never triggers the strict-'>' updates
            }
            double quarter = normalized[i] - phaseQuarters;
            double residual = quarter - Math.Round(quarter);
            if (residual == 0.5) residual = -0.5;
            double bestFit = SubdivisionFitCounted(residual, ref fitEvaluations);
            weightedFit += weights[i] * bestFit;
        }
        acc.SubdivisionFitEvals += fitEvaluations;
        return totalWeight > 0 ? weightedFit / totalWeight : 0;
    }

    private static double ScoreForPhasePrunedNoCounters(
        double[] normalized, double[] weights, double[] weightSuffix,
        double phaseQuarters, double incumbentScore, double totalWeight)
    {
        if (totalWeight <= 0)
            return 0;

        double boundMargin = 8.0 * normalized.Length * double.Epsilon;
        double threshold = incumbentScore * totalWeight / (1 + boundMargin);
        double weightedFit = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (weightedFit + weightSuffix[i] <= threshold)
                return 0;
            double quarter = normalized[i] - phaseQuarters;
            double residual = quarter - Math.Round(quarter);
            if (residual == 0.5) residual = -0.5;
            weightedFit += weights[i] * SubdivisionFit(residual);
        }
        return totalWeight > 0 ? weightedFit / totalWeight : 0;
    }

    private static double ScoreForPhaseUnprunedCounted(
        double[] normalized, double[] weights, double phaseQuarters,
        double totalWeight, CounterAccumulator acc)
    {
        double weightedFit = 0;
        int fitEvaluations = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            double quarter = normalized[i] - phaseQuarters;
            double residual = quarter - Math.Round(quarter);
            if (residual == 0.5) residual = -0.5;
            double bestFit = SubdivisionFitCounted(residual, ref fitEvaluations);
            weightedFit += weights[i] * bestFit;
        }
        acc.SubdivisionFitEvals += fitEvaluations;
        return totalWeight > 0 ? weightedFit / totalWeight : 0;
    }

    private static double ScoreForPhaseUnprunedNoCounters(
        double[] normalized, double[] weights, double phaseQuarters,
        double totalWeight)
    {
        double weightedFit = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            double quarter = normalized[i] - phaseQuarters;
            double residual = quarter - Math.Round(quarter);
            if (residual == 0.5) residual = -0.5;
            double bestFit = SubdivisionFit(residual);
            weightedFit += weights[i] * bestFit;
        }
        return totalWeight > 0 ? weightedFit / totalWeight : 0;
    }

    /// <summary>Localized fit of one onset's phase residual against the subdivision
    /// lattice: the highest weighted subdivision the residual lands within a small
    /// tolerance of an integer multiple, else a decaying fit to the quarter grid.</summary>
    private static double SubdivisionFit(double normalizedResidualQuarter)
    {
        const double denominator = 2.0 * 0.08 * 0.08;
        const double quarterWeight = 1.00;
        const double eighthWeight = 0.96;
        const double tripletEighthWeight = 0.92;
        const double sixteenthWeight = 0.88;
        const double tripletSixteenthWeight = 0.80;
        const double thirtySecondWeight = 0.72;
        const double inverseDenominator = 1.0 / denominator;

        double deviation = normalizedResidualQuarter;
        double bestDeviationSquared = deviation * deviation;
        double bestLog = -bestDeviationSquared * inverseDenominator;
        double bestWeight = quarterWeight;
        if (bestLog >= LogEighthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        double scaled = normalizedResidualQuarter * 2.0;
        deviation = scaled - Math.Round(scaled);
        double deviationSquared = deviation * deviation;
        double fitLog = LogEighthWeight - deviationSquared * inverseDenominator;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = eighthWeight;
        }
        if (bestLog >= LogTripletEighthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 3.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogTripletEighthWeight - deviationSquared * inverseDenominator;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = tripletEighthWeight;
        }
        if (bestLog >= LogSixteenthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 4.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogSixteenthWeight - deviationSquared * inverseDenominator;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = sixteenthWeight;
        }
        if (bestLog >= LogTripletSixteenthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 6.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogTripletSixteenthWeight - deviationSquared * inverseDenominator;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = tripletSixteenthWeight;
        }
        if (bestLog >= LogThirtySecondWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 8.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogThirtySecondWeight - deviationSquared * inverseDenominator;
        if (fitLog > bestLog)
        {
            bestDeviationSquared = deviationSquared;
            bestWeight = thirtySecondWeight;
        }
        return bestWeight * Math.Exp(-bestDeviationSquared / denominator);
    }

    private static double SubdivisionFitCounted(
        double normalizedResidualQuarter, ref int fitEvaluations)
    {
        // Subdivisions is ordered by descending maximum possible fit. Once the
        // current fit reaches the next term's weight, no later term can replace
        // it because exp(x) <= 1. The exits are exact maxima bounds, not an
        // approximation, and preserve the original strict `fit > best` rule.
        const double sigma = 0.08;
        const double denominator = 2.0 * sigma * sigma;
        const double quarterWeight = 1.00;
        const double eighthWeight = 0.96;
        const double tripletEighthWeight = 0.92;
        const double sixteenthWeight = 0.88;
        const double tripletSixteenthWeight = 0.80;
        const double thirtySecondWeight = 0.72;
        const double inverseDenominator = 1.0 / denominator;

        // normalizedResidualQuarter is already the signed residual from the
        // nearest quarter. It is in [-0.5, 0.5], so the first lattice term needs
        // neither a division nor another round.
        double deviation = normalizedResidualQuarter;
        double bestDeviationSquared = deviation * deviation;
        double bestLog = -bestDeviationSquared * inverseDenominator;
        double bestWeight = quarterWeight;
        fitEvaluations++;
        if (bestLog >= LogEighthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        double scaled = normalizedResidualQuarter * 2.0;
        deviation = scaled - Math.Round(scaled);
        double deviationSquared = deviation * deviation;
        double fitLog = LogEighthWeight - deviationSquared * inverseDenominator;
        fitEvaluations++;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = eighthWeight;
        }
        if (bestLog >= LogTripletEighthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 3.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogTripletEighthWeight - deviationSquared * inverseDenominator;
        fitEvaluations++;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = tripletEighthWeight;
        }
        if (bestLog >= LogSixteenthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 4.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogSixteenthWeight - deviationSquared * inverseDenominator;
        fitEvaluations++;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = sixteenthWeight;
        }
        if (bestLog >= LogTripletSixteenthWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 6.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogTripletSixteenthWeight - deviationSquared * inverseDenominator;
        fitEvaluations++;
        if (fitLog > bestLog)
        {
            bestLog = fitLog;
            bestDeviationSquared = deviationSquared;
            bestWeight = tripletSixteenthWeight;
        }
        if (bestLog >= LogThirtySecondWeight)
            return bestWeight * Math.Exp(-bestDeviationSquared / denominator);

        scaled = normalizedResidualQuarter * 8.0;
        deviation = scaled - Math.Round(scaled);
        deviationSquared = deviation * deviation;
        fitLog = LogThirtySecondWeight - deviationSquared * inverseDenominator;
        fitEvaluations++;
        if (fitLog > bestLog)
        {
            bestDeviationSquared = deviationSquared;
            bestWeight = thirtySecondWeight;
        }
        return bestWeight * Math.Exp(-bestDeviationSquared / denominator);
    }

    private static (long PhaseSample, double Score) RefinePhase(long[] samples, double[] weights, double spq, double bestPhase, int sampleRate)
        => RefinePhase(samples, weights, spq, bestPhase, sampleRate, acc: null);

    private static (long PhaseSample, double Score) RefinePhase(long[] samples, double[] weights, double spq, double bestPhase, int sampleRate, CounterAccumulator? acc)
    {
        double bestScore = -1;
        long bestSample = (long)bestPhase;
        // TI-HOIST: per-tempo onset normalization, as in Search.
        double[] normalized = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            normalized[i] = samples[i] / spq;
        // Fine phase scan around the coarse winner.
        int fine = 200;
        double totalWeight = 0;
        for (int j = 0; j < weights.Length; j++)
            totalWeight += weights[j];
        for (int i = 0; i <= fine; i++)
        {
            double candidate = bestPhase - spq / 2 + spq * i / (double)fine;
            if (candidate < 0)
                candidate = 0;
            double phaseQuarters = candidate / spq;
            // TI-PRUNE: refinement intentionally unpruned (null suffix) — TI-PRUNE
            // scope is the Search phase grid; keeping RefinePhase/ResolveHalfDouble
            // bit-identical guarantees the fixture output is byte-identical.
            double score = ScoreForPhase(normalized, weights, null, phaseQuarters, 0, totalWeight, acc);
            if (score > bestScore * (1 + ScoreTieEpsilon))
            {
                bestScore = score;
                bestSample = (long)Math.Round(candidate);
            }
        }
        _ = sampleRate;
        return (Math.Max(0, bestSample), bestScore);
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
        ResolveHalfDouble(double bestBpm, List<TempoCandidate> candidates,
            long[] samples, double[] weights, int sampleRate,
            long[] durations, Onset[] accents,
            long searchPhaseSample, double searchRefinedScore, CounterAccumulator? acc)
    {
        // The full power-of-two metrical family around the winning tempo, so the
        // exact half/double is always represented even if its raw grid score was
        // muted by the density of the other tempo.
        double[] ratios = { 0.25, 0.5, 1.0, 2.0, 4.0 };
        var scored = new List<(TempoCandidate Candidate, double Score)>(ratios.Length);
        foreach (double ratio in ratios)
        {
            double bpm = bestBpm / ratio;
            if (bpm < MinBpm || bpm > MaxBpm)
                continue;
            // The 1.0 family member IS the tempo Search() just scored; reusing its
            // refined phase/score avoids re-running the full coarse+refine scan for
            // the identical BPM. The refined score is the coherent companion of the
            // refined phase (Search now reports it), so the combined score uses it.
            if (Math.Abs(ratio - 1.0) < 1e-12)
            {
                double reuseDurationScore = DurationSubdivisionScore(durations, bpm, sampleRate);
                double reuseAccentScore = AccentFitScore(accents, bpm, sampleRate);
                double reusePrior = TempoPrior(bpm);
                double reuseCombined = 0.55 * searchRefinedScore + 0.20 * reuseDurationScore + 0.10 * reuseAccentScore + 0.15 * reusePrior;
                scored.Add((new TempoCandidate(bpm, searchPhaseSample, reuseCombined, TempoAmbiguity.None), reuseCombined));
                continue;
            }
            double spq = sampleRate * 60.0 / bpm;
            TempoCandidate? coarse = FindCandidate(candidates, bpm);
            double bestPhase;
            double bestPhaseScore;
            if (coarse is not null && double.IsFinite(coarse.PhaseOffsetSamples))
            {
                // Search already evaluated this BPM's complete coarse phase
                // lattice. Reusing its incumbent avoids rescoring the same
                // onset/subdivision pairs during metrical-family resolution.
                bestPhase = coarse.PhaseOffsetSamples;
                bestPhaseScore = coarse.Score;
            }
            else
            {
                // Defensive fallback for callers constructing candidates without
                // the cached phase offset.
                double[] normalized = new double[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                    normalized[i] = samples[i] / spq;
                double totalWeight = 0;
                for (int i = 0; i < weights.Length; i++)
                    totalWeight += weights[i];
                bestPhase = -1;
                bestPhaseScore = -1;
                for (int p = 0; p < PhaseSteps; p++)
                {
                    double phaseSamples = spq * p / PhaseSteps;
                    double phaseQuarters = PhaseQuarters[p];
                    double score = ScoreForPhase(normalized, weights, null, phaseQuarters, 0, totalWeight, acc);
                    if (score > bestPhaseScore * (1 + ScoreTieEpsilon))
                    {
                        bestPhaseScore = score;
                        bestPhase = phaseSamples;
                    }
                }
            }
            (long phaseSample, _) = RefinePhase(samples, weights, spq, bestPhase, sampleRate, acc);
            double durationScore = DurationSubdivisionScore(durations, bpm, sampleRate);
            double accentScore = AccentFitScore(accents, bpm, sampleRate);
            double prior = TempoPrior(bpm);
            double combined = 0.55 * bestPhaseScore + 0.20 * durationScore + 0.10 * accentScore + 0.15 * prior;
            scored.Add((new TempoCandidate(bpm, phaseSample, combined,
                IsMetricalFamilyRatio(ratio) && Math.Abs(ratio - 0.5) < 0.01 ? TempoAmbiguity.HalfTempo
                    : IsMetricalFamilyRatio(ratio) && Math.Abs(ratio - 2.0) < 0.01 ? TempoAmbiguity.DoubleTempo
                    : TempoAmbiguity.None), combined));
        }

        // The family is bounded to five candidates. Select the same stable
        // first-on-tie winners as OrderByDescending/Max without creating an
        // iterator chain and a second alternatives list.
        TempoCandidate bestCandidate = scored[0].Candidate;
        double bestScore = scored[0].Score;
        for (int index = 1; index < scored.Count; index++)
        {
            (TempoCandidate candidate, double score) = scored[index];
            if (score > bestScore)
            {
                bestCandidate = candidate;
                bestScore = score;
            }
        }

        double? altBpm = null;
        double? altScore = null;
        for (int index = 0; index < scored.Count; index++)
        {
            (TempoCandidate candidate, double score) = scored[index];
            if (Math.Abs(score - bestScore) <= 1e-9
                || !IsMetricalFamilyRatio(bestCandidate.Bpm / candidate.Bpm))
                continue;
            if (altScore is null || score > altScore.Value)
            {
                altBpm = candidate.Bpm;
                altScore = score;
            }
        }
        return (bestCandidate, altBpm, bestScore, altScore);
    }

    private static TempoCandidate? FindCandidate(List<TempoCandidate> candidates, double bpm)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            if (Math.Abs(candidates[index].Bpm - bpm) < 1e-9)
                return candidates[index];
        }
        return null;
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
    private static double DurationSubdivisionScore(long[] durations, double bpm, int sampleRate)
    {
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
        for (int index = 0; index < DyadicMultipliers.Length; index++)
        {
            double scaled = quarters * DyadicMultipliers[index];
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
    private static readonly double[] DyadicMultipliers =
        [4.0, 8.0, 16.0, 32.0, 64.0, 128.0, 256.0, 512.0, 1024.0, 2048.0, 4096.0, 8192.0, 16384.0];

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
    private static double AccentFitScore(Onset[] accented, double bpm, int sampleRate)
    {
        if (accented.Length == 0)
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
        Onset[] ordered = list.ToArray();
        Array.Sort(ordered, static (left, right) =>
        {
            int sample = left.Sample.CompareTo(right.Sample);
            return sample != 0 ? sample : left.Weight.CompareTo(right.Weight);
        });
        return ordered;
    }

    private static long[] CollectDurations(VisualizationTimeline timeline)
    {
        var durations = new List<long>();
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note.EndSample > note.StartSample)
                durations.Add(note.EndSample - note.StartSample);
        }
        return durations.ToArray();
    }

    private static Onset[] CollectAccents(VisualizationTimeline timeline)
    {
        var seen = new HashSet<long>();
        var accented = new List<Onset>();
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
        return accented.ToArray();
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
        public int UniqueSampleCount;
        public long ScoreForPhaseCalls;
        public long SubdivisionFitEvals;
        public long ScorePhasesPruned;
        public long OnsetEvaluationsAvoided;
    }
}
