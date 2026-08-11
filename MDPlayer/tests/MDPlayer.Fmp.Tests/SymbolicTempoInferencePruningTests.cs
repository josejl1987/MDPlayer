#nullable enable

using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TI-PRUNE: exhaustive equivalence of the suffix-sum phase-scoring prune
/// against the unpruned reference. Every phase of a representative corpus of
/// BPM/onset/phase sets is scored both ways and the two runs are compared
/// under the EXACT update rules Search uses (strict '&gt;' with the relative
/// <see cref="SymbolicTempoInference.ScoreTieEpsilon"/> band for the per-BPM
/// best, strict '&gt;' for the global best):
///   (a) any phase the prune fully evaluates must return a BIT-IDENTICAL
///       score to the unpruned reference (the prune only skips phases; it
///       never alters the computation of an evaluated phase);
///   (b) the argmax — per-BPM best phase AND the global winning (BPM, phase)
///       — must be identical between the pruned and unpruned runs.
/// A phase is "pruned" iff the pruned call returns 0 (its sentinel); scores
/// are always strictly positive for a fully evaluated phase because every
/// SubdivisionFit is positive and all weights are positive, so 0 unambiguously
/// marks a skipped phase.
/// </summary>
public sealed class SymbolicTempoInferencePruningTests
{
    private const int Sr = 44_100;
    private const int PhaseSteps = 96;

    /// <summary>Representative BPM corpus spanning the [40, 240] search range.</summary>
    private static readonly double[] RepresentativeBpms =
        { 40, 60, 80, 96, 115, 120, 144, 180, 216, 240 };

    [Fact]
    public void PrunedScoring_IsExactEquivalent_OverRepresentativeCorpus()
    {
        var rng = new Random(20260811);
        int prunedPhases = 0;
        int evaluatedPhases = 0;

        foreach (OnsetSet set in BuildCorpus(rng))
        {
            foreach (double bpm in RepresentativeBpms)
            {
                double spq = Sr * 60.0 / bpm;
                double[] normalized = set.Samples.Select(s => s / spq).ToArray();
                double[] weights = set.Weights;
                double[] suffix = SuffixSums(weights);
                double totalWeight = 0;
                for (int w = 0; w < weights.Length; w++)
                    totalWeight += weights[w];

                // Unpruned reference run: replicate Search's phase loop exactly
                // (local: strict '>' with the tie-epsilon band; global: strict '>').
                double localBestU = -1, localPhaseU = 0, globalBestU = -1, globalPhaseU = 0, globalBpmU = 0;
                for (int p = 0; p < PhaseSteps; p++)
                {
                    double phaseQuarters = p / (double)PhaseSteps;
                    double score = SymbolicTempoInference.ScoreForPhase(
                        normalized, weights, null, phaseQuarters, incumbentScore: 0, totalWeight, acc: null);
                    if (score > localBestU * (1 + SymbolicTempoInference.ScoreTieEpsilon))
                    {
                        localBestU = score;
                        localPhaseU = p;
                    }
                    if (score > globalBestU)
                    {
                        globalBestU = score;
                        globalBpmU = bpm;
                        globalPhaseU = p;
                    }
                }

                // Pruned run: same rules, but ScoreForPhase receives the running
                // per-BPM best as the incumbent and the suffix array as the bound.
                double localBestP = -1, localPhaseP = 0, globalBestP = -1, globalPhaseP = 0, globalBpmP = 0;
                for (int p = 0; p < PhaseSteps; p++)
                {
                    double phaseQuarters = p / (double)PhaseSteps;
                    double score = SymbolicTempoInference.ScoreForPhase(
                        normalized, weights, suffix, phaseQuarters, incumbentScore: localBestP, totalWeight, acc: null);
                    if (score == 0)
                    {
                        // Pruned: never evaluated. Its true score must not be able
                        // to beat the incumbent it was pruned against.
                        double reference = SymbolicTempoInference.ScoreForPhase(
                            normalized, weights, null, phaseQuarters, incumbentScore: 0, totalWeight, acc: null);
                        Assert.True(reference > 0, "fully evaluated scores are strictly positive");
                        Assert.True(reference <= localBestP,
                            $"pruned phase {p} at {bpm} BPM could still beat incumbent ({reference} > {localBestP})");
                        prunedPhases++;
                        continue;
                    }

                    // Fully evaluated: score must be bit-identical to unpruned.
                    // xUnit's 2-arg double overload compares exact equality.
                    evaluatedPhases++;
                    double unpruned = SymbolicTempoInference.ScoreForPhase(
                        normalized, weights, null, phaseQuarters, incumbentScore: 0, totalWeight, acc: null);
                    Assert.Equal(unpruned, score);
                    if (score > localBestP * (1 + SymbolicTempoInference.ScoreTieEpsilon))
                    {
                        localBestP = score;
                        localPhaseP = p;
                    }
                    if (score > globalBestP)
                    {
                        globalBestP = score;
                        globalBpmP = bpm;
                        globalPhaseP = p;
                    }
                }

                // (b) argmax identity: per-BPM best and the global winner.
                Assert.Equal(localBestU, localBestP);
                Assert.Equal(localPhaseU, localPhaseP);
                Assert.Equal(globalBestU, globalBestP);
                Assert.Equal(globalBpmU, globalBpmP);
                Assert.Equal(globalPhaseU, globalPhaseP);
            }
        }

        Assert.True(prunedPhases > 0, "pruning must actually skip phases on this corpus");
        Assert.True(evaluatedPhases > 0, "corpus must exercise fully evaluated phases too");
    }

    // ---- Corpus -----------------------------------------------------------------

    private readonly record struct OnsetSet(long[] Samples, double[] Weights);

    private static List<OnsetSet> BuildCorpus(Random rng) =>
    [
        // Clean quarter/eighth/sixteenth grid at 120 BPM: many exact-score
        // plateaus, so the tie boundary of the prune bound is exercised.
        GridSet(trueBpm: 120, bars: 8, [ (1.0, 1.0), (0.5, 0.8), (0.25, 0.5) ], jitterSamples: 0, rng),
        // Same grid with small jitter: near-ties and distinct scores.
        GridSet(trueBpm: 120, bars: 8, [ (1.0, 1.0), (0.5, 0.8), (0.25, 0.5) ], jitterSamples: 4, rng),
        // Dense sixteenth grid at 96 BPM over 16 bars: ~256 onsets, the largest
        // suffix/prune workload in the corpus.
        GridSet(trueBpm: 96, bars: 16, [ (0.25, 0.9) ], jitterSamples: 3, rng),
        // Eighth-triplet lattice at 144 BPM: off-grid for most of the corpus.
        GridSet(trueBpm: 144, bars: 6, [ (1.0 / 3.0, 0.95) ], jitterSamples: 2, rng),
        // Sparse, musically random onsets: low scores, weak incumbent signal.
        OffGridSet(rng),
    ];

    private static OnsetSet GridSet(double trueBpm, int bars, (double Units, double Weight)[] subdivisions, int jitterSamples, Random rng)
    {
        double spq = Sr * 60.0 / trueBpm;
        var onsets = new List<(long Sample, double Weight)>();
        foreach ((double units, double weight) in subdivisions)
        {
            int perBar = (int)Math.Round(4.0 / units);
            for (int bar = 0; bar < bars; bar++)
            {
                for (int k = 0; k < perBar; k++)
                {
                    long sample = (long)Math.Round((bar * 4 + k * units) * spq);
                    if (jitterSamples > 0)
                        sample += rng.Next(-jitterSamples, jitterSamples + 1);
                    if (sample >= 0)
                        onsets.Add((sample, weight));
                }
            }
        }
        return GroupOnsets(onsets);
    }

    private static OnsetSet OffGridSet(Random rng)
    {
        double spq = Sr * 60.0 / 120.0;
        var onsets = new List<(long Sample, double Weight)>();
        for (int i = 0; i < 12; i++)
        {
            long sample = rng.Next(0, (int)(8 * spq));
            onsets.Add((sample, rng.Next(0, 2) == 0 ? 1.0 : 0.6));
        }
        return GroupOnsets(onsets);
    }

    /// <summary>Replicates Search's onset folding: distinct samples ascending,
    /// weight = sum over onsets sharing the sample.</summary>
    private static OnsetSet GroupOnsets(List<(long Sample, double Weight)> onsets)
    {
        (long Sample, double Weight)[] grouped = onsets
            .GroupBy(o => o.Sample)
            .Select(g => (Sample: g.Key, Weight: g.Sum(o => o.Weight)))
            .OrderBy(t => t.Sample)
            .ToArray();
        return new OnsetSet(
            grouped.Select(t => t.Sample).ToArray(),
            grouped.Select(t => t.Weight).ToArray());
    }

    /// <summary>Replicates Search's suffix-sum construction: weightSuffix[i] is
    /// the sum of weights[j] for j &gt;= i, accumulated from the tail.</summary>
    private static double[] SuffixSums(double[] weights)
    {
        double[] suffix = new double[weights.Length];
        double acc = 0;
        for (int i = weights.Length - 1; i >= 0; i--)
        {
            acc += weights[i];
            suffix[i] = acc;
        }
        return suffix;
    }
}
