namespace Fmp.Core.Visualization;

/// <summary>
/// Resolves per-hit DAC sample identities in two stages: exact canonical-hash
/// grouping first, then conservative normalized cross-correlation on
/// duration-compatible candidates that did not collide exactly.
///
/// Design rules from the identity specification:
/// - Hit detection answers only "struck here"; identity is decided here.
/// - Classification constrains which candidates are compared, never decides
///   identity (same-class fuzzy comparison only; exact hashes always merge).
/// - Source addressing is strong evidence, never absolute identity: the same
///   recurring source region relaxes the merge threshold slightly, but a
///   strong waveform match still merges across different source regions.
/// - Ambiguous candidates stay separate (conservative default).
/// - Identities are assigned deterministically in first-use order.
/// </summary>
internal static class DacIdentityResolver
{
    private const double MergeNcc = 0.98;
    private const double MergeNccSameSource = 0.97;
    private const double AmbiguityGap = 0.004;
    private const int MaxBoundaryShift = 6;
    private const int MaxLengthDelta = 16;

    /// <summary>One candidate hit with its payload slice for comparison.</summary>
    internal sealed record Candidate(
        DacHitEvent Hit,
        ReadOnlyMemory<byte> Slice);

    /// <summary>
    /// Resolves identities for <paramref name="candidates"/> in first-use order
    /// and returns the identity id for each candidate (parallel array).
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<Candidate> candidates)
    {
        var identities = new List<string>(candidates.Count);
        var groups = new List<Group>();
        var exactByHash = new Dictionary<DacHash256, int>();

        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate candidate = candidates[i];
            DacCanonicalWaveform.Canonical canonical = DacCanonicalWaveform.Build(candidate.Slice.Span);
            if (canonical.ContentLength == 0)
            {
                identities.Add(NewIdentity(groups));
                continue;
            }

            // Stage 1: exact canonical hash.
            if (exactByHash.TryGetValue(canonical.Hash, out int exactGroup))
            {
                identities.Add(groups[exactGroup].IdentityId);
                groups[exactGroup].Members.Add(i);
                continue;
            }

            // Stage 2: fuzzy against existing groups.
            int? merge = BestFuzzyMatch(groups, candidate, canonical);
            if (merge is int groupIndex)
            {
                identities.Add(groups[groupIndex].IdentityId);
                groups[groupIndex].Members.Add(i);
                continue;
            }

            int newIndex = groups.Count;
            string identityId = NewIdentity(groups);
            groups.Add(new Group(identityId, canonical, candidate));
            exactByHash[canonical.Hash] = newIndex;
            identities.Add(identityId);
        }

        return identities;
    }

    private static string NewIdentity(List<Group> groups)
        => $"dacid:{groups.Count}";

    private static int? BestFuzzyMatch(
        List<Group> groups,
        Candidate candidate,
        DacCanonicalWaveform.Canonical canonical)
    {
        double best = -1;
        int bestIndex = -1;
        double second = -1;
        int secondIndex = -1;

        for (int i = 0; i < groups.Count; i++)
        {
            Group group = groups[i];
            DacCanonicalWaveform.Canonical rep = group.Representative;
            if (rep.ContentLength == 0)
                continue;
            if (Math.Abs(rep.ContentLength - canonical.ContentLength) > MaxLengthDelta)
                continue;
            // Classification constrains comparisons, never defines identity:
            // same-class candidates are compared; different-class candidates
            // are not (an exact hash still merges regardless).
            if (group.FirstHit.Classification != candidate.Hit.Classification)
                continue;

            double score = BestNcc(rep.Normalized, canonical.Normalized, MaxBoundaryShift);
            if (score > best)
            {
                second = best;
                secondIndex = bestIndex;
                best = score;
                bestIndex = i;
            }
            else if (score > second)
            {
                second = score;
                secondIndex = i;
            }
        }

        if (bestIndex < 0)
            return null;

        bool sameSource = candidate.Hit.SourceOffset is long source
            && groups[bestIndex].FirstHit.SourceOffset == source;
        double threshold = sameSource ? MergeNccSameSource : MergeNcc;

        if (best < threshold)
            return null;
        if (best - second < AmbiguityGap && !sameSource)
            return null;

        return bestIndex;
    }

    /// <summary>
    /// Normalized cross-correlation over the overlap of two canonical
    /// waveforms, allowing a few samples of boundary displacement.
    /// </summary>
    private static double BestNcc(float[] a, float[] b, int maxShift)
    {
        double best = -1;
        for (int shift = -maxShift; shift <= maxShift; shift++)
        {
            int startA = Math.Max(0, -shift);
            int startB = Math.Max(0, shift);
            int length = Math.Min(a.Length - startA, b.Length - startB);
            if (length <= 0)
                continue;

            double dot = 0;
            double normA = 0;
            double normB = 0;
            for (int i = 0; i < length; i++)
            {
                double x = a[startA + i];
                double y = b[startB + i];
                dot += x * y;
                normA += x * x;
                normB += y * y;
            }
            if (normA <= 1e-12 || normB <= 1e-12)
                continue;
            double ncc = dot / Math.Sqrt(normA * normB);
            if (ncc > best)
                best = ncc;
        }
        return best;
    }

    private sealed class Group
    {
        public Group(string identityId, DacCanonicalWaveform.Canonical representative, Candidate first)
        {
            IdentityId = identityId;
            Representative = representative;
            FirstHit = first.Hit;
        }

        public string IdentityId { get; }
        public DacCanonicalWaveform.Canonical Representative { get; }
        public DacHitEvent FirstHit { get; }
        public List<int> Members { get; } = [];
    }
}