#nullable enable

namespace Fmp.Core.Timing;

/// <summary>An absolute sample with its corresponding quarter-note position.</summary>
internal sealed record BeatAnchor(long Sample, double QuarterPosition, double? Confidence = null);

/// <summary>An explicit, validated tempo change at a sample.</summary>
internal sealed record TempoChangePoint(long Sample, double BeatsPerMinute, TimingSource Source, double Confidence = 1.0);

/// <summary>The constant-tempo fit for one region of the piecewise grid.</summary>
internal sealed class SegmentFit
{
    public required long StartSample { get; init; }

    public required long EndSample { get; init; }

    public required double SamplesPerQuarter { get; init; }

    public required double BeatsPerMinute { get; init; }

    public required TimingSource Source { get; set; }

    public required double Confidence { get; init; }

    /// <summary>Quarter position of this segment's start sample (absolute).</summary>
    public required double QuarterAtStart { get; init; }

    /// <summary>Median absolute residual of retained anchors, in quarters.</summary>
    public double MedianResidualQuarters { get; init; }

    public int RejectedAnchorCount { get; init; }
}

/// <summary>The complete beat-grid fit spanning the whole source.</summary>
internal sealed class PiecewiseFit
{
    public required SegmentFit[] Segments { get; init; }

    public required TimingDiagnostics Diagnostics { get; init; }
}

/// <summary>
/// Fits a tempo-and-phase grid to beat anchors and explicit tempo changes using
/// robust constant-tempo regression per region (median slope then least-squares
/// over inliers). Anchors are validated before use; every residual is surfaced so
/// misleading claims of alignment are impossible.
/// </summary>
internal static class BeatGridFitter
{
    /// <summary>
    /// Fits the beat grid. <paramref name="anchors"/> carry absolute quarter
    /// positions (already converted by the caller). An empty anchor list requires
    /// either explicit tempo changes or a fixed BPM to establish a grid.
    /// </summary>
    public static PiecewiseFit Fit(
        IReadOnlyList<BeatAnchor> anchors,
        int sampleRate,
        IReadOnlyList<TempoChangePoint>? tempoChanges = null,
        double? fixedBpm = null,
        double? phaseQuarterAtSampleZero = null,
        bool detectTempoChanges = false)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var diagnostics = new TimingDiagnostics
        {
            AnchorCount = anchors.Count,
            TempoSource = TimingSource.DriverBeatAnchors,
            PhaseSource = TimingSource.DriverBeatAnchors,
        };

        // Validate and clean anchors (§6).
        var clean = ValidateAndClean(anchors, diagnostics);
        diagnostics.RejectedAnchorCount = anchors.Count - clean.Count;

        var segList = new List<SegmentFit>();
        var warningsProvided = tempoChanges is { Count: > 0 };

        if (clean.Count >= 2)
        {
            // Partition anchors by explicit tempo changes; otherwise fit a single
            // segment (or run anchor-based segmentation when requested).
            List<List<BeatAnchor>> groups = Partition(clean, tempoChanges, detectTempoChanges);
            groups.Sort((a, b) => a[0].Sample.CompareTo(b[0].Sample));

            if (warningsProvided && groups.Count == 1)
            {
                // Tempo changes were declared but no segmenting occurred; they are
                // anchors into the grid we cannot honour.
                diagnostics.Warnings.Add("declared tempo changes ignored: insufficient beat anchors to segment");
            }

            // Fit each group; the builder re-anchors start quarters for continuity.
            var fittedGroups = new List<(List<BeatAnchor> inliers, SegmentFit segment)>();
            for (int g = 0; g < groups.Count; g++)
            {
                // An explicit phase override wins over the anchor-derived phase for
                // the first segment; later segments inherit continuity.
                double? groupPhase = g == 0 ? phaseQuarterAtSampleZero : null;
                if (g == 0 && phaseQuarterAtSampleZero is not null)
                    diagnostics.PhaseSource = TimingSource.UserOverride;
                (List<BeatAnchor> inliers, SegmentFit segment) = FitGroup(
                    groups[g], sampleRate, fixedBpm, groupPhase);
                segList.Add(segment);
                fittedGroups.Add((inliers, segment));
            }

            // Phase at sample zero for diagnostics: from the first segment.
            SegmentFit first = segList[0];
            double phase = first.QuarterAtStart - first.StartSample / first.SamplesPerQuarter;
            diagnostics.SampleZeroQuarter = phase;

            double maxResidual = 0;
            double sumSq = 0;
            int count = 0;
            // Residuals are computed only over the INLIERS of each group so a single
            // late/early anchor cannot corrupt the reported quality of the grid.
            foreach ((List<BeatAnchor> inliers, SegmentFit segment) in fittedGroups)
            {
                foreach (BeatAnchor anchor in inliers)
                {
                    double fitted = QuarterAt(anchor.Sample, segment);
                    double residual = fitted - anchor.QuarterPosition;
                    maxResidual = Math.Max(maxResidual, Math.Abs(residual));
                    sumSq += residual * residual;
                    count++;
                }
            }
            diagnostics.MaxResidualQuarters = maxResidual;
            diagnostics.RmsResidualQuarters = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        }
        else if (fixedBpm is > 0)
        {
            // Tempo known, phase may still be estimated from a single anchor or an explicit phase.
            double spq = sampleRate * 60.0 / fixedBpm.Value;
            double phase;
            if (phaseQuarterAtSampleZero is double p)
            {
                phase = p;
                diagnostics.PhaseSource = TimingSource.UserOverride;
            }
            else if (clean.Count == 1)
            {
                phase = clean[0].QuarterPosition - clean[0].Sample / spq;
                diagnostics.PhaseSource = TimingSource.SymbolicInference;
            }
            else
            {
                phase = 0;
                diagnostics.PhaseUnknown = true;
                diagnostics.Warnings.Add("tempo known but beat phase is unknown; grid will be unaligned");
            }
            segList.Add(new SegmentFit
            {
                StartSample = 0,
                EndSample = long.MaxValue,
                SamplesPerQuarter = spq,
                BeatsPerMinute = fixedBpm.Value,
                Source = TimingSource.DriverValidatedTempo,
                Confidence = 1.0,
                QuarterAtStart = phase,
                MedianResidualQuarters = 0,
            });
            diagnostics.SampleZeroQuarter = phase;
        }
        else
        {
            diagnostics.PhaseUnknown = true;
            diagnostics.PhaseSource = TimingSource.SymbolicInference;
            diagnostics.Warnings.Add("no beat anchors, validated tempo, or override available; grid is unknown");
            throw new MusicalTimingException("cannot establish tempo: no beat anchors, validated BPM, or fixed BPM override");
        }

        diagnostics.SegmentCount = segList.Count;
        return new PiecewiseFit
        {
            Segments = segList.ToArray(),
            Diagnostics = diagnostics,
        };
    }

    private static List<BeatAnchor> ValidateAndClean(
        IReadOnlyList<BeatAnchor> anchors,
        TimingDiagnostics diagnostics)
    {
        var result = new List<BeatAnchor>(anchors.Count);
        BeatAnchor? prev = null;
        foreach (BeatAnchor anchor in anchors.OrderBy(a => a.Sample).ThenBy(a => a.QuarterPosition))
        {
            if (!double.IsFinite(anchor.QuarterPosition))
                continue;
            if (prev is not null)
            {
                if (anchor.Sample < prev.Sample)
                {
                    diagnostics.Warnings.Add("decreasing sample position rejected");
                    continue;
                }
                if (anchor.QuarterPosition < prev.QuarterPosition)
                {
                    diagnostics.Warnings.Add("decreasing quarter position rejected");
                    continue;
                }
                if (anchor.Sample == prev.Sample
                    && Math.Abs(anchor.QuarterPosition - prev.QuarterPosition) < 1e-9)
                {
                    // Exact duplicate anchor: deduplicate (§6).
                    continue;
                }
            }
            result.Add(anchor);
            prev = anchor;
        }
        return result;
    }

    private static List<List<BeatAnchor>> Partition(
        List<BeatAnchor> clean,
        IReadOnlyList<TempoChangePoint>? tempoChanges,
        bool detectTempoChanges)
    {
        List<TempoChangePoint> changes = (tempoChanges ?? [])
            .Where(point => point.Sample > 0 && point.BeatsPerMinute > 0)
            .OrderBy(point => point.Sample)
            .DistinctBy(point => point.Sample)
            .ToList();

        // Build boundaries: samples at which a new segment begins.
        var boundaries = new SortedSet<long>();
        foreach (TempoChangePoint change in changes)
            boundaries.Add(change.Sample);

        if (detectTempoChanges && clean.Count >= 3)
            DetectAnchorTempoChanges(clean, boundaries);

        // If no boundaries, single group of all anchors.
        if (boundaries.Count == 0)
            return [new List<BeatAnchor>(clean)];

        var groups = new List<List<BeatAnchor>>(boundaries.Count + 1);
        List<BeatAnchor>? current = null;
        foreach (BeatAnchor anchor in clean)
        {
            if (current is null)
            {
                current = new List<BeatAnchor>();
                groups.Add(current);
            }
            else if (boundaries.Contains(anchor.Sample))
            {
                current = new List<BeatAnchor>();
                groups.Add(current);
            }
            current.Add(anchor);
        }
        // Remove empty groups (a boundary falling before the first anchor).
        groups.RemoveAll(group => group.Count == 0);
        return groups.Count == 0 ? [new List<BeatAnchor>(clean)] : groups;
    }

    /// <summary>
    /// Anchor-only change detection: split where the local beat duration sustains a
    /// change for several beats. Single-interval fluctuations are treated as jitter.
    /// </summary>
    private static void DetectAnchorTempoChanges(List<BeatAnchor> clean, SortedSet<long> boundaries)
    {
        // Local duration per interval (samples per quarter).
        var durations = new List<(long sample, double spq)>();
        for (int index = 1; index < clean.Count; index++)
        {
            double dq = clean[index].QuarterPosition - clean[index - 1].QuarterPosition;
            double ds = clean[index].Sample - clean[index - 1].Sample;
            if (dq > 0 && ds > 0)
                durations.Add((clean[index - 1].Sample, ds / dq));
        }
        if (durations.Count < 5)
            return;

        // Running median of windowed durations finds sustained shifts, not blips.
        int window = Math.Min(5, durations.Count);
        var medians = new double[durations.Count];
        for (int index = 0; index < durations.Count; index++)
        {
            int lo = Math.Max(0, index - window / 2);
            int hi = Math.Min(durations.Count, index + window / 2 + 1);
            double[] slice = durations.Skip(lo).Take(hi - lo).Select(d => d.spq).ToArray();
            Array.Sort(slice);
            medians[index] = slice[slice.Length / 2];
        }
        // A sustained change: the running median shifts more than 20% and holds for
        // several consecutive intervals.
        const double threshold = 0.20;
        const int persist = 3;
        int run = 0;
        for (int index = 1; index < medians.Length; index++)
        {
            bool changed = Math.Abs(medians[index] - medians[index - 1]) / Math.Max(1e-9, medians[index - 1]) > threshold;
            run = changed ? run + 1 : 0;
            if (run >= persist)
                boundaries.Add(durations[index].sample);
        }
    }

    private static (List<BeatAnchor> inliers, SegmentFit segment) FitGroup(
        List<BeatAnchor> group,
        int sampleRate,
        double? fixedBpm,
        double? phaseOverride = null)
    {
        // Robust constant fit: sample = intercept + spq * quarter.
        double spq, intercept;
        if (fixedBpm is > 0)
        {
            spq = sampleRate * 60.0 / fixedBpm.Value;
            intercept = 0;
        }
        else
        {
            (spq, intercept) = RobustLinearFit(group);
        }

        (List<BeatAnchor> inliers, double medianResidual, int rejected) =
            ComputePhaseAndResiduals(group, spq, intercept);

        // Quarter position at the group's start sample.
        double quarterAtStart = (group[0].Sample - intercept) / spq;

        // An explicit phase override (user-provided beat offset) wins for this group.
        if (phaseOverride is double p)
        {
            quarterAtStart = p + group[0].Sample / spq;
        }

        // Continuity of the full map is enforced by the builder, which re-anchors
        // each segment's start quarter from the previous segment's extrapolation.
        double bpm = spq > 0 ? 60.0 * sampleRate / spq : 0;

        SegmentFit segment = new SegmentFit
        {
            StartSample = group[0].Sample,
            EndSample = group[^1].Sample,
            SamplesPerQuarter = spq,
            BeatsPerMinute = bpm,
            Source = TimingSource.DriverBeatAnchors,
            Confidence = ConfidenceFromResiduals(medianResidual),
            QuarterAtStart = quarterAtStart,
            MedianResidualQuarters = medianResidual,
            RejectedAnchorCount = rejected,
        };
        return (inliers, segment);
    }

    /// <summary>Absolute quarter position defined by a segment's slope plus its start quarter.</summary>
    private static double QuarterAt(long sample, SegmentFit segment) =>
        segment.QuarterAtStart + (sample - segment.StartSample) / segment.SamplesPerQuarter;

    /// <summary>
    /// Robustly fits spq and intercept for a group. Median of adjacent slopes gives
    /// a jitter-resistant initial estimate; a least-squares refinement then runs over
    /// anchors whose residual is within a half-beat tolerance, rejecting the outliers
    /// (missing, duplicated, or late callbacks).
    /// </summary>
    private static (double spq, double intercept) RobustLinearFit(List<BeatAnchor> group)
    {
        IEnumerable<double> slopes = group
            .Zip(group.Skip(1))
            .Where(pair => pair.Second.QuarterPosition > pair.First.QuarterPosition)
            .Select(pair => (pair.Second.Sample - pair.First.Sample)
                / (pair.Second.QuarterPosition - pair.First.QuarterPosition))
            .ToArray();
        double spq = Median(slopes);

        // Intercept from median of (sample - spq*quarter).
        double intercept = Median(group.Select(anchor => anchor.Sample - spq * anchor.QuarterPosition));

        // Iterative inlier refinement.
        double tolerance = 0.45 * spq; // half a quarter note or so.
        for (int pass = 0; pass < 3; pass++)
        {
            List<BeatAnchor> inliers = group
                .Where(anchor =>
                {
                    double predicted = intercept + spq * anchor.QuarterPosition;
                    return Math.Abs(anchor.Sample - predicted) <= tolerance;
                })
                .ToList();
            if (inliers.Count < 2)
                break;
            (double newSpq, double newIntercept) = LeastSquares(inliers);
            if (!double.IsFinite(newSpq) || newSpq <= 0)
                break;
            spq = newSpq;
            intercept = newIntercept;
        }
        return (spq, intercept);
    }

    private static (double, double) LeastSquares(List<BeatAnchor> group)
    {
        double n = 0, sumQ = 0, sumQ2 = 0, sumS = 0, sumSq = 0;
        foreach (BeatAnchor anchor in group)
        {
            n += 1;
            double q = anchor.QuarterPosition;
            sumQ += q;
            sumQ2 += q * q;
            sumS += anchor.Sample;
            sumSq += anchor.Sample * q;
        }
        double denom = n * sumQ2 - sumQ * sumQ;
        if (Math.Abs(denom) < 1e-12)
            return (0, 0);
        double spq = (n * sumSq - sumQ * sumS) / denom;
        double intercept = (sumS - spq * sumQ) / n;
        return (spq, intercept);
    }

    private static (List<BeatAnchor> inliers, double medianResidual, int rejected) ComputePhaseAndResiduals(
        List<BeatAnchor> group,
        double spq,
        double intercept)
    {
        // Residual in samples: predicted start = intercept + spq*qStart.
        double quarterAtStart = (group[0].Sample - intercept) / spq;
        _ = quarterAtStart;

        var inliers = new List<BeatAnchor>(group.Count);
        var residuals = new List<double>();
        int rejected = 0;
        foreach (BeatAnchor anchor in group)
        {
            double predictedSample = intercept + spq * anchor.QuarterPosition;
            double residualQuarters = (anchor.Sample - predictedSample) / spq;
            residuals.Add(residualQuarters);
            if (Math.Abs(residualQuarters) > 0.45)
            {
                rejected++;
            }
            else
            {
                inliers.Add(anchor);
            }
        }
        residuals.Sort();
        double medianResidual = residuals.Count > 0
            ? residuals[residuals.Count / 2]
            : 0;
        if (inliers.Count == 0)
            inliers.Add(group[0]);
        return (inliers, medianResidual, rejected);
    }

    private static double ConfidenceFromResiduals(double medianResidual) =>
        Math.Clamp(1.0 - Math.Abs(medianResidual) * 2.0, 0.0, 1.0);

    private static double Median(IEnumerable<double> values)
    {
        double[] arr = values.ToArray();
        if (arr.Length == 0)
            return 0;
        Array.Sort(arr);
        int mid = arr.Length / 2;
        return arr.Length % 2 == 1 ? arr[mid] : (arr[mid - 1] + arr[mid]) / 2.0;
    }
}
