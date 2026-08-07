namespace Fmp.Core.Visualization;

/// <summary>
/// Canonicalizes <see cref="DacSampleCandidate"/>s into deduplicated sample
/// assets (spec §11–§12). Two candidates are the same asset only when their
/// format, payload length, and payload bytes are all identical. A content-hash
/// match alone is never treated as proof of equality.
///
/// Asset IDs and display bank/note assignments are deterministic (§11.2, §17)
/// and independent of dictionary iteration order, object hash codes, or
/// worker completion order.
/// </summary>
internal sealed class DacSampleCatalog
{
    private readonly DacNoteMapper _noteMapper;

    public DacSampleCatalog(DacNoteMapper? noteMapper = null)
    {
        _noteMapper = noteMapper ?? new DacNoteMapper();
    }

    /// <summary>
    /// Builds the canonical asset set and a mapping from candidate ordinal to
    /// the asset id each candidate resolved to. Outcomes are ordered
    /// deterministically and independent of dictionary iteration order.
    /// </summary>
    public DacCatalogResult Build(IReadOnlyList<DacSampleCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Bucket candidates by canonical key (format + length + hash). The key
        // is only a fast path; merging is decided by byte comparison.
        var buckets = new Dictionary<DacSampleKey, List<(int Ordinal, DacSampleCandidate Candidate)>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            DacSampleCandidate candidate = candidates[i];
            if (candidate.Payload.IsEmpty)
                continue;
            DacSampleKey key = DacSampleKey.Create(candidate.Format, candidate.Payload);
            if (!buckets.TryGetValue(key, out List<(int, DacSampleCandidate)>? list))
            {
                list = [];
                buckets.Add(key, list);
            }
            list.Add((i, candidate));
        }

        // Collision-safe asset units: one unit per genuinely identical payload.
        // Two candidates sharing a key but differing by bytes stay separate
        // (protection against hash-collision key errors).
        var units = new List<AssetUnit>();
        foreach (var (key, bucket) in buckets)
        {
            var ordered = bucket
                .OrderBy(entry => entry.Ordinal)
                .ToList();

            // Cluster entries by byte equality (structural, not reference).
            foreach (var cluster in ClusterByPayloadBytes(ordered))
            {
                List<DacSampleCandidate> merged = cluster
                    .OrderBy(entry => entry.Ordinal)
                    .Select(entry => entry.Candidate)
                    .ToList();
                DacSampleCandidate first = merged
                    .OrderBy(c => c.FirstTimestamp)
                    .First();
                units.Add(new AssetUnit(
                    cluster.Min(entry => entry.Ordinal),
                    first.FirstTimestamp,
                    key.ContentHash,
                    first,
                    merged));
            }
        }

        // Deterministic ordering: earliest first-use timestamp, then earliest
        // source ordinal as a tie-breaker, then content hash (spec §11.2).
        List<AssetUnit> orderedUnits = units
            .OrderBy(u => u.MinTimestamp)
            .ThenBy(u => u.MinOrdinal)
            .ThenBy(u => u.Hash)
            .ToList();

        var assets = new List<DacSampleAsset>(orderedUnits.Count);
        int[] assetIdByCandidate = new int[candidates.Count];
        Array.Fill(assetIdByCandidate, -1);

        for (int assetId = 0; assetId < orderedUnits.Count; assetId++)
        {
            AssetUnit unit = orderedUnits[assetId];
            (int bank, int note) = _noteMapper.Map(assetId);

            var sources = unit.Merged
                .Select(c => c.Source)
                .Distinct()
                .ToArray();

            var asset = new DacSampleAsset
            {
                AssetId = assetId,
                StableName = $"DAC S{assetId:000}",
                Format = unit.First.Format,
                Payload = unit.First.Payload,
                ContentHash = unit.Hash,
                FirstUseTimestamp = unit.MinTimestamp,
                FirstUseSequence = unit.MinOrdinal,
                DisplayBank = bank,
                DisplayNote = note,
                TriggerCount = unit.Merged.Count,
                Sources = sources,
            };
            assets.Add(asset);

            foreach (DacSampleCandidate candidate in unit.Merged)
            {
                int ordinal = IndexOf(candidates, candidate);
                if (ordinal >= 0)
                    assetIdByCandidate[ordinal] = assetId;
            }
        }

        return new DacCatalogResult(assets, assetIdByCandidate);
    }

    private static int IndexOf(IReadOnlyList<DacSampleCandidate> list, DacSampleCandidate candidate)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], candidate))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Partitions a key bucket into clusters of structurally identical payload
    /// bytes. GroupBy over <see cref="byte[]"/> compares by reference, so this
    /// helper performs explicit byte-sequence comparison and keeps the result
    /// deterministic regardless of bucket order.
    /// </summary>
    private static IEnumerable<List<(int Ordinal, DacSampleCandidate Candidate)>> ClusterByPayloadBytes(
        IReadOnlyList<(int Ordinal, DacSampleCandidate Candidate)> entries)
    {
        var clusters = new List<List<(int Ordinal, DacSampleCandidate Candidate)>>();
        foreach (var entry in entries)
        {
            bool placed = false;
            foreach (var cluster in clusters)
            {
                if (cluster[0].Candidate.Payload.Span.SequenceEqual(entry.Candidate.Payload.Span))
                {
                    cluster.Add(entry);
                    placed = true;
                    break;
                }
            }
            if (!placed)
                clusters.Add([entry]);
        }
        return clusters;
    }

    private sealed record AssetUnit(
        int MinOrdinal,
        long MinTimestamp,
        DacHash256 Hash,
        DacSampleCandidate First,
        List<DacSampleCandidate> Merged);
}

/// <summary>Outcome of <see cref="DacSampleCatalog.Build"/>.</summary>
internal sealed record DacCatalogResult(
    IReadOnlyList<DacSampleAsset> Assets,
    int[] AssetIdByCandidate)
{
    public int ResolveAssetId(int candidateOrdinal)
        => candidateOrdinal >= 0 && candidateOrdinal < AssetIdByCandidate.Length
            ? AssetIdByCandidate[candidateOrdinal]
            : -1;
}