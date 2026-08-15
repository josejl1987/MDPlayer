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

/// <summary>One source-time onset before simultaneous attacks have been folded.</summary>
internal readonly record struct SymbolicOnset(
    long SourceTime,
    string VoiceId,
    double Strength,
    bool IsRhythm = false);

/// <summary>Result of the symbolic tatum/beat hierarchy, kept in source units.</summary>
internal readonly record struct MetricalTiming(
    double TatumDuration,
    long TatumPhaseSample,
    double TatumConfidence,
    int TatumsPerBeat,
    double BeatDuration,
    long BeatPhaseSample,
    double MetricalScore,
    double MetricalConfidence,
    Meter? Meter,
    int? DownbeatPhase,
    long? DownbeatSample,
    double? AlternativeBpm,
    double? AlternativeScore);

internal readonly record struct BeatLevelScore(
    int TatumsPerBeat,
    int Phase,
    double AccentContrast,
    double StrongRecall,
    double Periodicity,
    double Score);

internal readonly record struct TatumLevel(
    double Duration,
    long PhaseSample,
    double Score,
    double DirectSupport,
    double GridSupport);

/// <summary>
/// Batch 3: infers source-time tatum, musical beat level and phase from symbolic
/// onsets (note-ons, rhythm hits) when the driver provides no validated tempo or
/// beat anchors. Tempo is derived only after the tatum/beat hierarchy has been
/// scored; the established quarter-grid search remains a low-confidence fallback
/// and alias diagnostic. Audio inference is explicitly out of scope.
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
        // Keep the established phase scorer as a bounded fallback and compatibility
        // baseline. It no longer chooses the normal symbolic quarter-note level;
        // the hierarchy below decides that from the source onset timeline first.
        double fallbackBpm = 120.0;
        long fallbackPhaseSample = timeline.StartSample;
        double fallbackScore = 0;
        double? fallbackAlternativeBpm = null;
        double? fallbackAlternativeScore = null;
        var searchedCandidates = new List<TempoCandidate>();
        RhythmRoleOnset[] rhythmRoles = CollectRhythmRoles(timeline);
        if (onsets.Length >= 2)
        {
            (double searchedBpm, long searchedPhase, double searchedScore, List<TempoCandidate> candidates) =
                Search(onsets, timeline.SampleRate, acc, out long[] samples, out double[] weights);
            searchedCandidates.AddRange(candidates);
            long[] durations = CollectDurations(timeline);
            Onset[] accents = CollectAccents(timeline);
            (TempoCandidate resolved, double? alternative, double resolvedScore, double? alternativeScore) =
                ResolveHalfDouble(
                    searchedBpm, candidates, samples, weights,
                    timeline.SampleRate, durations, accents,
                    searchedPhase, searchedScore, acc);
            fallbackBpm = resolved.Bpm;
            fallbackPhaseSample = resolved.PhaseSample;
            fallbackScore = resolvedScore;
            fallbackAlternativeBpm = alternative;
            fallbackAlternativeScore = alternativeScore;
        }
        double? preferredRoleBpm = PreferredRoleTempo(rhythmRoles, timeline.SampleRate);
        if (preferredRoleBpm is double roleBpm
            && roleBpm >= MinBpm && roleBpm <= MaxBpm
            && rhythmRoles.Length >= 8
            && rhythmRoles.Select(role => role.Role).Distinct().Count() >= 2)
        {
            fallbackBpm = roleBpm;
            fallbackAlternativeBpm = null;
            fallbackAlternativeScore = null;
            fallbackScore = Math.Max(fallbackScore, 0.75);
        }

        Onset[] hierarchyOnsets = CollectCollapsedOnsets(timeline);
        MetricalTiming hierarchy = InferMetricalTiming(
            timeline, hierarchyOnsets, fallbackBpm, fallbackPhaseSample, fallbackScore,
            fallbackAlternativeBpm, fallbackAlternativeScore);
        double bestBpm = hierarchy.BeatDuration > 0
            ? timeline.SampleRate * 60.0 / hierarchy.BeatDuration
            : fallbackBpm;
        long bestPhaseSample = hierarchy.BeatPhaseSample;
        double bestScore = hierarchy.MetricalScore > 0 ? hierarchy.MetricalScore : fallbackScore;

        var diagnostics = new TimingDiagnostics
        {
            AnchorCount = onsets.Length,
            TempoSource = TimingSource.SymbolicInference,
            PhaseSource = TimingSource.SymbolicInference,
            PhaseSample = bestPhaseSample,
            MaxResidualQuarters = 0,
            RmsResidualQuarters = 0,
            TatumDurationSamples = hierarchy.TatumDuration,
            TatumsPerBeat = hierarchy.TatumsPerBeat,
            BeatDurationSamples = hierarchy.BeatDuration,
            BeatPhaseSample = hierarchy.BeatPhaseSample,
            MetricalScore = hierarchy.MetricalScore,
            MetricalConfidence = hierarchy.MetricalConfidence,
            DownbeatPhase = hierarchy.DownbeatPhase,
        };

        bool roleResolved = fallbackAlternativeBpm is null
            && rhythmRoles.Length >= 8
            && rhythmRoles.Select(role => role.Role).Distinct().Count() >= 2;
        bool hierarchyResolved = hierarchy.MetricalConfidence >= 0.20
            && hierarchy.TatumConfidence >= 0.20;
        bool hierarchyAgreesWithFallback = Math.Abs(bestBpm - fallbackBpm) < 0.50;
        double fallbackRatio = fallbackBpm > 0 ? bestBpm / fallbackBpm : double.NaN;
        bool unresolvedPowerOfTwoAlias = hierarchy.TatumsPerBeat == 2
            && IsMetricalFamilyRatio(fallbackRatio)
            && Math.Abs(fallbackRatio - 1.0) > 0.01
            && hierarchy.MetricalConfidence < 0.50;
        if (roleResolved)
        {
            hierarchyResolved = true;
            hierarchyAgreesWithFallback = true;
            bestBpm = fallbackBpm;
            bestPhaseSample = fallbackPhaseSample;
            double beatDuration = timeline.SampleRate * 60.0 / Math.Max(1.0, bestBpm);
            long downbeat = CanonicalBoundary(
                fallbackPhaseSample, timeline.StartSample, beatDuration);
            hierarchy = hierarchy with
            {
                TatumDuration = beatDuration / 4.0,
                TatumsPerBeat = 4,
                BeatDuration = beatDuration,
                BeatPhaseSample = fallbackPhaseSample,
                Meter = hierarchy.Meter ?? new Meter(4, 4),
                DownbeatSample = hierarchy.DownbeatSample ?? downbeat,
                DownbeatPhase = hierarchy.DownbeatPhase ?? 0,
            };
        }
        if (unresolvedPowerOfTwoAlias)
            hierarchyResolved = false;
        if (hierarchyResolved && hierarchyAgreesWithFallback)
        {
            // The old symbolic scorer uses the full onset set to refine the same
            // IOIs. Keep its value only when it agrees with the hierarchical beat;
            // the hierarchy still supplies the metrical interpretation.
            bestBpm = fallbackBpm;
            bestPhaseSample = fallbackPhaseSample;
            diagnostics.BeatDurationSamples = timeline.SampleRate * 60.0 / bestBpm;
            diagnostics.TatumDurationSamples = diagnostics.BeatDurationSamples
                / Math.Max(1, hierarchy.TatumsPerBeat);
            diagnostics.TatumsPerBeat = hierarchy.TatumsPerBeat;
            diagnostics.BeatPhaseSample = bestPhaseSample;
            if (fallbackScore > 0)
                bestScore = fallbackScore;
        }
        if (!hierarchyResolved)
        {
            // Sparse/metrical-free material keeps the established safe inference
            // result. In particular, a dense but unaccented stream must not be
            // forced to the lowest candidate beat level merely because all levels
            // fit its raw grid equally well.
            bestBpm = fallbackBpm;
            bestPhaseSample = fallbackPhaseSample;
            bestScore = fallbackScore;
            diagnostics.TatumDurationSamples = hierarchy.TatumDuration > 0
                ? hierarchy.TatumDuration
                : timeline.SampleRate * 60.0 / Math.Max(1.0, bestBpm) / 4.0;
            diagnostics.BeatDurationSamples = timeline.SampleRate * 60.0 / Math.Max(1.0, bestBpm);
            diagnostics.BeatPhaseSample = bestPhaseSample;
            diagnostics.TatumsPerBeat = hierarchy.TatumsPerBeat > 0
                ? hierarchy.TatumsPerBeat
                : 4;
            diagnostics.MetricalScore = 0;
        }

        diagnostics.SelectedBpm = bestBpm;
        diagnostics.SelectedScore = bestScore;
        if (hierarchyResolved && !hierarchyAgreesWithFallback)
        {
            diagnostics.AlternativeBpm = hierarchy.AlternativeBpm;
            diagnostics.AlternativeScore = hierarchy.AlternativeScore;
        }
        else
        {
            diagnostics.AlternativeBpm = fallbackAlternativeBpm;
            diagnostics.AlternativeScore = fallbackAlternativeScore;
        }

        diagnostics.TempoSource = TimingSource.SymbolicInference;
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.AnchorCount = onsets.Length;
        diagnostics.PhaseSample = bestPhaseSample;
        diagnostics.PhaseUnknown = onsets.Length < 2;
        diagnostics.TempoConfidence = ConfidenceFromScore(bestScore,
            diagnostics.AlternativeBpm is double a && IsMetricalFamilyRatio(bestBpm / a)
                ? Math.Abs((diagnostics.SelectedScore ?? 0) - (diagnostics.AlternativeScore ?? 0))
                : hierarchyResolved ? hierarchy.MetricalConfidence : 0.0);
        diagnostics.MeterKnown = options.Meter is not null || hierarchy.Meter is not null;
        diagnostics.DownbeatKnown = (options.FirstDownbeatSample is not null && diagnostics.MeterKnown)
            || hierarchy.DownbeatSample is not null;
        diagnostics.Warnings.Add(
            $"symbolic inference: tempo {bestBpm:0.##} BPM, phase sample {bestPhaseSample} " +
            $"(score {bestScore:0.###}, confidence {diagnostics.TempoConfidence:0.###}, " +
            $"tatum {diagnostics.TatumDurationSamples:0.###}, " +
            $"{diagnostics.TatumsPerBeat} tatums/beat); " +
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
        diagnostics.TempoMicrosecondsPerQuarter = segment.MicrosecondsPerQuarter;

        Meter? meter = options.Meter ?? hierarchy.Meter;
        double? firstDownbeatQuarter = null;
        if (options.FirstDownbeatSample is long explicitDownbeat && meter is not null)
        {
            firstDownbeatQuarter = quarterAtStart
                + (explicitDownbeat - timeline.StartSample) / spq;
        }
        else if (hierarchy.DownbeatSample is long inferredDownbeat && meter is not null)
        {
            firstDownbeatQuarter = quarterAtStart
                + (inferredDownbeat - timeline.StartSample) / spq;
        }

        IReadOnlyList<MusicalGridCandidate> gridCandidates =
            BuildGridCandidates(
                searchedCandidates,
                fallbackBpm,
                bestBpm,
                bestPhaseSample,
                bestScore,
                timeline.SampleRate,
                timeline.StartSample,
                options.Meter,
                hierarchy.Meter,
                options.FirstDownbeatSample);
        var map = new MusicalTimeMap(
            timeline.SampleRate,
            timeline.StartSample,
            new[] { segment },
            meter,
            firstDownbeatQuarter,
            confidence: diagnostics.TempoConfidence ?? segment.Confidence,
            alternateBpm: diagnostics.AlternativeBpm,
            isTempoAmbiguous: diagnostics.AlternativeBpm is double,
            gridCandidates: gridCandidates);
        diagnostics.MeterKnown = map.Meter is not null;
        diagnostics.DownbeatKnown = map.FirstDownbeatQuarter is not null;
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.SampleZeroQuarter = map.Segments[0].QuarterPositionAtStart;
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

    private static MetricalTiming InferMetricalTiming(
        VisualizationTimeline timeline,
        Onset[] onsets,
        double fallbackBpm,
        long fallbackPhaseSample,
        double fallbackScore,
        double? fallbackAlternativeBpm,
        double? fallbackAlternativeScore)
    {
        double fallbackBeat = timeline.SampleRate * 60.0 / Math.Max(1.0, fallbackBpm);
        double fallbackTatum = fallbackBeat / 4.0;
        if (onsets.Length < 2)
        {
            return new MetricalTiming(
                fallbackTatum,
                fallbackPhaseSample,
                0,
                4,
                fallbackBeat,
                fallbackPhaseSample,
                0,
                0,
                null,
                null,
                null,
                fallbackAlternativeBpm,
                fallbackAlternativeScore);
        }

        double[] intervals = CollectPositiveIntervals(onsets);
        if (intervals.Length == 0)
        {
            return new MetricalTiming(
                fallbackTatum,
                fallbackPhaseSample,
                0,
                4,
                fallbackBeat,
                fallbackPhaseSample,
                0,
                0,
                null,
                null,
                null,
                fallbackAlternativeBpm,
                fallbackAlternativeScore);
        }

        TatumLevel tatum = InferTatum(intervals, onsets);
        List<BeatLevelScore> levels = ScoreBeatLevels(timeline, onsets, tatum);
        BeatLevelScore rawBest = levels[0];
        // Do not let a bar-period accent alone promote the beat to a double-level
        // grid. When the salience evidence is effectively tied, choose the simpler
        // metrical multiple; materially different 4-vs-6 evidence still wins on its
        // score rather than on this tie-break.
        BeatLevelScore best = levels
            .Where(level => rawBest.Score - level.Score <= 0.025)
            .OrderBy(level => level.TatumsPerBeat)
            .ThenBy(level => level.Phase)
            .First();
        BeatLevelScore? alternative = levels
            .Where(level => level.TatumsPerBeat != best.TatumsPerBeat)
            .OrderByDescending(level => level.Score)
            .ThenBy(level => level.TatumsPerBeat)
            .ThenBy(level => level.Phase)
            .FirstOrDefault();

        double neutralScore = 0.5 * 0.35 + 0.5 * 0.20;
        bool hasAccentVariation = OnsetStrengthSpread(onsets) > 0.10;
        bool hasMetricalEvidence = hasAccentVariation
            && best.Score > neutralScore + 0.025
            && (best.StrongRecall > 0.62 || best.Periodicity > 0.62);

        int selectedTatumsPerBeat = hasMetricalEvidence
            ? best.TatumsPerBeat
            : NearestBeatMultiple(fallbackBeat / Math.Max(1.0, tatum.Duration));
        BeatLevelScore selected = hasMetricalEvidence
            ? best
            : levels.First(level => level.TatumsPerBeat == selectedTatumsPerBeat);
        double beatDuration = tatum.Duration * selectedTatumsPerBeat;
        long beatPhaseSample = CanonicalBoundary(
            tatum.PhaseSample + selected.Phase * tatum.Duration,
            timeline.StartSample,
            beatDuration);

        double tatumMargin = TatumMargin(intervals, tatum);
        double tatumConfidence = Math.Clamp(
            0.60 * tatum.Score + 0.40 * tatumMargin, 0, 1);
        double metricalMargin = alternative is BeatLevelScore alt
            ? Math.Max(0, selected.Score - alt.Score)
            : 0;
        double metricalConfidence = hasMetricalEvidence
            ? Math.Clamp(0.60 * selected.Score + 0.40 * Math.Min(1, metricalMargin * 4), 0, 1)
            : 0.10;

        (Meter? meter, int? downbeatPhase, long? downbeatSample) =
            InferMeter(onsets, tatum, selected, timeline.StartSample);
        double? alternativeBpm = null;
        double? alternativeScore = null;
        if (hasMetricalEvidence && alternative is BeatLevelScore alternativeLevel)
        {
            alternativeBpm = timeline.SampleRate * 60.0 /
                (tatum.Duration * alternativeLevel.TatumsPerBeat);
            alternativeScore = alternativeLevel.Score;
        }

        return new MetricalTiming(
            tatum.Duration,
            tatum.PhaseSample,
            tatumConfidence,
            selectedTatumsPerBeat,
            beatDuration,
            beatPhaseSample,
            selected.Score,
            metricalConfidence,
            meter,
            downbeatPhase,
            downbeatSample,
            alternativeBpm,
            alternativeScore);
    }

    private static TatumLevel InferTatum(double[] intervals, Onset[] onsets)
    {
        var seeds = new List<double>(intervals.Length * 6);
        foreach (double interval in intervals)
        {
            foreach (int divisor in new[] { 1, 2, 3, 4, 6, 8 })
                seeds.Add(interval / divisor);
        }
        seeds.Sort();

        var candidates = new List<double>();
        foreach (double seed in seeds)
        {
            if (!double.IsFinite(seed) || seed < 1)
                continue;
            if (candidates.Count == 0
                || Math.Abs(seed - candidates[^1]) > Math.Max(2.0, seed * 0.02))
            {
                candidates.Add(seed);
            }
            else
            {
                candidates[^1] = (candidates[^1] + seed) / 2.0;
            }
        }

        TatumLevel best = default;
        bool found = false;
        foreach (double candidate in candidates)
        {
            (double score, double direct, double grid) = ScoreTatum(candidate, intervals);
            long phase = FindTatumPhase(candidate, onsets);
            var level = new TatumLevel(candidate, phase, score, direct, grid);
            if (!found
                || level.Score > best.Score + 1e-9
                || (Math.Abs(level.Score - best.Score) <= 1e-9
                    && level.DirectSupport > best.DirectSupport + 1e-9)
                || (Math.Abs(level.Score - best.Score) <= 1e-9
                    && Math.Abs(level.DirectSupport - best.DirectSupport) <= 1e-9
                    && level.Duration < best.Duration))
            {
                best = level;
                found = true;
            }
        }

        return found
            ? best
            : new TatumLevel(intervals[0], FindTatumPhase(intervals[0], onsets), 0, 0, 0);
    }

    private static (double Score, double DirectSupport, double GridSupport) ScoreTatum(
        double candidate,
        double[] intervals)
    {
        if (candidate <= 0 || intervals.Length == 0)
            return (0, 0, 0);

        double direct = 0;
        double grid = 0;
        foreach (double interval in intervals)
        {
            double multiple = Math.Max(1, Math.Round(interval / candidate));
            if (multiple > 32)
                continue;
            double normalizedResidual = Math.Abs(interval - multiple * candidate) / candidate;
            double fit = normalizedResidual <= 0.30
                ? Math.Clamp(1.0 - normalizedResidual / 0.30, 0, 1)
                : 0;
            grid += fit;
            if (multiple == 1 && normalizedResidual <= 0.18)
                direct += 1;
        }

        double directSupport = direct / intervals.Length;
        double gridSupport = grid / intervals.Length;
        // Direct 1x support is deliberately stronger than explaining every IOI as
        // 2x/4x. That prevents an unsupported half-tatum from winning simply
        // because it divides an otherwise perfect grid.
        return (0.60 * directSupport + 0.40 * gridSupport, directSupport, gridSupport);
    }

    private static double[] CollectPositiveIntervals(Onset[] onsets)
    {
        var intervals = new List<double>();
        int limit = Math.Min(onsets.Length, 4096);
        for (int index = 0; index < limit; index++)
        {
            int end = Math.Min(limit, index + 5);
            for (int next = index + 1; next < end; next++)
            {
                long delta = onsets[next].Sample - onsets[index].Sample;
                if (delta > 0)
                    intervals.Add(delta);
            }
        }
        intervals.Sort();
        return intervals.ToArray();
    }

    private static long FindTatumPhase(double duration, Onset[] onsets)
    {
        double bestScore = double.NegativeInfinity;
        double bestPhase = 0;
        foreach (Onset onset in onsets)
        {
            double phase = PositiveModulo(onset.Sample, duration);
            double score = 0;
            foreach (Onset candidate in onsets)
            {
                double residual = CircularDistance(PositiveModulo(candidate.Sample, duration), phase, duration);
                score += candidate.Weight * PhaseFit(residual / duration);
            }
            if (score > bestScore + 1e-9
                || (Math.Abs(score - bestScore) <= 1e-9 && phase < bestPhase))
            {
                bestScore = score;
                bestPhase = phase;
            }
        }
        return (long)Math.Round(bestPhase, MidpointRounding.AwayFromZero);
    }

    private static List<BeatLevelScore> ScoreBeatLevels(
        VisualizationTimeline timeline,
        Onset[] onsets,
        TatumLevel tatum)
    {
        int[] multiples = { 2, 3, 4, 6, 8 };
        Dictionary<int, double> voicePeriodicity = VoicePeriodicityByMultiple(timeline, tatum);
        var result = new List<BeatLevelScore>(multiples.Length);
        foreach (int multiple in multiples)
        {
            double[] strength = BuildTatumStrengths(onsets, tatum, out long firstBin);
            for (int phase = 0; phase < multiple; phase++)
            {
                (double contrast, double recall, double periodicity) = ScoreBeatPhase(
                    strength, firstBin, multiple, phase, voicePeriodicity[multiple]);
                // Accent contrast and direct lag periodicity carry the primary
                // decision in dense symbolic streams. Strong-event recall stays
                // as a guard against half/double levels, but raw attack density
                // must not outweigh a stable beat-period autocorrelation.
                // Dense surface attacks can make every other tatum look strong.
                // Give the direct periodicity term enough weight to distinguish
                // that subdivision from a recurring voice-level pulse; recall
                // remains part of the score and still rejects half/double aliases.
                double score = 0.45 * contrast + 0.15 * recall + 0.40 * periodicity;
                result.Add(new BeatLevelScore(multiple, phase, contrast, recall, periodicity, score));
            }
        }

        return result
            .OrderByDescending(level => level.Score)
            .ThenBy(level => level.TatumsPerBeat)
            .ThenBy(level => level.Phase)
            .ToList();
    }

    private static double[] BuildTatumStrengths(Onset[] onsets, TatumLevel tatum, out long firstBin)
    {
        long minSample = onsets.Min(onset => onset.Sample);
        long maxSample = onsets.Max(onset => onset.Sample);
        firstBin = (long)Math.Floor((minSample - tatum.PhaseSample) / tatum.Duration) - 1;
        long lastBin = (long)Math.Ceiling((maxSample - tatum.PhaseSample) / tatum.Duration) + 1;
        long span = Math.Clamp(lastBin - firstBin + 1, 1, 250_000);
        var strengths = new double[(int)span];
        foreach (Onset onset in onsets)
        {
            long bin = (long)Math.Round(
                (onset.Sample - tatum.PhaseSample) / tatum.Duration,
                MidpointRounding.AwayFromZero);
            int index = (int)Math.Clamp(bin - firstBin, 0, strengths.Length - 1);
            strengths[index] = Math.Min(4.0, strengths[index] + onset.Weight);
        }
        return strengths;
    }

    private static (double Contrast, double Recall, double Periodicity) ScoreBeatPhase(
        double[] strength,
        long firstBin,
        int multiple,
        int phase,
        double voicePeriodicity)
    {
        double beatTotal = 0;
        double offTotal = 0;
        int beatCount = 0;
        int offCount = 0;
        for (int index = 0; index < strength.Length; index++)
        {
            long bin = firstBin + index;
            int position = (int)PositiveModulo(bin - phase, multiple);
            if (position == 0)
            {
                beatTotal += strength[index];
                beatCount++;
            }
            else if (strength[index] > 0)
            {
                offTotal += strength[index];
                offCount++;
            }
        }

        double beatMean = beatCount > 0 ? beatTotal / beatCount : 0;
        double offMean = offCount > 0 ? offTotal / offCount : 0;
        double contrast = beatMean + offMean > 0
            ? Math.Clamp((beatMean - offMean) / (beatMean + offMean), 0, 1)
            : 0;

        double min = strength.Where(value => value > 0).DefaultIfEmpty().Min();
        double max = strength.DefaultIfEmpty().Max();
        double spread = max - min;
        double recall = 0.5;
        if (spread > 0.10)
        {
            double[] positive = strength.Where(value => value > 0).OrderBy(value => value).ToArray();
            double threshold = positive[(int)Math.Clamp(Math.Ceiling(positive.Length * 0.75) - 1, 0, positive.Length - 1)];
            double totalStrong = 0;
            double onBeatStrong = 0;
            for (int index = 0; index < strength.Length; index++)
            {
                if (strength[index] + 1e-9 < threshold)
                    continue;
                totalStrong += strength[index];
                long bin = firstBin + index;
                if (PositiveModulo(bin - phase, multiple) == 0)
                    onBeatStrong += strength[index];
            }
            recall = totalStrong > 0 ? onBeatStrong / totalStrong : 0.5;
        }

        double periodicity = 0.5 * PeriodicityAtLag(strength, multiple)
            + 0.5 * voicePeriodicity;
        return (contrast, recall, periodicity);
    }

    private static Dictionary<int, double> VoicePeriodicityByMultiple(
        VisualizationTimeline timeline,
        TatumLevel tatum)
    {
        var raw = CollectSymbolicOnsets(timeline);
        var binsByVoice = new Dictionary<string, (HashSet<long> Bins, bool IsRhythm)>(StringComparer.Ordinal);
        foreach (SymbolicOnset onset in raw)
        {
            long bin = (long)Math.Round(
                (onset.SourceTime - tatum.PhaseSample) / tatum.Duration,
                MidpointRounding.AwayFromZero);
            if (!binsByVoice.TryGetValue(onset.VoiceId, out (HashSet<long> Bins, bool IsRhythm) value))
                value = (new HashSet<long>(), false);
            value.Bins.Add(bin);
            binsByVoice[onset.VoiceId] = (value.Bins, value.IsRhythm || onset.IsRhythm);
        }

        var result = new Dictionary<int, double>();
        IReadOnlyList<(HashSet<long> Bins, bool IsRhythm)> evidence =
            binsByVoice.Values.Where(value => value.IsRhythm).ToArray();
        if (evidence.Count == 0)
            evidence = binsByVoice.Values.ToArray();
        foreach (int multiple in new[] { 2, 3, 4, 6, 8 })
        {
            double weighted = 0;
            double totalWeight = 0;
            foreach ((HashSet<long> bins, bool isRhythm) in evidence)
            {
                if (bins.Count < 3)
                    continue;
                double voiceWeight = Math.Min(1.0, bins.Count / 8.0)
                    * (isRhythm ? 3.0 : 1.0);
                int repeated = bins.Count(bin => bins.Contains(bin - multiple));
                double support = repeated / (double)bins.Count;
                weighted += voiceWeight * support;
                totalWeight += voiceWeight;
            }
            result[multiple] = totalWeight > 0
                ? weighted / totalWeight
                : 0.5;
        }
        return result;
    }

    private static double PeriodicityAtLag(double[] values, int lag)
    {
        if (lag <= 0 || values.Length <= lag)
            return 0.5;
        double mean = values.Average();
        double covariance = 0;
        double left = 0;
        double right = 0;
        for (int index = 0; index < values.Length - lag; index++)
        {
            double a = values[index] - mean;
            double b = values[index + lag] - mean;
            covariance += a * b;
            left += a * a;
            right += b * b;
        }
        if (left <= 1e-12 || right <= 1e-12)
            return 0.5;
        double correlation = covariance / Math.Sqrt(left * right);
        return Math.Clamp((correlation + 1.0) / 2.0, 0, 1);
    }

    private static double OnsetStrengthSpread(Onset[] onsets)
    {
        if (onsets.Length == 0)
            return 0;
        double min = onsets.Min(onset => onset.Weight);
        double max = onsets.Max(onset => onset.Weight);
        return max - min;
    }

    private static double TatumMargin(double[] intervals, TatumLevel selected)
    {
        (double score, _, _) = ScoreTatum(selected.Duration, intervals);
        // Re-evaluate only the strongest competing direct interval scale. The
        // actual candidate list is intentionally not retained in production
        // diagnostics, so this bounded margin is deterministic and cheap.
        double competitorDuration = selected.Duration * 2.0;
        (double competitor, _, _) = ScoreTatum(competitorDuration, intervals);
        return Math.Clamp(Math.Max(0, score - competitor), 0, 1);
    }

    private static int NearestBeatMultiple(double ratio)
    {
        int[] multiples = { 2, 3, 4, 6, 8 };
        return multiples
            .OrderBy(value => Math.Abs(value - ratio))
            .ThenBy(value => value)
            .First();
    }

    private static (Meter? Meter, int? DownbeatPhase, long? DownbeatSample) InferMeter(
        Onset[] onsets,
        TatumLevel tatum,
        BeatLevelScore beat,
        long sourceStart)
    {
        double[] strength = BuildTatumStrengths(onsets, tatum, out long firstBin);
        var beatStrength = new List<double>();
        int firstBeat = (int)Math.Floor((firstBin - beat.Phase) / (double)beat.TatumsPerBeat);
        int lastBeat = (int)Math.Ceiling(
            (firstBin + strength.Length - 1 - beat.Phase) / (double)beat.TatumsPerBeat);
        for (int index = firstBeat; index <= lastBeat; index++)
        {
            double total = 0;
            for (int tatumIndex = 0; tatumIndex < beat.TatumsPerBeat; tatumIndex++)
            {
                long bin = (long)index * beat.TatumsPerBeat + beat.Phase + tatumIndex;
                int sourceIndex = (int)(bin - firstBin);
                if (sourceIndex >= 0 && sourceIndex < strength.Length)
                    total += strength[sourceIndex];
            }
            beatStrength.Add(total);
        }

        if (beatStrength.Count < 6)
            return (null, null, null);

        (int Numerator, double Score, int Phase) best = (0, 0, 0);
        foreach (int numerator in new[] { 3, 4 })
        {
            for (int phase = 0; phase < numerator; phase++)
            {
                double on = 0;
                double off = 0;
                int onCount = 0;
                int offCount = 0;
                for (int index = 0; index < beatStrength.Count; index++)
                {
                    if (PositiveModulo(firstBeat + index - phase, numerator) == 0)
                    {
                        on += beatStrength[index];
                        onCount++;
                    }
                    else
                    {
                        off += beatStrength[index];
                        offCount++;
                    }
                }
                double contrast = onCount > 0 && offCount > 0
                    ? Math.Clamp((on / onCount - off / offCount) /
                        Math.Max(1e-9, on / onCount + off / offCount), 0, 1)
                    : 0;
                double periodicity = PeriodicityAtLag(beatStrength.ToArray(), numerator);
                double score = 0.65 * contrast + 0.35 * periodicity;
                if (score > best.Score + 1e-9
                    || (Math.Abs(score - best.Score) <= 1e-9 && numerator < best.Numerator))
                    best = (numerator, score, phase);
            }
        }

        if (best.Numerator == 0 || best.Score < 0.60)
            return (null, null, null);

        double beatDuration = tatum.Duration * beat.TatumsPerBeat;
        long downbeat = CanonicalBoundary(
            tatum.PhaseSample + (beat.Phase + best.Phase * beat.TatumsPerBeat) * tatum.Duration,
            sourceStart,
            beatDuration);
        return (new Meter(best.Numerator, 4), best.Phase, downbeat);
    }

    private static long CanonicalBoundary(double boundary, long sourceStart, double period)
    {
        if (period <= 0 || !double.IsFinite(period))
            return sourceStart;
        long result = (long)Math.Round(boundary, MidpointRounding.AwayFromZero);
        long step = Math.Max(1, (long)Math.Round(period, MidpointRounding.AwayFromZero));
        while (result > sourceStart)
            result -= step;
        return result;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static long PositiveModulo(long value, int modulus)
    {
        long result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static double CircularDistance(double left, double right, double period)
    {
        double distance = Math.Abs(left - right);
        return Math.Min(distance, period - distance);
    }

    private static double PhaseFit(double normalizedResidual) =>
        Math.Exp(-normalizedResidual * normalizedResidual / (2.0 * 0.08 * 0.08));

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
                double reuseCombined = 0.55 * searchRefinedScore
                    + 0.20 * reuseDurationScore
                    + 0.10 * reuseAccentScore
                    + 0.15 * reusePrior;
                scored.Add((new TempoCandidate(
                    bpm, searchPhaseSample, reuseCombined, TempoAmbiguity.None), reuseCombined));
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
            double combined = 0.55 * bestPhaseScore
                + 0.20 * durationScore
                + 0.10 * accentScore
                + 0.15 * prior;
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
            if (Math.Abs(score - bestScore) <= 1e-9)
                continue;
            double ratio = candidate.Bpm / bestCandidate.Bpm;
            if (Math.Abs(ratio - 2.0) > 0.01 && Math.Abs(ratio - 0.5) > 0.01)
                continue;
            // Report the strongest conventional half/double alias. Quarter/four-times
            // candidates remain available to the structural second pass.
            if (altScore is null || score > altScore.Value)
            {
                altBpm = candidate.Bpm;
                altScore = score;
            }
        }
        return (bestCandidate, altBpm, bestScore, altScore);
    }

    private static bool RhythmRoleResolutionIsStrong(
        RhythmRoleOnset[] roles,
        TempoCandidate winner,
        List<(TempoCandidate Candidate, double Score)> scored,
        int sampleRate)
    {
        if (roles.Length < 8 || roles.Select(role => role.Role).Distinct().Count() < 2)
            return false;

        double winnerRole = RhythmRoleScore(roles, winner.Bpm, winner.PhaseSample, sampleRate);
        double runnerUp = 0;
        foreach ((TempoCandidate candidate, _) in scored)
        {
            if (Math.Abs(candidate.Bpm - winner.Bpm) < 0.01)
                continue;
            runnerUp = Math.Max(
                runnerUp,
                RhythmRoleScore(roles, candidate.Bpm, candidate.PhaseSample, sampleRate));
        }
        return winnerRole >= 0.50 && winnerRole - runnerUp >= 0.05;
    }

    private static double RhythmRoleScore(
        IReadOnlyList<RhythmRoleOnset> roles,
        double bpm,
        long phaseSample,
        int sampleRate)
    {
        if (roles.Count == 0 || bpm <= 0)
            return 0.5;

        double spq = sampleRate * 60.0 / bpm;
        double weighted = 0;
        double totalWeight = 0;
        foreach (RhythmRole role in Enum.GetValues<RhythmRole>())
        {
            if (role == RhythmRole.Unknown)
                continue;
            RhythmRoleOnset[] events = roles.Where(value => value.Role == role).ToArray();
            if (events.Length == 0)
                continue;

            double eventFit = 0;
            foreach (RhythmRoleOnset rhythm in events)
            {
                double quarter = PositiveModulo((rhythm.Sample - phaseSample) / spq, 4.0);
                double fit = role switch
                {
                    // 4/4 backbeat template. Half/double aliases stay on-grid, so
                    // this is a phase preference, not a tempo discriminator.
                    RhythmRole.Bd => DistanceToSet(quarter, 0, 2, 4),
                    RhythmRole.Sd => DistanceToSet(quarter, 1, 3, 4),
                    RhythmRole.Hh => DistanceToGrid(quarter, 0.25),
                    _ => 0.5,
                };

                eventFit += fit * rhythm.Strength;
            }
            double eventWeight = events.Sum(value => value.Strength);
            eventFit = eventWeight > 0 ? eventFit / eventWeight : 0.5;

            double patternFit = role is RhythmRole.Bd or RhythmRole.Sd
                ? 0.25 * BackbeatCoverage(events, role, phaseSample, spq)
                    + 0.75 * PulseIntervalFit(events, spq)
                : 0.5;
            double roleWeight = role == RhythmRole.Sd ? 2.0
                : role == RhythmRole.Bd ? 1.5
                : 0.35;
            weighted += roleWeight * (0.35 * eventFit + 0.65 * patternFit);
            totalWeight += roleWeight;
        }
        return totalWeight > 0 ? weighted / totalWeight : 0.5;
    }
    private static double PulseIntervalFit(
        IReadOnlyList<RhythmRoleOnset> events,
        double spq)
    {
        if (events.Count < 3 || spq <= 0)
            return 0.5;
        long[] samples = events.Select(value => value.Sample).OrderBy(value => value).ToArray();
        double median = samples
            .Zip(samples.Skip(1), (left, right) => (double)(right - left))
            .OrderBy(value => value)
            .ElementAt(samples.Length / 2 - 1);
        double beatRatio = median / spq;
        return Math.Exp(-(beatRatio - 2.0) * (beatRatio - 2.0) / (2 * 0.45 * 0.45));
    }


    private static double BackbeatCoverage(
        IReadOnlyList<RhythmRoleOnset> events,
        RhythmRole role,
        long phaseSample,
        double spq)
    {
        bool first = false;
        bool second = false;
        foreach (RhythmRoleOnset rhythm in events)
        {
            double quarter = PositiveModulo((rhythm.Sample - phaseSample) / spq, 4.0);
            if (DistanceToSet(quarter, role == RhythmRole.Bd ? 0 : 1,
                    role == RhythmRole.Bd ? 2 : 3, 4) >= 0.9)
            {
                double firstDistance = CircularDistance(
                    quarter, role == RhythmRole.Bd ? 0 : 1, 4);
                if (firstDistance < 0.12)
                    first = true;
                else
                    second = true;
            }
        }
        return (first ? 0.5 : 0) + (second ? 0.5 : 0);
    }
    private static double? PreferredRoleTempo(
        IReadOnlyList<RhythmRoleOnset> roles,
        int sampleRate)
    {
        foreach (RhythmRole role in new[] { RhythmRole.Sd, RhythmRole.Bd })
        {
            long[] samples = roles
                .Where(value => value.Role == role)
                .Select(value => value.Sample)
                .OrderBy(value => value)
                .ToArray();
            if (samples.Length < 3)
                continue;
            double median = samples
                .Zip(samples.Skip(1), (left, right) => (double)(right - left))
                .OrderBy(value => value)
                .ElementAt(samples.Length / 2 - 1);
            if (median > 0)
                return sampleRate * 120.0 / median;
        }
        return null;
    }

    private static double DistanceToSet(double value, double first, double second, double period)
    {
        double distance = Math.Min(
            CircularDistance(value, first, period),
            CircularDistance(value, second, period));
        return Math.Exp(-distance * distance / (2 * 0.12 * 0.12));
    }

    private static double DistanceToGrid(double value, double step)
    {
        double distance = Math.Abs(value / step - Math.Round(value / step));
        return Math.Exp(-distance * distance / (2 * 0.12 * 0.12));
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

    private static Onset[] CollectCollapsedOnsets(VisualizationTimeline timeline)
    {
        SymbolicOnset[] raw = CollectSymbolicOnsets(timeline);
        var folded = new List<Onset>();
        foreach (IGrouping<long, SymbolicOnset> group in raw
            .OrderBy(onset => onset.SourceTime)
            .ThenBy(onset => onset.VoiceId, StringComparer.Ordinal)
            .GroupBy(onset => onset.SourceTime))
        {
            // A chord contributes accent evidence, but repeated records for one
            // source voice do not multiply it. The final strength is capped so a
            // large chord cannot dominate the entire timing analysis.
            double strength = group
                .GroupBy(onset => onset.VoiceId, StringComparer.Ordinal)
                .Select(voice => voice.Max(onset => onset.Strength))
                .Sum();
            folded.Add(new Onset(group.Key, Math.Clamp(strength, 0.1, 4.0)));
        }
        return folded.ToArray();
    }

    private static SymbolicOnset[] CollectSymbolicOnsets(VisualizationTimeline timeline)
    {
        var raw = new List<SymbolicOnset>();
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null)
                continue;
            raw.Add(new SymbolicOnset(
                rhythm.SamplePosition,
                rhythm.ChannelId ?? rhythm.Voice ?? "rhythm",
                MetricalRhythmWeight(rhythm.Strength),
                IsRhythm: true));
        }
        foreach (AggregateHitEvent hit in timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
        {
            if (hit is null)
                continue;
            raw.Add(new SymbolicOnset(hit.SamplePosition, hit.VoiceId ?? "aggregate", 1.0, IsRhythm: true));
        }
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note is null)
                continue;
            if (note.IsRetrigger)
                continue;
            double weight = 0.6; // normal note attack
            raw.Add(new SymbolicOnset(note.StartSample, note.ChannelId ?? "note", weight));
        }
        return raw.ToArray();
    }

    /// <summary>
    /// Small score prior for grid candidates whose meter matches the hierarchy's
    /// PROVISIONAL meter. The provisional meter is onset-hierarchy evidence, not a
    /// user override: it biases ranking but must never prevent evaluating 4/4,
    /// 3/4, or 6/8 candidates. 0.02 is large enough to break near-ties toward the
    /// hierarchy's reading, yet far below any structural-evidence signal.
    /// </summary>
    private const double ProvisionalMeterBonus = 0.02;

    /// <summary>Builds the retained grid-candidate set for the symbolic map.
    /// Internal so tests can verify the provisional-meter and source-aligned
    /// downbeat contracts directly (same pattern as <see cref="ScoreForPhase"/>).</summary>
    internal static IReadOnlyList<MusicalGridCandidate> BuildGridCandidates(
        IReadOnlyList<TempoCandidate> searched,
        double fallbackBpm,
        double requiredBpm,
        long requiredPhaseSample,
        double requiredScore,
        int sampleRate,
        long startSample,
        Meter? explicitMeter,
        Meter? provisionalMeter,
        long? explicitDownbeatSample)
    {
        TempoCandidate fallback = new(
            fallbackBpm,
            searched.Count > 0 ? searched[0].PhaseSample : startSample,
            searched.Count > 0 ? searched[0].Score : 0,
            TempoAmbiguity.None);
        TempoCandidate required = new(
            requiredBpm,
            requiredPhaseSample,
            requiredScore,
            TempoAmbiguity.None);
        var bases = searched
            .Concat(new[] { fallback, required })
            .Where(candidate => candidate.Bpm > 0 && double.IsFinite(candidate.Bpm))
            .GroupBy(candidate => Math.Round(candidate.Bpm, 6))
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .Take(8)
            .ToList();
        if (!bases.Any(candidate => Math.Abs(candidate.Bpm - fallbackBpm) < 1e-6))
            bases.Add(fallback);
        if (!bases.Any(candidate => Math.Abs(candidate.Bpm - requiredBpm) < 1e-6))
            bases.Add(required);

        var expanded = new List<(double Bpm, double QuarterAtSourceStart, double Score)>(
            bases.Count * 5);
        foreach (TempoCandidate source in bases)
        {
            foreach (double ratio in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
            {
                double bpm = source.Bpm * ratio;
                if (bpm is < 20 or > 480)
                    continue;
                double samplesPerQuarter = sampleRate * 60.0 / bpm;
                double quarterAtSourceStart = (startSample - source.PhaseSample)
                    / samplesPerQuarter;
                double score = source.Score
                    * (Math.Abs(ratio - 1.0) < 1e-9 ? 1.0 : 0.92);
                expanded.Add((bpm, quarterAtSourceStart, score));
            }
        }

        var families = expanded
            .GroupBy(candidate => Math.Round(candidate.Bpm, 6))
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.QuarterAtSourceStart)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Bpm)
            .ToArray();
        // Only an explicit user meter override hard-restricts the evaluated
        // meters. A provisional hierarchy meter never restricts — it only adds
        // the small prior bonus below.
        Meter[] meters = explicitMeter is Meter fixedMeter
            ? new[] { fixedMeter }
            : new[] { new Meter(4, 4), new Meter(3, 4), new Meter(6, 8) };
        var result = new List<MusicalGridCandidate>(families.Length * meters.Length * 4);
        foreach ((double bpm, double quarterAtSourceStart, double score) family in families)
        {
            foreach (Meter meter in meters)
            {
                double meterBonus = provisionalMeter is Meter provisional && meter == provisional
                    ? ProvisionalMeterBonus
                    : 0.0;
                if (explicitDownbeatSample is long explicitSample)
                {
                    // Explicit downbeat: convert that exact sample under this
                    // candidate's tempo; generate ONLY that phase — never shifted
                    // or snapped to the meter grid.
                    double samplesPerQuarter = sampleRate * 60.0 / family.bpm;
                    double downbeat = family.quarterAtSourceStart
                        + (explicitSample - startSample) / samplesPerQuarter;
                    result.Add(new MusicalGridCandidate(
                        family.bpm,
                        meter,
                        family.quarterAtSourceStart,
                        downbeat,
                        family.score + meterBonus));
                    continue;
                }

                // Inferred phase: downbeats AT OR BEFORE source start, one per
                // plausible beat phase within the bar.
                double[] offsets = meter switch
                {
                    { Numerator: 4, Denominator: 4 } => new[] { 0.0, 1.0, 2.0, 3.0 },
                    { Numerator: 3, Denominator: 4 } => new[] { 0.0, 1.0, 2.0 },
                    { Numerator: 6, Denominator: 8 } => new[] { 0.0, 1.5 },
                    _ => new[] { 0.0 },
                };
                foreach (double offset in offsets)
                {
                    double downbeat = NormalizeDownbeat(
                        family.quarterAtSourceStart - offset,
                        family.quarterAtSourceStart,
                        meter.QuartersPerBar);
                    result.Add(new MusicalGridCandidate(
                        family.bpm,
                        meter,
                        family.quarterAtSourceStart,
                        downbeat,
                        family.score + meterBonus));
                }
            }
        }

        return result
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => explicitMeter is Meter fixedMeter
                && candidate.Meter == fixedMeter ? 0 : 1)
            .ThenBy(candidate => candidate.Bpm)
            .ThenBy(candidate => candidate.QuarterAtSourceStart)
            .ThenBy(candidate => candidate.FirstDownbeatQuarter)
            .ToArray();
    }

    /// <summary>
    /// Brings a candidate downbeat into (quarterAtStart - QuartersPerBar,
    /// quarterAtStart]: the downbeat is at or before source start but never more
    /// than one bar earlier.
    /// </summary>
    private static double NormalizeDownbeat(
        double downbeat,
        double quarterAtStart,
        double quartersPerBar)
    {
        while (downbeat > quarterAtStart)
            downbeat -= quartersPerBar;
        while (downbeat <= quarterAtStart - quartersPerBar)
            downbeat += quartersPerBar;
        return downbeat;
    }

    internal static Onset[] CollectOnsets(VisualizationTimeline timeline)
    {
        var seen = new HashSet<long>();
        var list = new List<Onset>();
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null)
                continue;
            if (seen.Add(rhythm.SamplePosition))
                list.Add(new Onset(rhythm.SamplePosition, WeightFor(rhythm.Strength, high: true)));
        }
        foreach (AggregateHitEvent hit in timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
        {
            if (hit is null)
                continue;
            if (seen.Add(hit.SamplePosition))
                list.Add(new Onset(hit.SamplePosition, 1.0));
        }
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note is null || seen.Contains(note.StartSample) || note.IsRetrigger)
                continue;
            // Preserve the existing separate-entry polyphony contract. Search's
            // sample folding and the hierarchy's capped folding are distinct steps.
            list.Add(new Onset(note.StartSample, 0.6));
        }
        Onset[] ordered = list.ToArray();
        Array.Sort(ordered, static (left, right) =>
        {
            int sample = left.Sample.CompareTo(right.Sample);
            return sample != 0 ? sample : left.Weight.CompareTo(right.Weight);
        });
        return ordered;
    }

    private static double TypicalNoteDuration(VisualizationTimeline timeline)
    {
        long[] durations = (timeline.Notes ?? Array.Empty<NoteEvent>())
            .Where(note => note is not null && note.EndSample > note.StartSample)
            .Select(note => note.EndSample - note.StartSample)
            .OrderBy(value => value)
            .ToArray();
        if (durations.Length == 0)
            return 0;
        int middle = durations.Length / 2;
        return durations.Length % 2 == 1
            ? durations[middle]
            : (durations[middle - 1] + durations[middle]) / 2.0;
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

    private static RhythmRoleOnset[] CollectRhythmRoles(VisualizationTimeline timeline)
    {
        var result = new List<RhythmRoleOnset>();
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null)
                continue;
            RhythmRole role = RhythmRoleClassifier.Classify(rhythm);
            if (role != RhythmRole.Unknown)
                result.Add(new RhythmRoleOnset(
                    rhythm.SamplePosition,
                    Math.Clamp(0.8 + rhythm.Strength, 0.2, 2.0),
                    role));
        }

        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note is null)
                continue;
            RhythmRole role = RhythmRoleClassifier.Classify(note);
            if (role != RhythmRole.Unknown)
                result.Add(new RhythmRoleOnset(note.StartSample, 1.0, role));
        }
        return result.ToArray();
    }

    private static double WeightFor(float strength, bool high) =>
        high ? Math.Clamp(0.7 + strength * 0.6, 0.1, 1.3) : 0.6;

    // A rhythm attack is an explicit accent signal in the symbolic timeline,
    // whereas a note attack primarily describes surface activity. Keep the
    // distinction local to metrical scoring: the established quarter-grid
    // scorer and the public onset collector retain their existing weights.
    private static double MetricalRhythmWeight(float strength) =>
        Math.Clamp(1.6 + strength * 0.8, 1.2, 2.4);

    private readonly record struct RhythmRoleOnset(
        long Sample,
        double Strength,
        RhythmRole Role);

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
