#nullable enable

namespace Fmp.Core.Timing;

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
        bool detectTempoChanges = false,
        long timelineStartSample = 0)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var diagnostics = new TimingDiagnostics
        {
            AnchorCount = anchors.Count,
            RawAnchorCount = anchors.Count,
            TempoSource = TimingSource.DriverBeatAnchors,
            PhaseSource = TimingSource.DriverBeatAnchors,
        };

        // Validate and clean anchors (§6): dedup exact duplicates, reject non-finite
        // positions, backward motion and, when present, same-sample conflicts.
        List<BeatAnchor> clean = ValidateAndClean(anchors, diagnostics);
        diagnostics.RejectedAnchorCount = anchors.Count - clean.Count;

        List<SegmentFit> segList;

        // Explicit validated driver tempo transitions are the authoritative
        // segmentation source (§17): each becomes a segment boundary at its exact
        // sample, and the region after it takes the validated BPM as its rate.
        List<TempoChangePoint> validatedChanges = (tempoChanges ?? [])
            .Where(point => point.BeatsPerMinute is > 0 && double.IsFinite(point.BeatsPerMinute))
            .OrderBy(point => point.Sample)
            .DistinctBy(point => point.Sample)
            .ToList();
        List<SegmentFit>? validatedSegments = validatedChanges.Count > 0
            ? FitValidatedTempoSegments(clean, sampleRate, fixedBpm, validatedChanges, phaseQuarterAtSampleZero, diagnostics, timelineStartSample)
            : null;

        if (validatedSegments is not null)
        {
            segList = validatedSegments;
            // P2: the no-anchor validated-tempo path knows the tempo but not the beat
            // phase. Surface the phase-unknown warning so the CLI / timing report do
            // not silently proceed with an arbitrary sample-zero phase.
            if (clean.Count == 0 && phaseQuarterAtSampleZero is null)
            {
                diagnostics.PhaseUnknown = true;
                diagnostics.Warnings.Add("tempo known but beat phase is unknown; grid will be unaligned");
            }
        }
        else if (clean.Count >= 2)
        {
            segList = new List<SegmentFit>();
            // No validated transitions: fit a single segment (or anchor-based
            // segmentation when requested). Tempo is recovered purely from anchors.
            List<List<BeatAnchor>> groups = Partition(clean, tempoChanges, detectTempoChanges);
            groups.Sort((a, b) => a[0].Sample.CompareTo(b[0].Sample));

            var fittedGroups = new List<(List<BeatAnchor> inliers, SegmentFit segment)>();
            for (int g = 0; g < groups.Count; g++)
            {
                // An explicit phase override wins over the anchor-derived phase for
                // the first segment; later segments inherit continuity from the builder.
                double? groupPhase = g == 0 ? phaseQuarterAtSampleZero : null;
                if (g == 0 && phaseQuarterAtSampleZero is not null)
                    diagnostics.PhaseSource = TimingSource.UserOverride;
                (List<BeatAnchor> inliers, SegmentFit segment) = FitGroup(
                    groups[g], sampleRate, fixedBpm, groupPhase, diagnostics);
                segList.Add(segment);
                fittedGroups.Add((inliers, segment));
            }

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
            segList = new List<SegmentFit>();
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

        // Surface the fitted phase and estimated tempo for diagnostics.
        SegmentFit firstSegment = segList[0];
        double firstPhase = firstSegment.QuarterAtStart - firstSegment.StartSample / firstSegment.SamplesPerQuarter;
        diagnostics.SampleZeroQuarter = firstPhase;
        diagnostics.EstimatedBpm = segList[0].BeatsPerMinute;
        diagnostics.PhaseAuthoritative = clean.Count >= 1 && !diagnostics.PhaseUnknown
            && diagnostics.PhaseSource is TimingSource.DriverBeatAnchors or TimingSource.UserOverride;
        diagnostics.TempoAuthoritative =
            diagnostics.TempoSource is TimingSource.DriverValidatedTempo or TimingSource.UserOverride
            || segList.Any(segment => segment.Source is TimingSource.DriverValidatedTempo or TimingSource.UserOverride);
        // RejectedAnchorCount is the total number of rejected anchors, matching
        // every entry recorded in RejectedAnchors (validation + per-segment outliers).
        diagnostics.RejectedAnchorCount = diagnostics.RejectedAnchors.Count;

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
            {
                diagnostics.AddRejectedAnchor(anchor, double.NaN, "non-finite quarter position");
                continue;
            }
            if (prev is not null)
            {
                if (anchor.Sample < prev.Sample)
                {
                    diagnostics.Warnings.Add("decreasing sample position rejected");
                    diagnostics.AddRejectedAnchor(anchor, double.NaN, "decreasing sample position");
                    continue;
                }
                if (anchor.Sample == prev.Sample
                    && Math.Abs(anchor.QuarterPosition - prev.QuarterPosition) >= 1e-9)
                {
                    // Same sample, different quarter: a conflicting duplicate. Never
                    // average it; report it and mark the fit conflicted (§13, §56).
                    diagnostics.HasConflictingAnchors = true;
                    diagnostics.Warnings.Add(
                        $"conflicting anchors at sample {anchor.Sample}: beat " +
                        $"{prev.QuarterPosition:0.####} vs {anchor.QuarterPosition:0.####}");
                    diagnostics.AddRejectedAnchor(anchor, anchor.QuarterPosition - prev.QuarterPosition, "conflicting same-sample anchor");
                    continue;
                }
                if (anchor.QuarterPosition < prev.QuarterPosition)
                {
                    diagnostics.Warnings.Add("decreasing quarter position rejected");
                    diagnostics.AddRejectedAnchor(anchor, anchor.QuarterPosition - prev.QuarterPosition, "decreasing quarter position");
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

    /// <summary>
    /// Builds segments from explicit validated driver tempo transitions (§17). Each
    /// distinct validated BPM change is an authoritative boundary: a new segment
    /// begins exactly at the transition sample and the region after it takes the
    /// validated BPM as its rate. Phase is established from the beat anchors (a
    /// median intercept at the validated rate) and never reset — a validated BPM
    /// supplies rate, not phase (§10 "important combination rule").
    ///
    /// Consecutive validated changes that resolve to the SAME emitted µs/qn value are
    /// not given their own segments: sampling/timer jitter around one constant tempo
    /// must not spawn per-change segments (§17, §75).
    /// </summary>
    private static List<SegmentFit> FitValidatedTempoSegments(
        List<BeatAnchor> clean,
        int sampleRate,
        double? fixedBpm,
        List<TempoChangePoint> validatedChanges,
        double? phaseQuarterAtSampleZero,
        TimingDiagnostics diagnostics,
        long timelineStartSample = 0)
    {
        diagnostics.TempoSource = TimingSource.DriverValidatedTempo;
        if (phaseQuarterAtSampleZero is not null)
            diagnostics.PhaseSource = TimingSource.UserOverride;

        // Collapse validated BPM observations that fall within the jitter tolerance of
        // the running representative tempo (§17 sustained-change filtering): sampling
        // / timer jitter around one tempo (e.g. 120.0 vs 120.1 BPM) must not fragment
        // the grid into per-observation segments, even when the rounded µs/qn differs.
        var changes = CollapseJitter(validatedChanges);
        var residuals = new ValidatedResidualTotals();

        long domainStart = clean.Count > 0 ? clean[0].Sample : 0;
        // Clip the segmentation domain to the timeline's actual start so validated
        // tempo changes observed before the exported timeline range do not create a
        // segment whose end precedes the map's start sample (which MusicalTimeMap
        // validation rejects). Changes entirely before the clip point are dropped.
        domainStart = Math.Max(domainStart, timelineStartSample);
        // The validated tempo already in EFFECT at the timeline's start is the last
        // change that occurs at or before domainStart (its value is the active head
        // rate until the first retained change), so when we drop pre-domain changes
        // we still preserve the tempo state, not just the transition list.
        double? headEff = validatedChanges
            .Where(point => point.Sample <= domainStart)
            .OrderByDescending(point => point.Sample)
            .Select(point => (double?)point.BeatsPerMinute)
            .FirstOrDefault();
        changes = changes.Where(point => point.Sample >= domainStart).ToList();

        if (changes.Count == 0)
        {
            // Every validated change was clipped away (all before the timeline's
            // start). Fall back to a single constant-grid segment over the domain
            // at the fixed/supplied BPM so the map is well-formed.
            double fallbackBpm = fixedBpm ?? 120.0;
            if (headEff is not null)
                fallbackBpm = headEff.Value;
            (double spq, double bpm) = RegionRate(clean, sampleRate, fixedBpm, fallbackBpm);
            return new List<SegmentFit>
            {
                MakeValidatedSegment(
                    domainStart, long.MaxValue, spq, bpm, clean,
                    sampleRate, phaseQuarterAtSampleZero, diagnostics, isFirst: true, residuals),
            };
        }

        var segments = new List<SegmentFit>(changes.Count + 1);

        // Initial region: [domainStart, changes[0].Sample) — tempo not yet validated.
        // Emitted only when it actually spans a positive width (a change exactly at
        // the domain start leaves no "before" region to segment).
        long firstChangeSample = changes[0].Sample;
        if (firstChangeSample > domainStart)
        {
            List<BeatAnchor> head = clean.Where(a => a.Sample < firstChangeSample).ToList();
            // The head region's rate is the validated tempo in effect at the timeline
            // start (last pre-domain change), which takes precedence over the fixed
            // grid fallback; pass no fixedBpm so headEff is honored.
            double? headEffectiveBpm = headEff;
            (double headSpq, double headBpm) = RegionRate(head, sampleRate, null, headEffectiveBpm ?? changes[0].BeatsPerMinute);
            segments.Add(MakeValidatedSegment(
                domainStart, firstChangeSample, headSpq, headBpm, head,
                sampleRate, phaseQuarterAtSampleZero, diagnostics, isFirst: true, residuals));
        }
        else
        {
            // The first (and possibly only) change is at the domain start; the segment
            // after it is handled below as the first segment.
        }

        // One segment per distinct change, starting at its sample.
        for (int i = 0; i < changes.Count; i++)
        {
            long start = changes[i].Sample;
            long end = i < changes.Count - 1 ? changes[i + 1].Sample : long.MaxValue;
            if (end < start)
                continue; // impossible ordering guard
            double bpm = changes[i].BeatsPerMinute;
            double spq = sampleRate * 60.0 / bpm;
            List<BeatAnchor> region = clean.Where(a => a.Sample >= start && a.Sample < end).ToList();
            segments.Add(MakeValidatedSegment(
                start, end, spq, bpm, region, sampleRate,
                segments.Count == 0 ? phaseQuarterAtSampleZero : null,
                diagnostics, isFirst: segments.Count == 0, residuals));
        }

        // Report the fitted residuals for the validated-tempo regions so residual
        // reporting and IsTrustworthy reflect actual jitter/outliers (previously RMS
        // stayed 0 on this path regardless of the data).
        if (residuals.Count > 0)
        {
            diagnostics.MaxResidualQuarters = Math.Max(diagnostics.MaxResidualQuarters, residuals.MaxResidual);
            diagnostics.RmsResidualQuarters = Math.Sqrt(residuals.SumSquares / residuals.Count);
            double regionSpq = RegionRate(clean, sampleRate, fixedBpm, 120.0).Item1;
            if (regionSpq > 0 && diagnostics.RmsResidualQuarters > 0)
            {
                diagnostics.RmsResidualSamples = diagnostics.RmsResidualQuarters * regionSpq;
                diagnostics.MaxResidualSamples = diagnostics.MaxResidualQuarters * regionSpq;
            }
        }

        // A lone validated observation — or jitter that collapses to a single tempo —
        // is not a real transition: every segment shares the same rate, so the grid
        // must stay ONE constant segment rather than pre/post fragmentation (§17).
        //
        // A single distinct validated BPM (after jitter collapse) is an OBSERVATION,
        // not evidence of a sustained transition (§10 anchors-first precedence). The
        // uncorroborated-lone decision is structural, driven by the distinct-value
        // count and the anchor evidence — never by segment geometry (segments[0],
        // segments.Count, or a region whose head has too few anchors to establish the
        // rate). When a consistent, clean set of beat anchors establishes a rate and
        // the lone validated value conflicts with it, the anchors win for the whole
        // domain regardless of where the observation sits (sample 0, first-beat
        // boundary, or mid-source) — unless the anchors themselves corroborate the
        // claimed new rate at its sample (a genuine transition).
        bool loneObservation = changes.Count == 1;

        if (loneObservation)
        {
            bool corroboratedByAnchors = AnchorsCorroborateRate(
                clean, changes[0].Sample, changes[0].BeatsPerMinute, sampleRate);

            if (!corroboratedByAnchors)
            {
                // Anchor-derived rate over ALL clean anchor evidence. Pass fixedBpm:null
                // so a fixed-BPM that merely mirrors the lone observation (e.g. auto
                // resolved gridBpm = FirstValidatedBpm = the lone value) cannot defeat
                // the anchors-first precedence check (§10).
                (double anchorSpq, double anchorBpm) = RegionRate(
                    clean, sampleRate, fixedBpm: null, changes[0].BeatsPerMinute);
                bool anchorsEstablishRate =
                    clean.Count >= 2 && double.IsFinite(anchorSpq) && anchorSpq > 0;

                if (anchorsEstablishRate
                    && !WithinTempoTolerance(changes[0].BeatsPerMinute, anchorBpm))
                {
                    // The lone reading conflicts with the anchor-established constant
                    // rate: the anchors are the authoritative source, so rebuild the
                    // whole-domain segment at the anchor-derived rate (§10).
                    SegmentFit anchorFit = MakeValidatedSegment(
                        domainStart, long.MaxValue, anchorSpq, anchorBpm, clean, sampleRate,
                        phaseQuarterAtSampleZero, diagnostics, isFirst: true, residuals: null);
                    // The lone-observation validated fit that produced residual/rejected
                    // state was discarded; clear it so IsTrustworthy/strict reflect only
                    // the anchor-established result (§10 anchors-first).
                    diagnostics.ResetAnchorDiagnostics();
                    return CollapseToSingle(anchorFit);
                }
            }
        }

        SegmentFit? representative = null;
        if (segments.Count > 1)
        {
            bool allSameRate = segments.Skip(1)
                .All(segment => WithinTempoTolerance(segment.BeatsPerMinute, segments[0].BeatsPerMinute));

            if (allSameRate)
                representative = segments[0];
        }
        else if (loneObservation)
        {
            // segments.Count == 1: the lone change sat at the domain start (or no
            // anchors exist to corroborate a split), so the single emitted segment
            // carries the observation's own BPM across the whole domain. With no
            // anchor-established rate to override it, that BPM stands; the caller
            // surfaces the phase-unknown warning when no anchors exist.
            representative = segments[0];
        }

        if (representative is not null)
            return CollapseToSingle(representative);

        return segments;
    }

    /// <summary>Collapses a representative valid segment to a single whole-domain segment.</summary>
    private static List<SegmentFit> CollapseToSingle(SegmentFit representative) =>
        new()
        {
            new SegmentFit
            {
                StartSample = representative.StartSample,
                EndSample = long.MaxValue,
                SamplesPerQuarter = representative.SamplesPerQuarter,
                BeatsPerMinute = representative.BeatsPerMinute,
                Source = TimingSource.DriverValidatedTempo,
                Confidence = representative.Confidence,
                QuarterAtStart = representative.QuarterAtStart,
                MedianResidualQuarters = representative.MedianResidualQuarters,
                RejectedAnchorCount = representative.RejectedAnchorCount,
            },
        };

    /// <summary>
    /// True when the anchors at/after <paramref name="transitionSample"/> fit a
    /// constant rate within jitter tolerance of <paramref name="bpm"/> — i.e. they
    /// corroborate a single validated observation as a real transition rather than a
    /// lone conflicting reading (§10). Fewer than two anchors in the region cannot
    /// corroborate it.
    /// </summary>
    private static bool AnchorsCorroborateRate(
        List<BeatAnchor> clean,
        long transitionSample,
        double bpm,
        int sampleRate)
    {
        List<BeatAnchor> region = clean.Where(a => a.Sample >= transitionSample).ToList();
        if (region.Count < 2)
            return false;
        (double spq, _) = RobustLinearFit(region);
        if (spq <= 0 || !double.IsFinite(spq))
            return false;
        double regionBpm = 60.0 * sampleRate / spq;
        return WithinTempoTolerance(regionBpm, bpm);
    }

    private static (double spq, double bpm) RegionRate(
        List<BeatAnchor> regionAnchors,
        int sampleRate,
        double? fixedBpm,
        double fallbackBpm)
    {
        if (fixedBpm is > 0)
            return (sampleRate * 60.0 / fixedBpm.Value, fixedBpm.Value);
        if (regionAnchors.Count >= 2)
        {
            (double spq, _) = RobustLinearFit(regionAnchors);
            if (spq > 0 && double.IsFinite(spq))
                return (spq, 60.0 * sampleRate / spq);
        }
        return (sampleRate * 60.0 / fallbackBpm, fallbackBpm);
    }

    /// <summary>Fractional tolerance for treating two validated BPM values as one sustained tempo (e.g. 120.0 vs 120.1).</summary>
    private const double TempoJitterTolerance = 0.02; // 2% — sampling/timer jitter is well under this

    /// <summary>
    /// Sustained-change filtering (§17): walks the validated BPM observations in
    /// sample order and keeps only those that move away from the running
    /// representative tempo by more than <see cref="TempoJitterTolerance"/>. Near-
    /// identical observations around one tempo collapse into a single change (and
    /// therefore a single segment) instead of fragmenting into per-observation
    /// segments. A change that carries the running tempo is never preceded by a
    /// redundant transition at its own sample.
    /// </summary>
    private static List<TempoChangePoint> CollapseJitter(List<TempoChangePoint> validatedChanges)
    {
        var changes = new List<TempoChangePoint>(validatedChanges.Count);
        foreach (TempoChangePoint change in validatedChanges)
        {
            if (changes.Count == 0)
            {
                changes.Add(change);
                continue;
            }
            TempoChangePoint previous = changes[^1];
            if (!WithinTempoTolerance(change.BeatsPerMinute, previous.BeatsPerMinute))
                changes.Add(change);
        }
        return changes;
    }

    /// <summary>True when two BPM values are close enough to be the same sustained tempo.</summary>
    private static bool WithinTempoTolerance(double bpm, double reference)
    {
        if (reference <= 0 || !double.IsFinite(reference) || !double.IsFinite(bpm) || bpm <= 0)
            return false;
        return Math.Abs(bpm - reference) / reference <= TempoJitterTolerance;
    }

    /// <summary>Running total of residual squares and the running max, in quarters, across validated-tempo anchors.</summary>
    private sealed class ValidatedResidualTotals
    {
        public double SumSquares { get; private set; }

        public double MaxResidual { get; private set; }

        public int Count { get; private set; }

        public void Add(double residualQuarters)
        {
            double abs = Math.Abs(residualQuarters);
            SumSquares += residualQuarters * residualQuarters;
            if (abs > MaxResidual)
                MaxResidual = abs;
            Count++;
        }
    }

    /// <summary>
    /// Builds one constant-tempo segment. Rate comes from <paramref name="spq"/>;
    /// phase (quarter at start) comes from the region's anchors (median intercept)
    /// so the validated BPM never destroys anchor-established phase (§10). Anchors
    /// that fall outside the half-beat residual tolerance are rejected and
    /// recorded with a reason.
    /// </summary>
    private static SegmentFit MakeValidatedSegment(
        long startSample,
        long endSample,
        double spq,
        double bpm,
        List<BeatAnchor> regionAnchors,
        int sampleRate,
        double? phaseOverride,
        TimingDiagnostics diagnostics,
        bool isFirst,
        ValidatedResidualTotals? residuals = null)
    {
        double intercept = 0;
        double confidence = 1.0;
        int rejected = 0;

        if (regionAnchors.Count > 0)
        {
            // Phase from anchors at the validated rate: median(sample - spq*quarter).
            intercept = Median(regionAnchors.Select(a => a.Sample - spq * a.QuarterPosition));
            double tolerance = 0.45 * spq;
            var segmentResiduals = new List<double>();
            foreach (BeatAnchor anchor in regionAnchors)
            {
                double predicted = intercept + spq * anchor.QuarterPosition;
                double residual = (anchor.Sample - predicted) / spq;
                segmentResiduals.Add(residual);
                residuals?.Add(residual);
                if (Math.Abs(residual) > 0.45)
                {
                    rejected++;
                    diagnostics.AddRejectedAnchor(anchor, residual, "outlier vs validated tempo");
                }
            }
            segmentResiduals.Sort();
            double medianResidual = segmentResiduals.Count > 0 ? segmentResiduals[segmentResiduals.Count / 2] : 0;
            confidence = ConfidenceFromResiduals(medianResidual);
        }

        double quarterAtStart = (startSample - intercept) / spq;
        if (isFirst && phaseOverride is double p)
            quarterAtStart = p + startSample / spq;

        return new SegmentFit
        {
            StartSample = startSample,
            EndSample = endSample,
            SamplesPerQuarter = spq,
            BeatsPerMinute = bpm,
            Source = TimingSource.DriverValidatedTempo,
            Confidence = confidence,
            QuarterAtStart = quarterAtStart,
            MedianResidualQuarters = 0,
            RejectedAnchorCount = rejected,
        };
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
        double? phaseOverride = null,
        TimingDiagnostics? diagnostics = null)
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
            ComputePhaseAndResiduals(group, spq, intercept, diagnostics);

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
        double intercept,
        TimingDiagnostics? diagnostics = null)
    {
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
                diagnostics?.AddRejectedAnchor(anchor, residualQuarters, "outlier vs fitted grid");
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
