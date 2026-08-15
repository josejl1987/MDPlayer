#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Derives coarse musical form — bars → labeled sections → repeated-loop bounds — from a
/// decoded timeline and its established <see cref="MusicalTimeMap"/>. Consumes only
/// note-ons and the meter/quarter grid, so every decoded chip family shares this stage.
/// </summary>
internal static class MusicalStructureAnalyzer
{
    private const double SimilarityThreshold = 0.85;
    private const int MaxRepeatPeriodBars = 64;
    private const double RestartBoundaryErrorLimit = 0.125;
    private const double SingleRestartSimilarityThreshold = 0.88;
    private const double SingleRestartMaterialCoverage = 0.50;
    private const double RestartBoundarySigmaBars = 0.06;
    private const double RestartBoundaryErrorLimitDownbeat = 0.06;
    private const double DownbeatRestartSimilarity = 0.90;
    // Spec P0-6 rejection gates for detected repeated blocks, and the
    // fundamental-period preference tolerance (periods scoring within this gap,
    // one a multiple of the other, prefer the shorter/fundamental period).
    private const double RepeatedMaterialCoverageThreshold = 0.40;
    private const double RepeatedSpanCoverageThreshold = 0.25;
    private const double FundamentalPeriodTolerance = 0.02;
    // Hypothesis 4 (calibration): content-only loop fallback thresholds. When
    // the grid fails to resolve, only NEAR-PERFECT repeated content earns a
    // promoted loop (similarity >= 0.99, span coverage >= 0.50) — anything
    // weaker stays invisible rather than fabricating a loop.
    private const double ContentFallbackSimilarity = 0.99;
    private const double ContentFallbackSpanCoverage = 0.50;
    // Patch 8C: accent-aware downbeat discriminator. Sustained note starts are
    // counted per beat class relative to the candidate downbeat; the beat-0
    // share through r/(1+r) is the accent fit. The signal is a PHASE-ONLY
    // discriminator: it never enters the combined score, so tempo/meter margins
    // (and every other song's resolution state) are untouched.
    private const double AccentDownbeatWeight = 0.30;
    private const double AccentDownbeatMarginMin = 0.03;
    private const double AccentSustainedQuarterSpan = 0.5;
    private const int AccentMinimumSustainedStarts = 8;
    // Patch 8D (calibration Exp 7): bass-downbeat focus discriminator.
    // Low-register (MIDI <= 55) onsets are counted per beat class relative to
    // the candidate's OWN downbeat; the beat-0 share through r/(1+r) is the
    // bass fit. Like the accent fit it is a PHASE-ONLY signal: it never enters
    // the stored combined score. Gate-side it (a) re-ranks phases within the
    // winner family to gate the downbeat, and (b) re-ranks the family list to
    // CONFIRM the tempo margin — the winner's bass-adjusted score minus the
    // best bass-adjusted rival family. The adjusted score blends the bass fit
    // with the combined score in proportion to each candidate's own total
    // signal weight, so a thin-evidence score (e.g. percussion-free, restart-
    // free tracks whose total weight is 0.55) lets bass carry more weight.
    // max() semantics mean bass can only strengthen resolution, never weaken
    // or re-decide it.
    private const double BassDownbeatWeight = 0.12;
    private const double BassDownbeatMarginMin = 0.03;
    private const int BassMaximumMidiNote = 55;
    private const int BassMinimumOnsets = 6;

    private readonly record struct RhythmRoleEvidence(
        double Fit,
        bool Strong,
        int KnownRoleHits,
        int TotalHits,
        int DistinctRoles);

    private readonly record struct RepeatedContentEvidence(
        double Fit,
        double MaterialCoverage,
        double SpanCoverage,
        RepeatedBlock? Block);

    private readonly record struct GridScore(
        double Score,
        double? SourcePeriodFit,
        double? RepeatedContentFit,
        double? RestartBoundaryFit,
        RhythmRoleEvidence RhythmRole,
        double? OnsetFit,
        double? PhraseFit,
        double? SpanCoverage,
        double? MaterialCoverage,
        RepeatedBlock? BestBlock = null,
        RestartBoundaryEvidence? RestartEvidence = null,
        double TotalSignalWeight = 0);

    private readonly record struct ScoredGrid(
        MusicalGridCandidate Candidate,
        GridScore Score);

    /// <summary>
    /// Scores retained timing grids against source-loop and repeated-content evidence.
    /// Only symbolic maps are eligible; authoritative driver/user timing is unchanged.
    /// <paramref name="percussionEvidence"/> is the SAME unified evidence instance
    /// that tempo inference consumed (spec §3/§7, D3); grid scoring derives its
    /// percussion signals from it, never re-reading the timeline.
    /// </summary>
    internal static MusicalTimeMapBuildResult SelectGrid(
        MusicalTimeMapBuildResult initial,
        VisualizationTimeline timeline,
        IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(percussionEvidence);
        MusicalTimeMap map = initial.Map;
        if (map.Segments.Count != 1 || map.GridCandidates.Count == 0
            || map.Segments[0].Source != TimingSource.SymbolicInference)
            return initial;

        bool hasMaterial = (timeline.Notes ?? Array.Empty<NoteEvent>()).Any(note => note is not null)
            || (timeline.Rhythm ?? Array.Empty<RhythmEvent>()).Any(hit => hit is not null)
            || (timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
                .Any(hit => hit is not null);
        if (!hasMaterial)
            return initial;

        SourceLoopEvidence sourceEvidence = CaptureSourceLoopEvidence(timeline);

        // DISCRIMINATING RESTART SIGNAL: pre-compute every candidate's
        // restart-boundary fit and take the minimum across candidates that share
        // the evidence as the baseline. The signal later contributes only
        // fit - baseline, so candidates aligned equally to the same restart
        // boundary add exactly 0 to their score — shared restart evidence never
        // compresses the tempo/meter margin (Patch 2 behavior restored).
        // Hypothesis 8: both the baseline and the signal require >= 2 phase
        // candidates of the same (bpm, meter) family to carry restart evidence —
        // a single coincidental phase alignment is phase luck, not tempo
        // evidence, and must not dominate the ranking.
        (double restartBaseline, Dictionary<(double Bpm, Meter Meter), int> restartFamilySupport) =
            ComputeRestartSignalPlan(map, timeline, sourceEvidence, percussionEvidence);

        ScoredGrid[] ranked = map.GridCandidates
            .Select(candidate =>
            {
                MusicalTimeMap variant = VariantMap(map, candidate);
                return new ScoredGrid(
                    candidate,
                    GridStructuralScore(candidate, variant, timeline, sourceEvidence, restartBaseline, restartFamilySupport, percussionEvidence));
            })
            .Where(value => double.IsFinite(value.Score.Score))
            .OrderByDescending(value => value.Score.Score)
            .ToArray();
        if (ranked.Length == 0)
            return initial;

        // FAMILY RANKING: retain only the best phase candidate per distinct
        // (rounded BPM + meter); the winner is the best family, and the family
        // list still drives the tempo-ambiguity reporting below.
        ScoredGrid[] rankedFamilies = ranked
            .GroupBy(value => (Bpm: Math.Round(value.Candidate.Bpm, 6), value.Candidate.Meter))
            .Select(group => group.OrderByDescending(value => value.Score.Score).First())
            .OrderByDescending(value => value.Score.Score)
            .ToArray();
        ScoredGrid winner = rankedFamilies[0];

        // PATCH 8B: per-dimension margins (candidate level, identical to the
        // receipts). The single joint tempo/meter margin is gone: a clear tempo
        // may resolve while the meter stays unknown, and vice versa.
        double tempoMargin = MarginTo(winner, ranked, other =>
            Math.Round(other.Candidate.Bpm, 6) != Math.Round(winner.Candidate.Bpm, 6))
            ?? double.PositiveInfinity;
        double meterMargin = MarginTo(winner, ranked, other =>
            other.Candidate.Meter != winner.Candidate.Meter)
            ?? double.PositiveInfinity;
        double downbeatMargin = MarginTo(winner, ranked, other =>
            Math.Round(other.Candidate.Bpm, 6) == Math.Round(winner.Candidate.Bpm, 6)
            && other.Candidate.Meter == winner.Candidate.Meter
            && other.Candidate.FirstDownbeatQuarter != winner.Candidate.FirstDownbeatQuarter)
            ?? double.PositiveInfinity;

        // Patch 8C: accent-aware phase discrimination, family-internal and
        // phase-only. Within the winner family (same bpm + meter) the combined
        // score is re-ranked with the sustained-start accent fit
        // (DownbeatAccentFit); the accent margin gates the downbeat ONLY.
        // The combined score — and with it every tempo/meter/downbeat margin —
        // is untouched. A family with no accent evidence (null fit, e.g. fast
        // percussion-only tracks) keeps its combined-score phase behavior
        // exactly; the accent margin requires at least two family phases with
        // accent evidence, so a lone candidate never self-resolves.
        double? accentDownbeatMargin = null;
        double? accentResolvedDownbeat = null;
        {
            double accentBest = double.NegativeInfinity;
            double accentSecond = double.NegativeInfinity;
            double? accentPhase = null;
            foreach (ScoredGrid familyMember in ranked)
            {
                if (Math.Round(familyMember.Candidate.Bpm, 6) != Math.Round(winner.Candidate.Bpm, 6)
                    || familyMember.Candidate.Meter != winner.Candidate.Meter)
                {
                    continue;
                }
                MusicalTimeMap variant = VariantMap(map, familyMember.Candidate);
                if (variant.Meter is not { } meter
                    || variant.FirstDownbeatQuarter is not double downbeatQ)
                {
                    continue;
                }
                double? fit = DownbeatAccentFit(
                    variant, timeline, downbeatQ, meter.QuartersPerBar);
                if (fit is not double f)
                    continue;
                double adjusted =
                    (familyMember.Score.Score + AccentDownbeatWeight * f) / (1.0 + AccentDownbeatWeight);
                if (adjusted > accentBest)
                {
                    accentSecond = accentBest;
                    accentBest = adjusted;
                    accentPhase = familyMember.Candidate.FirstDownbeatQuarter;
                }
                else if (adjusted > accentSecond)
                {
                    accentSecond = adjusted;
                }
            }
            if (accentPhase is not null
                && double.IsFinite(accentSecond)
                && accentBest - accentSecond >= AccentDownbeatMarginMin)
            {
                accentDownbeatMargin = accentBest - accentSecond;
                accentResolvedDownbeat = accentPhase;
            }
        }

        // Patch 8D: bass-downbeat focus, phase-only and gate-side like the
        // accent fit. Within the winner family, phases are re-ranked by the
        // bass-adjusted score and the phase margin gates the downbeat (same
        // 0.03 gate as the accent). Across families the same bass-adjusted
        // score re-ranks the family list; the winner's adjusted margin over
        // the best rival family CONFIRMS the tempo margin. The combined score
        // is never written — bass can only strengthen an existing resolution,
        // never change the winner or shrink a margin. Both the downbeat gate
        // and the tempo confirmation require at least two competing candidates
        // with bass evidence, so a lone candidate never self-resolves.
        double? bassDownbeatMargin = null;
        double? bassResolvedDownbeat = null;
        double? bassTempoMargin = null;
        {
            var familyAdjusted = new Dictionary<(double Bpm, Meter Meter), double>();
            double bassBest = double.NegativeInfinity;
            double bassSecond = double.NegativeInfinity;
            double? bassPhase = null;
            foreach (ScoredGrid familyMember in ranked)
            {
                MusicalTimeMap variant = VariantMap(map, familyMember.Candidate);
                if (variant.Meter is not { } meter
                    || variant.FirstDownbeatQuarter is not double downbeatQ)
                {
                    continue;
                }
                double? fit = BassDownbeatFocusFit(
                    variant, timeline, downbeatQ, meter.QuartersPerBar);
                if (fit is not double f)
                    continue;
                double totalWeight = Math.Max(0.0, familyMember.Score.TotalSignalWeight);
                double adjusted = (totalWeight * familyMember.Score.Score
                    + BassDownbeatWeight * f) / (totalWeight + BassDownbeatWeight);
                var familyKey = (Bpm: Math.Round(familyMember.Candidate.Bpm, 6), Meter: meter);
                if (!familyAdjusted.TryGetValue(familyKey, out double priorBest)
                    || adjusted > priorBest)
                {
                    familyAdjusted[familyKey] = adjusted;
                }
                if (familyKey.Bpm != Math.Round(winner.Candidate.Bpm, 6)
                    || familyKey.Meter != winner.Candidate.Meter)
                {
                    continue;
                }
                if (adjusted > bassBest)
                {
                    bassSecond = bassBest;
                    bassBest = adjusted;
                    bassPhase = familyMember.Candidate.FirstDownbeatQuarter;
                }
                else if (adjusted > bassSecond)
                {
                    bassSecond = adjusted;
                }
            }
            if (bassPhase is not null
                && double.IsFinite(bassSecond)
                && bassBest - bassSecond >= BassDownbeatMarginMin)
            {
                bassDownbeatMargin = bassBest - bassSecond;
                bassResolvedDownbeat = bassPhase;
            }
            var winnerKey = (Bpm: Math.Round(winner.Candidate.Bpm, 6), Meter: winner.Candidate.Meter);
            double winnerAdjusted = 0;
            if (familyAdjusted.TryGetValue(winnerKey, out double winnerAdjustedValue))
            {
                winnerAdjusted = winnerAdjustedValue;
                // The rival is the family the COMBINED score found closest (the
                // tempoMargin rival): the best-ranked candidate with a different
                // bpm. Bass re-scores that SAME contested pair — it never goes
                // fishing across every alias family, where a coarse alias grid
                // (e.g. 25 BPM on a 100 BPM song) trivially concentrates onsets
                // on its own beat-0 and would swamp the signal. No bass evidence
                // on the contested rival → no tempo confirmation.
                ScoredGrid? topRival = ranked.FirstOrDefault(value =>
                    Math.Round(value.Candidate.Bpm, 6) != Math.Round(winner.Candidate.Bpm, 6));
                if (topRival is { } rival)
                {
                    var rivalKey = (Bpm: Math.Round(rival.Candidate.Bpm, 6), Meter: rival.Candidate.Meter);
                    if (familyAdjusted.TryGetValue(rivalKey, out double rivalAdjusted))
                        bassTempoMargin = winnerAdjusted - rivalAdjusted;
                }
            }
        }

        // Strong-evidence gates: source fit, repeated content (spec strong gate
        // 0.90 loop-score composite / 0.40 material / 0.50 span), or rhythm.
        // Each path carries its own score/margin thresholds (Patch 8B harness
        // starting values); the no-strong-evidence path can resolve tempo alone
        // but never meter.
        bool sourceGate = winner.Score.SourcePeriodFit is >= 0.85;
        bool repeatedGate = winner.Score.RepeatedContentFit is >= 0.90
            && winner.Score.MaterialCoverage is >= 0.40
            && winner.Score.SpanCoverage is >= 0.50;
        bool roleGate = winner.Score.RhythmRole.Strong;
        double combined = winner.Score.Score;
        // Patch 8D: the effective tempo margin is the stronger of the combined
        // margin and the bass-confirmed margin. max() is deliberate: bass can
        // only confirm an existing winner, never overturn one.
        double effectiveTempoMargin = tempoMargin;
        if (bassTempoMargin is double confirmed && confirmed > effectiveTempoMargin)
            effectiveTempoMargin = confirmed;
        bool tempoResolved;
        if (sourceGate)
            tempoResolved = combined >= 0.64 && effectiveTempoMargin >= 0.02;
        else if (repeatedGate)
            tempoResolved = combined >= 0.68 && effectiveTempoMargin >= 0.03;
        else if (roleGate)
            tempoResolved = combined >= 0.70 && effectiveTempoMargin >= 0.04;
        else
            tempoResolved = combined >= 0.70 && effectiveTempoMargin >= 0.05;

        // Meter resolution follows the evidence path: a bare tempo never
        // resolves meter, and the meter margin only needs to be positive —
        // the strong evidence does the discriminating.
        bool meterResolved = tempoResolved
            && (sourceGate || repeatedGate || roleGate)
            && meterMargin > 0.0;

        // Downbeat acceptance is independent of tempo/meter: a clear phase
        // margin, or a restart boundary whose boundary error is <= 0.06 bar
        // (BoundaryFit >= exp(-0.5)) with content similarity >= 0.90. When the
        // phase evidence ties, the source-aligned candidate (offset 0 —
        // FirstDownbeatQuarter equal to the segment's QuarterPositionAtStart)
        // preserves the source's phase assertion instead of nulling the downbeat:
        // the grid may only OVERRIDE the source phase with clear evidence.
        RestartBoundaryEvidence? winnerRestart = CaptureRestartBoundaryEvidence(
            VariantMap(map, winner.Candidate),
            sourceEvidence,
            BuildBarsForMap(VariantMap(map, winner.Candidate), timeline, percussionEvidence) ?? Array.Empty<BarFeature>());
        double restartFitThreshold = Math.Exp(-(RestartBoundaryErrorLimitDownbeat * RestartBoundaryErrorLimitDownbeat)
            / (2 * RestartBoundarySigmaBars * RestartBoundarySigmaBars));
        bool restartDownbeatGate = winnerRestart is { } evidence
            && evidence.BoundaryFit >= restartFitThreshold
            && evidence.Similarity >= DownbeatRestartSimilarity;
        bool sourceAlignedDownbeat = winner.Candidate.FirstDownbeatQuarter is double winnerDb
            && Math.Abs(winnerDb - map.Segments[0].QuarterPositionAtStart) <= 1e-9;
        bool downbeatResolved = (winner.Candidate.FirstDownbeatQuarter is not null
                && (downbeatMargin >= 0.03 || restartDownbeatGate || sourceAlignedDownbeat))
            || (accentResolvedDownbeat is not null && accentDownbeatMargin >= AccentDownbeatMarginMin)
            || (bassResolvedDownbeat is not null && bassDownbeatMargin >= BassDownbeatMarginMin);
        double? resolvedDownbeat = accentResolvedDownbeat
            ?? bassResolvedDownbeat
            ?? (downbeatResolved ? winner.Candidate.FirstDownbeatQuarter : null);

        var resolution = new GridResolution(
            winner.Candidate,
            winner.Score.Score,
            tempoMargin,
            meterMargin,
            downbeatMargin,
            tempoResolved,
            meterResolved,
            downbeatResolved,
            ToBreakdown(winner));

        if (!resolution.TempoResolved || !resolution.MeterResolved)
        {
            initial.Diagnostics.GridSelection = BuildRejectionDiagnostics(
                winner, tempoMargin, meterMargin, tempoResolved, meterResolved,
                ranked, sourceGate, repeatedGate, roleGate);
            initial.Diagnostics.Warnings.Add(
                $"joint structural timing unresolved ({resolution.WinnerScore:0.###} score, " +
                $"tempo margin {resolution.TempoMargin:0.###}, meter margin {resolution.MeterMargin:0.###}; " +
                $"source={winner.Score.SourcePeriodFit?.ToString("0.###") ?? "none"}, " +
                $"{nameof(winner.Score.RepeatedContentFit)}={winner.Score.RepeatedContentFit?.ToString("0.###") ?? "none"}, " +
                $"rhythm={winner.Score.RhythmRole.Fit:0.###}; candidates=" +
                $"{string.Join(",", rankedFamilies.Take(8).Select(value =>
                    $"{value.Candidate.Bpm:0.###}/{value.Candidate.FirstDownbeatQuarter?.ToString("0.###") ?? "?"}/" +
                    $"{value.Score.Score:0.###}/{value.Score.RhythmRole.Fit:0.###}"))}");
            return initial;
        }

        // Tempo + meter resolved. The downbeat may or may not be: when it stays
        // unresolved the selected map keeps FirstDownbeatQuarter = null so no
        // FIRST_DOWNBEAT is emitted and no bar-anchored analysis runs.
        MusicalGridCandidate winnerCandidate = resolution.DownbeatResolved
            ? winner.Candidate with { FirstDownbeatQuarter = resolvedDownbeat }
            : winner.Candidate with { FirstDownbeatQuarter = null };
        ScoredGrid? alternative = rankedFamilies.Length > 1 ? rankedFamilies[1] : null;
        double? alternativeBpm = alternative is ScoredGrid other
            && Math.Abs(other.Candidate.Bpm - winner.Candidate.Bpm) > 1e-9
            ? other.Candidate.Bpm
            : null;
        double? alternativeScore = alternativeBpm is not null
            ? alternative!.Value.Score.Score
            : null;
        bool tempoAmbiguous = alternativeBpm is not null
            && alternativeScore is double competingScore
            && winner.Score.Score - competingScore < 0.04;
        MusicalTimeMap selected = VariantMap(
            map,
            winnerCandidate,
            Math.Clamp(winner.Score.Score, 0.0, 1.0),
            alternativeBpm,
            tempoAmbiguous);
        TimingDiagnostics diagnostics = initial.Diagnostics;
        diagnostics.SelectedBpm = selected.Segments[0].BeatsPerMinute;
        diagnostics.SelectedScore = winner.Score.Score;
        diagnostics.AlternativeBpm = alternativeBpm;
        diagnostics.AlternativeScore = alternativeScore;
        diagnostics.TempoConfidence = Math.Clamp(winner.Score.Score, 0.0, 1.0);
        diagnostics.TempoAmbiguous = tempoAmbiguous;
        diagnostics.MeterKnown = selected.Meter is not null;
        diagnostics.DownbeatKnown = selected.FirstDownbeatQuarter is not null;
        diagnostics.SampleZeroQuarter = selected.Segments[0].QuarterPositionAtStart;
        diagnostics.TempoMicrosecondsPerQuarter =
            selected.Segments[0].MicrosecondsPerQuarter;
        diagnostics.PhaseSource = TimingSource.SymbolicInference;
        diagnostics.PhaseUnknown = selected.FirstDownbeatQuarter is null;
        diagnostics.GridSelection = new GridSelectionDiagnostics
        {
            Attempted = true,
            TempoResolved = resolution.TempoResolved,
            MeterResolved = resolution.MeterResolved,
            DownbeatResolved = resolution.DownbeatResolved,
            SelectedBpm = winner.Candidate.Bpm,
            SelectedMeter = winner.Candidate.Meter,
            SelectedDownbeatQuarter = resolution.DownbeatResolved
                ? resolvedDownbeat
                : null,
            WinnerScore = resolution.WinnerScore,
            TempoMargin = resolution.TempoMargin,
            MeterMargin = resolution.MeterMargin,
            DownbeatMargin = resolution.DownbeatMargin,
            Breakdown = resolution.Breakdown,
            TopCandidates = ranked.Take(10).Select(value => ToReceipt(value, ranked, winner)).ToList(),
        };
        diagnostics.Warnings.Add(
            $"joint structural timing selected {selected.Segments[0].BeatsPerMinute:0.###} BPM " +
            $"({winner.Score.Score:0.###} score, {winner.Candidate.Meter} grid)");
        return new MusicalTimeMapBuildResult
        {
            Map = selected,
            Diagnostics = diagnostics,
            PercussionEvidence = percussionEvidence,
        };
    }

    /// <summary>Compatibility view for existing analyzer tests; production callers
    /// must use the map-plus-diagnostics boundary above. Re-derives the unified
    /// percussion evidence from the timeline so the test surface exercises the
    /// same evidence semantics as production.</summary>
    internal static MusicalTimeMap SelectGrid(
        MusicalTimeMap map,
        VisualizationTimeline timeline)
    {
        var diagnostics = new TimingDiagnostics
        {
            TempoSource = map.Segments[0].Source,
            PhaseSource = map.Segments[0].Source,
            SelectedBpm = map.Segments[0].BeatsPerMinute,
            TempoConfidence = map.Confidence,
            SampleZeroQuarter = map.Segments[0].QuarterPositionAtStart,
            TempoMicrosecondsPerQuarter = map.Segments[0].MicrosecondsPerQuarter,
            MeterKnown = map.Meter is not null,
            DownbeatKnown = map.FirstDownbeatQuarter is not null,
        };
        return SelectGrid(new MusicalTimeMapBuildResult
        {
            Map = map,
            Diagnostics = diagnostics,
        }, timeline, PercussionEvidenceBuilder.Build(timeline)).Map;
    }

    /// <summary>Compatibility overload for analyzer tests building a result
    /// directly; production callers must pass the SAME evidence instance that
    /// tempo inference consumed (spec §3/§7).</summary>
    internal static MusicalTimeMapBuildResult SelectGrid(
        MusicalTimeMapBuildResult initial,
        VisualizationTimeline timeline)
        => SelectGrid(initial, timeline, PercussionEvidenceBuilder.Build(timeline));

    /// <summary>
    /// Records a machine-readable rejection: stable gate keys joined by commas
    /// after a "reject:" prefix, so consumers can switch on the reason without
    /// parsing prose. Observability only — never changes the gate itself.
    /// Patch 8A: per-candidate receipts carry the explicit gate values and
    /// reasons; the summary reason keeps the stable reject: keys for consumers.
    /// Patch 8B: the rejection distinguishes an unresolved tempo from a
    /// tempo-resolved-but-meter-unresolved outcome.
    /// </summary>
    private static GridSelectionDiagnostics BuildRejectionDiagnostics(
        ScoredGrid winner,
        double tempoMargin,
        double meterMargin,
        bool tempoResolved,
        bool meterResolved,
        IReadOnlyList<ScoredGrid> ranked,
        bool sourceGate,
        bool repeatedGate,
        bool roleGate)
    {
        var failed = new List<string>();
        if (!tempoResolved)
        {
            failed.Add("tempo-unresolved");
            if (winner.Score.Score < 0.70)
                failed.Add("low-score");
            if (tempoMargin < 0.05)
                failed.Add("low-tempo-margin");
            if (!sourceGate && !repeatedGate && !roleGate)
                failed.Add("no-gate-evidence");
        }
        else
        {
            failed.Add("meter-unresolved");
            if (meterMargin <= 0.0)
                failed.Add("low-meter-margin");
        }
        return new GridSelectionDiagnostics
        {
            Attempted = true,
            TempoResolved = tempoResolved,
            MeterResolved = meterResolved,
            DownbeatResolved = false,
            TempoMargin = tempoMargin,
            MeterMargin = meterMargin,
            DownbeatMargin = double.PositiveInfinity,
            RejectionReason = $"reject:{string.Join(",", failed)}",
            TopCandidates = ranked.Take(10).Select(value => ToReceipt(value, ranked, winner)).ToList(),
        };
    }

    /// <summary>
    /// Maps the current <see cref="GridScore"/> into the observable breakdown.
    /// Component scores absent from the scorer stay at their default (0/null) —
    /// never fabricating values.
    /// </summary>
    private static GridScoreBreakdown ToBreakdown(ScoredGrid scored)
    {
        GridScore score = scored.Score;
        return new GridScoreBreakdown(
            CandidatePrior: scored.Candidate.Score,
            OnsetFit: score.OnsetFit ?? 0,
            RhythmRoleFit: score.RhythmRole.Fit,
            RestartBoundaryFit: score.RestartBoundaryFit,
            RepeatedContentFit: score.RepeatedContentFit,
            PhraseRegularityFit: score.PhraseFit,
            CombinedScore: score.Score,
            KnownRhythmRoleHits: score.RhythmRole.KnownRoleHits,
            DistinctRhythmRoles: score.RhythmRole.DistinctRoles,
            SpanCoverage: score.SpanCoverage ?? 0,
            MaterialCoverage: score.MaterialCoverage ?? 0,
            RestartBoundaryErrorBars: score.RestartEvidence?.BoundaryErrorBars,
            RestartPeriodBars: score.RestartEvidence?.PeriodBars,
            RepeatStartBar: score.BestBlock?.StartBar,
            RepeatLengthBars: score.BestBlock?.LengthBars,
            RepeatCount: score.BestBlock?.RepeatCount);
    }

    private static GridCandidateReceipt ToReceipt(
        ScoredGrid scored,
        IReadOnlyList<ScoredGrid> ranked,
        ScoredGrid? winner)
    {
        GridScore score = scored.Score;
        bool isWinner = winner is { } w
            && Math.Round(w.Candidate.Bpm, 6) == Math.Round(scored.Candidate.Bpm, 6)
            && w.Candidate.Meter == scored.Candidate.Meter
            && w.Candidate.FirstDownbeatQuarter == scored.Candidate.FirstDownbeatQuarter;
        double? tempoMargin = MarginTo(scored, ranked, other =>
            Math.Round(other.Candidate.Bpm, 6) != Math.Round(scored.Candidate.Bpm, 6));
        double? meterMargin = MarginTo(scored, ranked, other =>
            other.Candidate.Meter != scored.Candidate.Meter);
        double? downbeatMargin = MarginTo(scored, ranked, other =>
            Math.Round(other.Candidate.Bpm, 6) == Math.Round(scored.Candidate.Bpm, 6)
            && other.Candidate.Meter == scored.Candidate.Meter
            && other.Candidate.FirstDownbeatQuarter != scored.Candidate.FirstDownbeatQuarter);
        bool gateByRestart = score.SourcePeriodFit is >= 0.85;
        bool gateByRepetition = score.RepeatedContentFit is >= 0.90
            && score.MaterialCoverage is >= 0.40
            && score.SpanCoverage is >= 0.50;
        bool gateByRhythm = score.RhythmRole.Strong;
        return new GridCandidateReceipt(
            scored.Candidate.Bpm,
            scored.Candidate.Meter,
            scored.Candidate.QuarterAtSourceStart,
            scored.Candidate.FirstDownbeatQuarter,
            scored.Candidate.Score,
            score.Score,
            ToBreakdown(scored),
            tempoMargin,
            meterMargin,
            downbeatMargin,
            gateByRestart,
            gateByRepetition,
            gateByRhythm,
            CandidateRejectionReasons(scored, isWinner));
    }

    /// <summary>Score gap to the best candidate satisfying <paramref name="excluded"/>,
    /// or null when no such candidate exists (the candidate has nothing to lose to).</summary>
    private static double? MarginTo(
        ScoredGrid scored,
        IReadOnlyList<ScoredGrid> ranked,
        Func<ScoredGrid, bool> excluded)
    {
        double bestOther = double.NegativeInfinity;
        foreach (ScoredGrid other in ranked)
        {
            if (!excluded(other))
                continue;
            if (other.Score.Score > bestOther)
                bestOther = other.Score.Score;
        }
        return double.IsFinite(bestOther) ? scored.Score.Score - bestOther : null;
    }

    /// <summary>
    /// Explicit per-candidate rejection reasons — every "no-gate-evidence" carries
    /// the actual gate values (restart/repetition/rhythm), never a bare label.
    /// </summary>
    private static List<string> CandidateRejectionReasons(ScoredGrid scored, bool isWinner)
    {
        GridScore score = scored.Score;
        var reasons = new List<string>();
        if (score.Score < 0.70)
            reasons.Add($"score:{score.Score:0.###}<0.70");
        if (isWinner && score.Score < 0.70)
            reasons.Add("rejected-by-resolution-gate");
        if (!isWinner)
            reasons.Add("not-winner");
        bool gateByRestart = score.SourcePeriodFit is >= 0.85;
        bool gateByRepetition = score.RepeatedContentFit is >= 0.90
            && score.MaterialCoverage is >= 0.40
            && score.SpanCoverage is >= 0.50;
        bool gateByRhythm = score.RhythmRole.Strong;
        if (!gateByRestart && !gateByRepetition && !gateByRhythm)
        {
            reasons.Add("no-gate-evidence:"
                + $"restart={score.SourcePeriodFit?.ToString("0.###") ?? "none"},"
                + $"repetition={score.RepeatedContentFit?.ToString("0.###") ?? "none"},"
                + $"rhythm={score.RhythmRole.Fit:0.###}");
        }
        return reasons;
    }

    public static MusicalStructure Analyze(MusicalTimeMap map, VisualizationTimeline timeline)
        => Analyze(map, timeline, PercussionEvidenceBuilder.Build(timeline));

    /// <summary>Structural analysis consuming the SAME unified percussion
    /// evidence instance that timing consumed (spec §3/§7, D3); the bar
    /// percussion side is fed from it, never re-reading the timeline.</summary>
    public static MusicalStructure Analyze(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        if (map.Meter is not Meter meter || meter.QuartersPerBar <= 0
            || map.FirstDownbeatQuarter is null
            || map.Confidence < 0.60)
            return BuildContentOnlyFallback(map, timeline, percussionEvidence);

        double quartersPerBar = meter.QuartersPerBar;
        double barOrigin = map.FirstDownbeatQuarter.Value;
        double lastQuarter = map.SampleToQuarterPosition(map.EndSample);
        // Spec P0-8: the structure origin is the first downbeat, not source sample 0.
        // Bar 0 starts at the downbeat; pre-downbeat material is the pickup.
        int barCount = Math.Max(1, (int)Math.Ceiling((lastQuarter - barOrigin) / quartersPerBar));

        var pickup = new BarFeature(-1, barOrigin - quartersPerBar, barOrigin);
        BarFeature[] bars = BuildBars(
            map, timeline.Notes, percussionEvidence,
            barOrigin, quartersPerBar, barCount, pickup);
        SourceLoopEvidence sourceEvidence = CaptureSourceLoopEvidence(timeline);
        RepeatedBlock[] loops = DetectSourceLoop(map, sourceEvidence, bars)
            ?? DetectRepeatedBlocks(bars);
        (MusicalPhrase[] phrases, MusicalSection[] sections) = SegmentSections(bars, loops);

        return new MusicalStructure
        {
            Bars = bars,
            Phrases = phrases,
            Sections = sections,
            Loops = loops,
            PrimaryLoop = loops.Length > 0 ? loops[0] : null,
            Pickup = pickup.IsRest ? null : pickup,
        };
    }

    /// <summary>
    /// Hypothesis 4 (calibration): content-only loop fallback. When the grid
    /// fails to resolve — tempo/meter rejection, or a resolved tempo/meter with
    /// an unresolved downbeat — the normal path bails and per-candidate
    /// repetition evidence measured before resolution would be discarded. This
    /// re-derives the ranked candidates and promotes a NEAR-PERFECT content
    /// loop (<see cref="ContentFallbackSimilarity"/> / <see cref="ContentFallbackSpanCoverage"/>)
    /// into the structure so loopBars is reported and STRUCT_LOOP can be
    /// derived. Pure fallback: never overrides a resolved grid's loops, never
    /// touches tempo/meter/downbeat resolution gates, never fabricates
    /// phrases/sections. Returns <see cref="MusicalStructure.Empty"/> when no
    /// candidate's content clears the threshold.
    /// </summary>
    private static MusicalStructure BuildContentOnlyFallback(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        MusicalTimeMapBuildResult reselect = SelectGrid(
            new MusicalTimeMapBuildResult { Map = map, Diagnostics = new TimingDiagnostics() },
            timeline,
            percussionEvidence);
        GridSelectionDiagnostics? selection = reselect.Diagnostics.GridSelection;
        if (selection is not { TopCandidates.Count: > 0 })
            return MusicalStructure.Empty;

        RepeatedBlock? best = null;
        MusicalGridCandidate? bestCandidate = null;
        // Hypothesis 5 (calibration): the fallback must not let an alias-tempo
        // grid's repeated block displace the accepted grid's loop. On Smoking
        // Head the bpm-360 double-tempo alias grid carries a 64-bar cross-pass
        // block (sim 0.9961) that edges out the bpm-180 accepted grid's 32-bar
        // within-pass fundamental (sim 0.9958) — the 64-bar loop is an artifact
        // of the half-resolution alias, not of the music. Pass 1 restricts the
        // search to candidates at the accepted tempo (same-tempo idiom as
        // ToReceipt); pass 2 preserves the prior behavior only when no
        // accepted-tempo candidate clears the near-perfect thresholds.
        double? acceptedBpm = selection.SelectedBpm;
        for (int pass = 0; pass < 2; pass++)
        {
            bool tempoConstrained = pass == 0 && acceptedBpm is not null;
            foreach (GridCandidateReceipt receipt in selection.TopCandidates)
            {
                if (tempoConstrained
                    && Math.Round(receipt.Bpm, 6) != Math.Round(acceptedBpm!.Value, 6))
                {
                    continue;
                }
                var candidate = new MusicalGridCandidate(
                    receipt.Bpm, receipt.Meter, receipt.QuarterAtSourceStart,
                    receipt.FirstDownbeatQuarter, receipt.CandidatePrior);
                RepeatedBlock? block = AnalyzeCandidateEvidence(map, candidate, timeline, percussionEvidence)
                    .BestRepeatedBlock;
                if (block is null
                    || block.Similarity < ContentFallbackSimilarity
                    || block.SpanCoverage < ContentFallbackSpanCoverage)
                {
                    continue;
                }
                if (best is null || FallbackBlockOutranks(block, best))
                {
                    best = block;
                    bestCandidate = candidate;
                }
            }
            if (best is not null || !tempoConstrained)
                break;
        }
        if (best is null || bestCandidate is null)
            return MusicalStructure.Empty;

        MusicalTimeMap grid = VariantMap(map, bestCandidate);
        BarFeature[]? bars = BuildBarsForMap(grid, timeline, percussionEvidence);
        if (bars is null || bars.Length == 0)
            return MusicalStructure.Empty;

        RepeatedBlock promoted = best with { ContentOnlyFallback = true };
        return new MusicalStructure
        {
            Bars = bars,
            Phrases = Array.Empty<MusicalPhrase>(),
            Sections = Array.Empty<MusicalSection>(),
            Loops = new[] { promoted },
            PrimaryLoop = promoted,
            Pickup = null,
        };
    }

    /// <summary>Best near-perfect fallback loop: highest similarity, then longest
    /// period, then widest span coverage.</summary>
    private static bool FallbackBlockOutranks(RepeatedBlock candidate, RepeatedBlock current)
    {
        int bySimilarity = candidate.Similarity.CompareTo(current.Similarity);
        if (bySimilarity != 0)
            return bySimilarity > 0;
        if (candidate.LengthBars != current.LengthBars)
            return candidate.LengthBars > current.LengthBars;
        return candidate.SpanCoverage > current.SpanCoverage;
    }

    /// <summary>
    /// Patch 8A structural-evidence audit for ONE grid candidate: builds the
    /// candidate's bar grid from its OWN downbeat (bar origin = downbeat, barCount =
    /// Ceil((lastQuarter - firstDownbeatQuarter)/quartersPerBar), range covered),
    /// records every restart's bar snapping (exact position, chosen integer, error —
    /// dropped restarts are visible, never silent), and runs the repeated-block
    /// search over those bars. The corpus harness uses this to prove evidence exists
    /// per candidate BEFORE any acceptance decision. Observability only — never
    /// alters selection behavior.
    /// </summary>
    internal static CandidateStructuralEvidence AnalyzeCandidateEvidence(
        MusicalTimeMap sourceMap,
        MusicalGridCandidate candidate,
        VisualizationTimeline timeline,
        IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        MusicalTimeMap variant = VariantMap(sourceMap, candidate);
        if (variant.Meter is not Meter meter || meter.QuartersPerBar <= 0)
        {
            return new CandidateStructuralEvidence(
                HasMeter: false,
                HasDownbeat: false,
                BarOrigin: 0,
                BarCount: 0,
                QuartersPerBar: 0,
                LastQuarter: 0,
                RangeCovered: false,
                RestartSnaps: Array.Empty<RestartSnapRecord>(),
                EvaluatedMaxPeriod: 0,
                Period33Evaluated: false,
                Start0Evaluated: false,
                CompleteSpansForPeriod33: 0,
                BestRepeatedBlock: null,
                RestartEvidence: null);
        }

        double quartersPerBar = meter.QuartersPerBar;
        double lastQuarter = variant.SampleToQuarterPosition(variant.EndSample);
        if (variant.FirstDownbeatQuarter is not double downbeat)
        {
            return new CandidateStructuralEvidence(
                HasMeter: true,
                HasDownbeat: false,
                BarOrigin: 0,
                BarCount: 0,
                QuartersPerBar: quartersPerBar,
                LastQuarter: lastQuarter,
                RangeCovered: false,
                RestartSnaps: Array.Empty<RestartSnapRecord>(),
                EvaluatedMaxPeriod: 0,
                Period33Evaluated: false,
                Start0Evaluated: false,
                CompleteSpansForPeriod33: 0,
                BestRepeatedBlock: null,
                RestartEvidence: null);
        }

        int barCount = Math.Max(1, (int)Math.Ceiling((lastQuarter - downbeat) / quartersPerBar));
        BarFeature[] bars = BuildBarsForMap(variant, timeline, percussionEvidence)!;
        bool rangeCovered = bars.Length > 0 && bars[^1].QuarterEnd >= lastQuarter;

        SourceLoopEvidence sourceEvidence = CaptureSourceLoopEvidence(timeline);

        // Restart snapping audit: EVERY restart is recorded with its exact bar
        // position under this candidate's downbeat, the chosen integer bar, the
        // |exact - chosen| error, and whether it was dropped from single-restart
        // inference (boundary error beyond the 0.125-bar limit, or no room for a
        // full period before AND after it inside the capture).
        var snaps = new List<RestartSnapRecord>();
        foreach (long restart in sourceEvidence.RestartSamples)
        {
            double restartBarExact =
                (variant.SampleToQuarterPosition(restart) - downbeat) / quartersPerBar;
            int chosenBar = (int)Math.Round(restartBarExact, MidpointRounding.AwayFromZero);
            double error = Math.Abs(restartBarExact - chosenBar);
            bool dropped = error > RestartBoundaryErrorLimit
                || chosenBar < 2
                || chosenBar > bars.Length - 2;
            snaps.Add(new RestartSnapRecord(restart, restartBarExact, chosenBar, error, dropped));
        }

        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        // The XA2020 audit invariants: a 33-bar period needs at least 66 bars for
        // the search to evaluate it at all (period <= bars.Length/2), and the
        // start-0 placement needs 66 bars for two complete spans. The short render
        // tail after the second pass does not invalidate the pair — extension stops
        // at the first incomplete occurrence, never at a partial tail.
        bool period33Evaluated = bars.Length >= 66 && 33 <= maxPeriod;
        bool start0Evaluated = bars.Length >= 66;
        int completeSpansForPeriod33 = Math.Max(0, bars.Length / 33);
        RepeatedBlock? bestBlock = RepeatedContentFit(bars).Block;
        RestartBoundaryEvidence? restartEvidence =
            CaptureRestartBoundaryEvidence(variant, sourceEvidence, bars);

        return new CandidateStructuralEvidence(
            HasMeter: true,
            HasDownbeat: true,
            BarOrigin: downbeat,
            BarCount: barCount,
            QuartersPerBar: quartersPerBar,
            LastQuarter: lastQuarter,
            RangeCovered: rangeCovered,
            RestartSnaps: snaps,
            EvaluatedMaxPeriod: maxPeriod,
            Period33Evaluated: period33Evaluated,
            Start0Evaluated: start0Evaluated,
            CompleteSpansForPeriod33: completeSpansForPeriod33,
            BestRepeatedBlock: bestBlock,
            RestartEvidence: restartEvidence);
    }

    private static MusicalTimeMap VariantMap(
        MusicalTimeMap source,
        MusicalGridCandidate candidate,
        double? confidence = null,
        double? alternateBpm = null,
        bool? tempoAmbiguous = null)
    {
        double samplesPerQuarter = source.SampleRate * 60.0 / candidate.Bpm;
        TempoSegment segment = source.Segments[0] with
        {
            QuarterPositionAtStart = candidate.QuarterAtSourceStart,
            SamplesPerQuarter = samplesPerQuarter,
            BeatsPerMinute = candidate.Bpm,
        };
        return new MusicalTimeMap(
            source.SampleRate,
            source.StartSample,
            new[] { segment },
            candidate.Meter,
            candidate.FirstDownbeatQuarter,
            confidence ?? source.Confidence,
            alternateBpm ?? source.AlternateBpm,
            tempoAmbiguous ?? source.IsTempoAmbiguous,
            source.GridCandidates);
    }

    /// <summary>
    /// Restart-boundary signal plan (Hypothesis 8 calibration). The restart
    /// signal is a phase-only alignment — where the restart marker falls relative
    /// to each candidate's downbeat. A single coincidental phase can land the
    /// marker within <see cref="RestartBoundaryErrorLimit"/> of a bar boundary
    /// for almost ANY grid (on Robotnik the marker aligns with every tempo
    /// family at some phase), so one lucky phase is not tempo evidence. Following
    /// the Exp 6 doctrine (accent/bass phase signals require at least two family
    /// phases with evidence), the restart signal contributes only when >= 2 phase
    /// candidates of the same (bpm, meter) family carry restart-boundary
    /// evidence. The baseline is the weakest fit among SUPPORTED candidates only,
    /// so an unsupported near-threshold candidate can no longer amplify every
    /// other candidate's restart advantage (the Robotnik 0.123 knife-edge).
    /// </summary>
    private static (double Baseline, Dictionary<(double Bpm, Meter Meter), int> FamilySupport)
        ComputeRestartSignalPlan(
            MusicalTimeMap source,
            VisualizationTimeline timeline,
            SourceLoopEvidence sourceEvidence,
            IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        var support = new Dictionary<(double Bpm, Meter Meter), int>();
        var familyMinFit = new Dictionary<(double Bpm, Meter Meter), double>();
        foreach (MusicalGridCandidate candidate in source.GridCandidates)
        {
            MusicalTimeMap variant = VariantMap(source, candidate);
            if (variant.Meter is not Meter meter || variant.FirstDownbeatQuarter is not double)
                continue;
            BarFeature[]? bars = BuildBarsForMap(variant, timeline, percussionEvidence);
            if (bars is null)
                continue;
            double? fit = CaptureRestartBoundaryEvidence(variant, sourceEvidence, bars)?.BoundaryFit;
            if (fit is not double finite)
                continue;
            var key = (Bpm: Math.Round(candidate.Bpm, 6), Meter: meter);
            support[key] = support.TryGetValue(key, out int count) ? count + 1 : 1;
            familyMinFit[key] = Math.Min(
                familyMinFit.TryGetValue(key, out double prior) ? prior : double.PositiveInfinity,
                finite);
        }
        double lowest = double.PositiveInfinity;
        foreach (((double Bpm, Meter Meter) key, int count) in support)
        {
            if (count >= 2 && familyMinFit[key] < lowest)
                lowest = familyMinFit[key];
        }
        return (double.IsFinite(lowest) ? lowest : 0.0, support);
    }

    private static GridScore GridStructuralScore(
        MusicalGridCandidate candidate,
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        SourceLoopEvidence sourceEvidence,
        double restartBaseline,
        IReadOnlyDictionary<(double Bpm, Meter Meter), int> restartFamilySupport,
        IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        // No meter means no bar grid to score at all — the candidate cannot be
        // ranked, only this hard rejection is possible.
        if (map.Meter is not Meter meter)
            return new GridScore(double.NegativeInfinity, null, null, null, default, null, null, null, null);

        double quartersPerBar = meter.QuartersPerBar;
        double? downbeat = map.FirstDownbeatQuarter;

        var signals = new List<(double Weight, double Value)>();
        void Add(double? value, double weight)
        {
            if (value is double finite && double.IsFinite(finite))
                signals.Add((weight, Math.Clamp(finite, 0.0, 1.0)));
        }

        // Candidate prior: the tempo-inference confidence of this candidate's
        // (bpm, meter, phase) grid. Always available — never substituted.
        Add(candidate.Score, 0.15);
        double? sourceFit = SourcePeriodFit(map, sourceEvidence, quartersPerBar);
        double? restartBoundaryFit = null;
        RestartBoundaryEvidence? restartEvidence = null;
        RepeatedContentEvidence repeated = default;
        RhythmRoleEvidence rhythm = default;
        double? phraseFit = null;
        double? onsetFit = null;
        double? spanCoverage = null;
        double? materialCoverage = null;

        if (downbeat is double db)
        {
            // Bar-anchored signals: only when a downbeat pins the bar grid.
            onsetFit = OnsetGridFit(map, timeline, db);
            Add(onsetFit, 0.20);
            rhythm = RhythmRoleFit(map, percussionEvidence, db, quartersPerBar);
            Add(rhythm.TotalHits > 0 ? rhythm.Fit : null, 0.20);
            BarFeature[] bars = BuildBarsForMap(map, timeline, percussionEvidence)!;
            restartEvidence = CaptureRestartBoundaryEvidence(map, sourceEvidence, bars);
            restartBoundaryFit = restartEvidence?.BoundaryFit;
            // Hypothesis 8: the discriminating restart signal contributes only
            // for families whose restart alignment is corroborated by >= 2 phase
            // candidates (Exp 6 doctrine: phase-only evidence needs more than one
            // coincidental phase). A lone phase landing the marker within the
            // boundary limit is phase luck — it must not carry a family to
            // victory, and it must not set the baseline others are measured
            // against. The raw fit stays observable in the breakdown.
            bool restartSupported = restartEvidence is not null
                && restartFamilySupport.TryGetValue(
                    (Bpm: Math.Round(candidate.Bpm, 6), Meter: map.Meter), out int phaseCount)
                && phaseCount >= 2;
            // Discriminating restart signal: only the candidate's advantage
            // over the shared-evidence baseline contributes, so a restart
            // boundary every candidate aligns to adds exactly 0 (it cannot
            // compress the tempo/meter margin). An unsupported family (single
            // coincidental phase) must not be REWARDED for phase luck — but
            // dropping the weight outright renormalizes the remaining signals
            // and silently squeezes every tempo margin on single-phase
            // fixtures (HalfTempo 149.4 margin 0.083 -> 0.036). Neutralize:
            // keep the weight, contribute exactly 0, so the margin scale of
            // the base scoring is preserved while the signal never votes.
            if (restartSupported)
                Add(restartBoundaryFit - restartBaseline, 0.25);
            else if (restartBoundaryFit is not null)
                Add(0.0, 0.25);
            repeated = RepeatedContentFit(bars);
            spanCoverage = repeated.SpanCoverage > 0 ? repeated.SpanCoverage : null;
            materialCoverage = repeated.MaterialCoverage > 0 ? repeated.MaterialCoverage : null;
            Add(repeated.Fit > 0 ? repeated.Fit : null, 0.15);
            phraseFit = PhraseRegularityFit(bars);
            Add(phraseFit, 0.05);
            // Source-period fit: the grid-tempo discriminator that does not
            // depend on the downbeat. Onset alignment alone cannot separate
            // half/double tempo (every onset lands on both grids); the source
            // loop period in bars is the signal that does (33 <= 64 vs 66 > 64).
            Add(sourceFit, 0.20);
        }
        else
        {
            // Downbeat unresolved: score only the downbeat-independent signals
            // (candidate prior above, source-period fit, restart-boundary fit).
            // Never fabricate a downbeat or bar structure — bar-dependent
            // signals (onset phase, rhythm roles, repeated content, phrase
            // regularity) stay off and the available weights are renormalized.
            // Source-period fit takes the onset slot: it is the other grid
            // alignment signal that does not need a downbeat.
            Add(sourceFit, 0.20);
            Add(restartBoundaryFit - restartBaseline, 0.25); // null without a downbeat: never added
        }

        double totalWeight = signals.Sum(signal => signal.Weight);
        double score = signals.Sum(signal => signal.Weight * signal.Value) / totalWeight;
        return new GridScore(
            score,
            sourceFit,
            repeated.Fit > 0 ? repeated.Fit : null,
            restartBoundaryFit,
            rhythm,
            onsetFit,
            phraseFit,
            spanCoverage,
            materialCoverage,
            repeated.Block,
            restartEvidence,
            totalWeight);
    }

    private static double? OnsetGridFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat)
    {
        var positions = new List<long>();
        positions.AddRange((timeline.Notes ?? Array.Empty<NoteEvent>())
            .Where(note => note is not null)
            .Select(note => note.StartSample));
        positions.AddRange((timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .Where(hit => hit is not null)
            .Select(hit => hit.SamplePosition));
        positions.AddRange((timeline.AggregateHits ?? Array.Empty<AggregateHitEvent>())
            .Where(hit => hit is not null)
            .Select(hit => hit.SamplePosition));
        if (positions.Count == 0)
            return null;

        double total = 0;
        foreach (long sample in positions)
        {
            double quarter = map.SampleToQuarterPosition(sample);
            double grid = (quarter - downbeat) * 4.0;
            double residual = Math.Abs(grid - Math.Round(grid));
            total += 1.0 - Math.Min(0.5, residual) / 0.5;
        }
        return total / positions.Count;
    }

    /// <summary>
    /// Sustained-start accent fit: the share of sustained note starts (onset-to-
    /// offset span &gt;= <see cref="AccentSustainedQuarterSpan"/> quarter) whose onset
    /// rounds to beat class 0, relative to the mean of the other beat classes,
    /// through r/(1+r). A downbeat is acoustically accented when sustained
    /// material begins on it far more often than on the other beats. Null when
    /// fewer than <see cref="AccentMinimumSustainedStarts"/> sustained starts exist —
    /// the signal is absent, never a weak 0 (a percussion-only track must not
    /// pretend to phase evidence).
    /// </summary>
    private static double? DownbeatAccentFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat,
        double quartersPerBar)
    {
        IReadOnlyList<NoteEvent> notes = timeline.Notes ?? Array.Empty<NoteEvent>();
        int beatClasses = Math.Max(1, (int)Math.Ceiling(quartersPerBar));
        int[] counts = new int[beatClasses];
        int sustained = 0;
        double samplesPerQuarter = map.Segments[0].MicrosecondsPerQuarter
            / 1_000_000.0 * map.SampleRate;
        foreach (NoteEvent note in notes)
        {
            if (note is null)
                continue;
            if ((note.EndSample - note.StartSample) / samplesPerQuarter < AccentSustainedQuarterSpan)
                continue;
            sustained++;
            double beat = PositiveModulo(
                map.SampleToQuarterPosition(note.StartSample) - downbeat,
                quartersPerBar);
            for (int k = 0; k < beatClasses; k++)
            {
                if (Math.Abs(beat - k) <= 0.5)
                {
                    counts[k]++;
                    break;
                }
            }
        }
        if (sustained < AccentMinimumSustainedStarts)
            return null;
        int beatZero = counts[0];
        double otherMean = beatClasses > 1
            ? counts.Skip(1).Sum() / (double)(beatClasses - 1)
            : 0;
        double ratio = beatZero / Math.Max(1.0, otherMean);
        return ratio / (1.0 + ratio);
    }

    /// <summary>
    /// Bass-downbeat focus fit: the share of low-register (MIDI &lt;=
    /// <see cref="BassMaximumMidiNote"/>) onsets that round to beat class 0,
    /// relative to the mean of the other beat classes, through r/(1+r). Bass
    /// lines anchor the downbeat: when low-register material begins on beat 0
    /// far more often than on the other beats, the candidate's own downbeat is
    /// acoustically grounded. Null when fewer than
    /// <see cref="BassMinimumOnsets"/> bass onsets exist — the signal is
    /// absent, never a weak 0. The 6-onset floor mirrors the accent fit's
    /// "evidence or nothing" rule; it is lower than the accent's 8 because
    /// bass material is sparser than sustained starts, and 6 still guarantees
    /// more than one full bar's worth of bass attack material for any meter.
    /// </summary>
    private static double? BassDownbeatFocusFit(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        double downbeat,
        double quartersPerBar)
    {
        IReadOnlyList<NoteEvent> notes = timeline.Notes ?? Array.Empty<NoteEvent>();
        int beatClasses = Math.Max(1, (int)Math.Ceiling(quartersPerBar));
        int[] counts = new int[beatClasses];
        int bassOnsets = 0;
        foreach (NoteEvent note in notes)
        {
            if (note is null || note.InitialMidiNote > BassMaximumMidiNote)
                continue;
            bassOnsets++;
            double beat = PositiveModulo(
                map.SampleToQuarterPosition(note.StartSample) - downbeat,
                quartersPerBar);
            for (int k = 0; k < beatClasses; k++)
            {
                if (Math.Abs(beat - k) <= 0.5)
                {
                    counts[k]++;
                    break;
                }
            }
        }
        if (bassOnsets < BassMinimumOnsets)
            return null;
        int beatZero = counts[0];
        double otherMean = beatClasses > 1
            ? counts.Skip(1).Sum() / (double)(beatClasses - 1)
            : 0;
        double ratio = beatZero / Math.Max(1.0, otherMean);
        return ratio / (1.0 + ratio);
    }

    private static RhythmRoleEvidence RhythmRoleFit(
        MusicalTimeMap map,
        IReadOnlyList<PercussiveOnset> percussionEvidence,
        double downbeat,
        double quartersPerBar)
    {
        // Unified evidence fit (spec §3/§7, D3): the SAME percussion collection
        // that tempo inference consumed. Role labels come from the evidence —
        // never re-derived from the timeline — and unknown-role onsets keep the
        // generic whole-beat subdivision catch-all (they are never forced into a
        // BD/SD template, and they never count toward knownRoleHits or
        // distinctRoles).
        if (percussionEvidence.Count == 0)
            return default;

        // Meter-aware role targets (spec Patch 3d). 3/4 and 6/8 share
        // QuartersPerBar == 3, so branch on the signature, not bar length:
        // 3/4 has no rock backbeats (snare never anchors beat 2/quarter 1) and a
        // strong beat-1 preference; 6/8 anchors the dotted-quarter groups at
        // quarters 0 and 1.5 with eighth-note subdivision. 4/4 keeps the classic
        // backbeat template.
        bool isSixEight = map.Meter is { Numerator: 6, Denominator: 8 };
        bool isThreeFour = map.Meter is { Numerator: 3, Denominator: 4 };

        double total = 0;
        int knownRoleHits = 0;
        var distinctRoles = new HashSet<RhythmRole>();
        foreach (PercussiveOnset onset in percussionEvidence)
        {
            double beat = PositiveModulo(
                map.SampleToQuarterPosition(onset.SamplePosition) - downbeat,
                quartersPerBar);
            RhythmRole role = onset.Role;
            double fit = role switch
            {
                RhythmRole.Bd when isSixEight => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (0.0, 1.0),
                    (1.5, 0.85)),
                RhythmRole.Bd when isThreeFour => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (0.0, 1.0),
                    (2.0, 0.55)),
                RhythmRole.Bd => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (0.0, 1.0),
                    (2.0, 0.70)),
                RhythmRole.Sd when isSixEight => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (0.0, 0.85),
                    (1.5, 1.0)),
                RhythmRole.Sd when isThreeFour => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (0.0, 1.0),
                    (2.0, 0.45)),
                RhythmRole.Sd => WeightedRoleAlignment(
                    beat,
                    quartersPerBar,
                    (1.0, 1.0),
                    (3.0, 0.85)),
                RhythmRole.Hh when isSixEight || isThreeFour
                    => SubdivisionAlignment(beat, 0.5),
                RhythmRole.Hh => SubdivisionAlignment(beat, 0.25),
                RhythmRole.Tom or RhythmRole.Top or RhythmRole.Rim
                    => SubdivisionAlignment(beat, 0.5),
                _ => SubdivisionAlignment(beat, 1.0),
            };
            if (role != RhythmRole.Unknown)
            {
                knownRoleHits++;
                distinctRoles.Add(role);
            }
            total += fit;
        }

        double fitScore = total / percussionEvidence.Count;
        // The strong rhythm gate: at least 16 known-role hits spanning at least
        // two distinct roles, with the weighted fit >= 0.85.
        bool strong = knownRoleHits >= 16 && distinctRoles.Count >= 2 && fitScore >= 0.85;
        return new RhythmRoleEvidence(
            fitScore, strong, knownRoleHits, percussionEvidence.Count, distinctRoles.Count);
    }

    private static double WeightedRoleAlignment(
        double beat,
        double quartersPerBar,
        (double Position, double Weight) first,
        (double Position, double Weight) second)
    {
        double firstDistance = CircularDistance(beat, first.Position, quartersPerBar);
        double secondDistance = CircularDistance(beat, second.Position, quartersPerBar);
        double firstFit = 1.0 - Math.Min(0.5, firstDistance) / 0.5;
        double secondFit = 1.0 - Math.Min(0.5, secondDistance) / 0.5;
        return Math.Max(firstFit * first.Weight, secondFit * second.Weight);
    }

    private static double SubdivisionAlignment(double beat, double subdivision)
    {
        double residual = Math.Abs(beat - Math.Round(beat / subdivision) * subdivision);
        return 1.0 - Math.Min(subdivision / 2.0, residual) / (subdivision / 2.0);
    }

    private static double CircularDistance(double value, double target, double period)
    {
        double delta = Math.Abs(value - target) % period;
        return Math.Min(delta, period - delta);
    }

    private static double? SourcePeriodFit(
        MusicalTimeMap map,
        SourceLoopEvidence evidence,
        double quartersPerBar)
    {
        IReadOnlyList<(long Start, long End)> periods = SourcePeriods(evidence);
        if (periods.Count == 0)
            return null;

        double total = 0;
        int count = 0;
        foreach ((long start, long end) in periods)
        {
            double bars = (map.SampleToQuarterPosition(end)
                - map.SampleToQuarterPosition(start)) / quartersPerBar;
            if (bars < 2 || bars > MaxRepeatPeriodBars)
                continue;
            double error = Math.Abs(bars - Math.Round(bars));
            total += Math.Max(0, 1.0 - Math.Min(1.0, error));
            count++;
        }
        return count == 0 ? null : total / count;
    }

    private static RepeatedContentEvidence RepeatedContentFit(BarFeature[] bars)
    {
        if (bars.Length < 4)
            return default;
        RepeatedBlock? best = null;
        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? block = FindBestRepeatedBlock(bars, period);
            if (block is null)
                continue;
            if (best is null || BlockOutranks(block, best))
                best = block;
        }
        return best is null
            ? default
            : new RepeatedContentEvidence(LoopScore(best), best.MaterialCoverage, best.SpanCoverage, best);
    }

    /// <summary>
    /// Spec loop ranking score: 0.60*Similarity + 0.25*SpanCoverage +
    /// 0.15*MaterialCoverage.
    /// </summary>
    private static double LoopScore(RepeatedBlock block)
        => 0.60 * block.Similarity + 0.25 * block.SpanCoverage + 0.15 * block.MaterialCoverage;

    /// <summary>
    /// True when <paramref name="candidate"/> outranks <paramref name="current"/>:
    /// higher loopScore; when the scores are within
    /// <see cref="FundamentalPeriodTolerance"/> and one period is a multiple of the
    /// other, the SHORTER (fundamental) period wins; then higher span coverage;
    /// then the earlier start. Anchor/source tiers are ranked by callers before
    /// this tie-break applies.
    /// </summary>
    private static bool BlockOutranks(RepeatedBlock candidate, RepeatedBlock current)
    {
        double scoreDelta = LoopScore(candidate) - LoopScore(current);
        if (Math.Abs(scoreDelta) > FundamentalPeriodTolerance)
            return scoreDelta > 0;
        if (candidate.LengthBars != current.LengthBars)
        {
            int longer = Math.Max(candidate.LengthBars, current.LengthBars);
            int shorter = Math.Min(candidate.LengthBars, current.LengthBars);
            if (shorter > 0 && longer % shorter == 0)
                return candidate.LengthBars < current.LengthBars;
        }
        if (candidate.SpanCoverage != current.SpanCoverage)
            return candidate.SpanCoverage > current.SpanCoverage;
        return candidate.StartBar < current.StartBar;
    }

    private static int CompareBlocks(RepeatedBlock x, RepeatedBlock y)
    {
        if (BlockOutranks(x, y))
            return -1;
        if (BlockOutranks(y, x))
            return 1;
        return 0;
    }

    private static double? PhraseRegularityFit(BarFeature[] bars)
    {
        if (bars.Length < 8)
            return null;
        double total = 0;
        int count = 0;
        for (int start = 0; start + 8 <= bars.Length; start += 4)
        {
            total += bars[start..(start + 4)].Average(
                bar => bar.Similarity(bars[start + 4 + (bar.BarIndex - start)]));
            count++;
        }
        return count == 0 ? null : total / count;
    }


    private static IReadOnlyList<(long Start, long End)> SourcePeriods(
        SourceLoopEvidence evidence)
    {
        var result = new List<(long Start, long End)>();
        if (evidence.EntrySample is long entry && evidence.RestartSamples.Count > 0)
            result.Add((entry, evidence.RestartSamples[0]));
        for (int index = 1; index < evidence.RestartSamples.Count; index++)
            result.Add((evidence.RestartSamples[index - 1], evidence.RestartSamples[index]));
        return result;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static double BlockSimilarity(BarFeature[] bars, int start, int length)
    {
        if (start < 0 || start + length * 2 > bars.Length)
            return 0;
        double total = 0;
        for (int offset = 0; offset < length; offset++)
            total += bars[start + offset].Similarity(bars[start + length + offset]);
        return total / length;
    }


    private static BarFeature[] BuildBars(
        MusicalTimeMap map,
        IReadOnlyList<NoteEvent>? notes,
        IReadOnlyList<PercussiveOnset> percussionEvidence,
        double barOrigin,
        double quartersPerBar,
        int barCount,
        BarFeature? pickup = null)
    {
        var bars = new BarFeature[barCount];
        for (int i = 0; i < barCount; i++)
            bars[i] = new BarFeature(
                i,
                barOrigin + i * quartersPerBar,
                barOrigin + (i + 1) * quartersPerBar);

        if (notes is not null)
        {
            foreach (NoteEvent note in notes)
            {
                if (note is null || note.EndSample <= note.StartSample)
                    continue;

                long segmentStart = note.StartSample;
                double midiNote = note.InitialMidiNote;
                foreach (PitchChange change in (note.Pitch ?? Array.Empty<PitchChange>())
                    .OrderBy(change => change.SamplePosition))
                {
                    long segmentEnd = Math.Clamp(
                        change.SamplePosition, note.StartSample, note.EndSample);
                    if (segmentEnd > segmentStart)
                    {
                        AddNoteSpan(
                            map,
                            bars,
                            barOrigin,
                            quartersPerBar,
                            segmentStart,
                            segmentEnd,
                            PitchClass(midiNote),
                            note,
                            isAttack: segmentStart == note.StartSample,
                            pickup);
                    }

                    if (change.SamplePosition >= note.EndSample)
                    {
                        segmentStart = note.EndSample;
                        break;
                    }
                    midiNote = change.MidiNote;
                    segmentStart = Math.Max(segmentStart, change.SamplePosition);
                }

                if (segmentStart < note.EndSample)
                {
                    AddNoteSpan(
                        map,
                        bars,
                        barOrigin,
                        quartersPerBar,
                        segmentStart,
                        note.EndSample,
                        PitchClass(midiNote),
                        note,
                        isAttack: segmentStart == note.StartSample,
                        pickup);
                }
            }
        }

        if (percussionEvidence is not null)
        {
            foreach (PercussiveOnset onset in percussionEvidence)
            {
                double quarter = map.SampleToQuarterPosition(onset.SamplePosition);
                int barIndex = (int)Math.Floor((quarter - barOrigin) / quartersPerBar);
                if (barIndex < 0)
                {
                    // Pre-origin material is the pickup, never a negative bar.
                    if (pickup is not null && quarter < barOrigin)
                        pickup.AddRhythmOnset(
                            OnsetSlotInSpan(quarter, pickup.QuarterStart, quartersPerBar),
                            onset.VoiceId, onset.Domain);
                    continue;
                }
                if (barIndex >= barCount)
                    continue;
                double barStart = barOrigin + barIndex * quartersPerBar;
                bars[barIndex].AddRhythmOnset(
                    OnsetSlotInSpan(quarter, barStart, quartersPerBar), onset.VoiceId, onset.Domain);
            }
        }

        foreach (BarFeature bar in bars)
            bar.FinalizeFeatures();
        pickup?.FinalizeFeatures();
        return bars;
    }

    /// <summary>16-slot onset position of a quarter within a bar span.</summary>
    private static int OnsetSlotInSpan(double quarter, double spanStart, double quartersPerBar)
        => (int)Math.Floor(
            Math.Clamp((quarter - spanStart) / quartersPerBar * 16.0, 0, 15));

    /// <summary>Builds the per-bar feature array for a candidate map's grid.
    /// Returns null when the map lacks meter or a downbeat.</summary>
    private static BarFeature[]? BuildBarsForMap(
        MusicalTimeMap map,
        VisualizationTimeline timeline,
        IReadOnlyList<PercussiveOnset> percussionEvidence)
    {
        if (map.Meter is not Meter meter || map.FirstDownbeatQuarter is not double downbeat)
            return null;
        double quartersPerBar = meter.QuartersPerBar;
        double lastQuarter = map.SampleToQuarterPosition(map.EndSample);
        int barCount = Math.Max(
            1,
            (int)Math.Ceiling((lastQuarter - downbeat) / quartersPerBar));
        return BuildBars(
            map,
            timeline.Notes,
            percussionEvidence,
            downbeat,
            quartersPerBar,
            barCount);
    }

    private static void AddNoteSpan(
        MusicalTimeMap map,
        BarFeature[] bars,
        double barOrigin,
        double quartersPerBar,
        long startSample,
        long endSample,
        int pitchClass,
        NoteEvent note,
        bool isAttack,
        BarFeature? pickup = null)
    {
        double start = map.SampleToQuarterPosition(startSample);
        double end = map.SampleToQuarterPosition(endSample);
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            return;

        // Material before the downbeat belongs to the pickup (the partial leading
        // bar) — never mislabeled as bar 0, and never dropped. With no pickup
        // target (scoring path) pre-origin spans are excluded from the grid.
        if (start < barOrigin)
        {
            if (pickup is null)
                return;
            double pickupEnd = Math.Min(end, barOrigin);
            pickup.AddNoteSegment(
                pitchClass,
                pickupEnd - start,
                OnsetSlotInSpan(start, pickup.QuarterStart, quartersPerBar),
                note.ChannelId,
                note.Domain,
                isAttack);
            if (end <= barOrigin)
                return;
            start = barOrigin;
            isAttack = false; // the attack was already counted in the pickup
        }

        int firstBar = Math.Max(0, (int)Math.Floor((start - barOrigin) / quartersPerBar));
        int lastBar = Math.Min(
            bars.Length - 1,
            (int)Math.Floor((end - barOrigin - double.Epsilon) / quartersPerBar));
        int attackBar = Math.Clamp(firstBar, 0, bars.Length - 1);
        for (int barIndex = firstBar; barIndex <= lastBar; barIndex++)
        {
            double barStart = barOrigin + barIndex * quartersPerBar;
            double overlap = Math.Min(end, barStart + quartersPerBar)
                - Math.Max(start, barStart);
            if (overlap <= 0)
                continue;
            bool attack = isAttack && barIndex == attackBar;
            int onset = attack
                ? (int)Math.Floor(
                    Math.Clamp((start - barStart) / quartersPerBar * 16.0, 0, 15))
                : 0;
            bars[barIndex].AddNoteSegment(
                pitchClass,
                overlap,
                onset,
                note.ChannelId,
                note.Domain,
                attack);
        }
    }

    private static int PitchClass(double midiNote)
    {
        int rounded = (int)Math.Round(midiNote);
        int pitchClass = rounded % 12;
        return pitchClass < 0 ? pitchClass + 12 : pitchClass;
    }

    private static (MusicalPhrase[] Phrases, MusicalSection[] Sections) SegmentSections(
        BarFeature[] bars,
        IReadOnlyList<RepeatedBlock> loops)
    {
        if (bars.Length == 0)
            return (Array.Empty<MusicalPhrase>(), Array.Empty<MusicalSection>());
        // Rest-only material has no musical form: no phrases, no sections
        // (spec P1-12 — silence must not produce PHRASE markers).
        if (bars.All(bar => bar.IsRest))
            return (Array.Empty<MusicalPhrase>(), Array.Empty<MusicalSection>());

        RepeatedBlock? primary = loops.FirstOrDefault();
        MusicalPhrase[] phrases = BuildPhrases(bars, primary);
        MusicalSection[] sections = BuildSections(phrases);
        return (phrases, sections);
    }

    /// <summary>
    /// Splits the piece into 4-bar phrases. Span boundaries come from the validated
    /// loop (intro, each loop occurrence, tail) so phrases NEVER cross a loop
    /// boundary. Within a span, a 1-bar remainder becomes TURNAROUND and a 2-3 bar
    /// remainder is attached to the preceding in-span phrase — no normal phrase is
    /// ever shorter than 4 bars. Labels are reused across equal-length comparisons
    /// (PHRASE_A, PHRASE_B, ...) with the match similarity as confidence.
    /// </summary>
    private static MusicalPhrase[] BuildPhrases(BarFeature[] bars, RepeatedBlock? primary)
    {
        var phrases = new List<MusicalPhrase>();
        var representatives = new List<PhraseFeature>();
        var longRepresentatives = new List<PhraseFeature>();

        foreach ((int spanStart, int spanEnd) in PhraseSpans(bars.Length, primary))
        {
            int phraseCountBeforeSpan = phrases.Count;
            int start = spanStart;
            while (spanEnd - start >= 4)
            {
                phrases.Add(MakePhrase(
                    bars, representatives, longRepresentatives, start, length: 4));
                start += 4;
            }

            int remainder = spanEnd - start; // 0..3
            if (remainder == 1)
            {
                phrases.Add(new MusicalPhrase(start, spanEnd, "TURNAROUND", 1.0));
            }
            else if (remainder >= 2)
            {
                if (phrases.Count > phraseCountBeforeSpan)
                {
                    // Attach the 2-3 bar remainder to the preceding in-span phrase.
                    MusicalPhrase previous = phrases[^1];
                    phrases[^1] = previous with { EndBar = spanEnd };
                }
                else
                {
                    // A span shorter than 4 bars with no in-span predecessor (a
                    // short intro/tail): attaching would cross a loop boundary, so
                    // it stands alone as a short structural span.
                    phrases.Add(MakePhrase(
                        bars, representatives, longRepresentatives, start, spanEnd - start));
                }
            }
        }
        return phrases.ToArray();
    }

    /// <summary>
    /// Structural spans over which phrases are quantized: the intro before the
    /// loop, each loop occurrence (truncated at the capture end), and the tail.
    /// Without a validated loop the whole capture is one span.
    /// </summary>
    private static (int Start, int End)[] PhraseSpans(int barCount, RepeatedBlock? primary)
    {
        if (primary is not { LengthBars: > 0 } loop)
            return new[] { (0, barCount) };

        var spans = new List<(int Start, int End)>();
        if (loop.StartBar > 0)
            spans.Add((0, Math.Min(barCount, loop.StartBar)));
        for (int occurrence = loop.StartBar; occurrence < barCount; occurrence += loop.LengthBars)
        {
            int end = Math.Min(barCount, occurrence + loop.LengthBars);
            if (end > occurrence)
                spans.Add((occurrence, end));
        }
        return spans.ToArray();
    }

    /// <summary>
    /// Builds one labeled phrase starting at <paramref name="start"/> with the given
    /// length. Confidence is 1.0 for a new label; for repeats it is the
    /// corresponding-bar similarity to the representative that earned the label.
    /// </summary>
    private static MusicalPhrase MakePhrase(
        BarFeature[] bars,
        List<PhraseFeature> representatives,
        List<PhraseFeature> longRepresentatives,
        int start,
        int length)
    {
        var phrase = new PhraseFeature(start, bars.Skip(start).Take(length).ToArray());
        PhraseFeature? longPhrase = start + 8 <= bars.Length
            ? new PhraseFeature(start, bars.Skip(start).Take(8).ToArray())
            : null;

        int repeatOf = representatives.FindIndex(
            prior => prior.Similarity(phrase) >= SimilarityThreshold);

        // Compare complete eight-bar phrases in corresponding-bar order as a
        // tie-breaker. Four-bar PHRASE_* labels remain the public segmentation
        // unit while recognizing repeated 8-bar forms.
        if (repeatOf < 0 && longPhrase is not null)
        {
            int longRepeat = longRepresentatives.FindIndex(
                prior => prior.Similarity(longPhrase) >= SimilarityThreshold);
            if (longRepeat >= 0 && longRepeat < representatives.Count)
                repeatOf = longRepeat;
        }

        if (repeatOf < 0)
        {
            repeatOf = representatives.Count;
            representatives.Add(phrase);
            if (longPhrase is not null)
                longRepresentatives.Add(longPhrase);
            return new MusicalPhrase(start, start + length, PhraseLabel(repeatOf), Confidence: 1.0);
        }

        double confidence = phrase.Similarity(representatives[repeatOf]);
        if (longPhrase is not null && repeatOf < longRepresentatives.Count)
            confidence = Math.Max(confidence, longPhrase.Similarity(longRepresentatives[repeatOf]));
        return new MusicalPhrase(start, start + length, PhraseLabel(repeatOf), confidence);
    }

    /// <summary>
    /// Sections merge adjacent phrases that share a label (targeting 8 bars).
    /// Turnarounds are phrase-level only — a 1-bar span is not a normal section —
    /// and each section's confidence is the average of its merged phrases'.
    /// </summary>
    private static MusicalSection[] BuildSections(IReadOnlyList<MusicalPhrase> phrases)
    {
        var sections = new List<MusicalSection>();
        MusicalSection? current = null;
        double confidenceSum = 0;
        int confidenceCount = 0;

        foreach (MusicalPhrase phrase in phrases)
        {
            if (string.Equals(phrase.Label, "TURNAROUND", StringComparison.Ordinal))
                continue;
            string label = phrase.Label.StartsWith("PHRASE_", StringComparison.Ordinal)
                ? phrase.Label["PHRASE_".Length..]
                : phrase.Label;
            if (current is not null
                && current.Label == label
                && current.EndBar == phrase.StartBar)
            {
                confidenceSum += phrase.Confidence;
                confidenceCount++;
                current = current with { EndBar = phrase.EndBar };
            }
            else
            {
                if (current is not null)
                    sections.Add(current);
                confidenceSum = phrase.Confidence;
                confidenceCount = 1;
                current = new MusicalSection(
                    phrase.StartBar, phrase.EndBar, label, phrase.Confidence);
            }
        }

        if (current is not null)
            sections.Add(current with
            {
                Confidence = confidenceCount > 0 ? confidenceSum / confidenceCount : 1.0,
            });

        return sections.ToArray();
    }

    private static SourceLoopEvidence CaptureSourceLoopEvidence(
        VisualizationTimeline timeline)
    {
        LoopMarker[] markers = (timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            .Where(marker => marker is not null)
            .OrderBy(marker => marker.SamplePosition)
            .ThenBy(marker => marker.Iteration)
            .ToArray();
        long? entry = markers
            .FirstOrDefault(marker => marker.Kind == LoopMarkerKind.Start)
            ?.SamplePosition;
        long[] restarts = markers
            .Where(marker => marker.Kind == LoopMarkerKind.Restart)
            .Select(marker => marker.SamplePosition)
            .Distinct()
            .ToArray();
        return new SourceLoopEvidence(entry, restarts);
    }

    private static RepeatedBlock[]? DetectSourceLoop(
        MusicalTimeMap map,
        SourceLoopEvidence evidence,
        BarFeature[] bars)
    {
        if (bars.Length < 4)
            return null;

        double quartersPerBar = bars[0].QuarterEnd - bars[0].QuarterStart;
        // Anchor tiers: source-restart periods (2) > single-restart boundary
        // inference (1) > content-only repeats near a source period (0). Anchored
        // candidates must outrank content-only ones so coincidental sub-loops of a
        // validated restart period never displace the boundary-anchored loop.
        var candidates = new List<(int Anchor, RepeatedBlock Block)>();
        foreach ((long start, long end) in SourcePeriods(evidence))
        {
            double periodExact = PeriodBarsExact(map, start, end, bars);
            int period = (int)Math.Round(periodExact, MidpointRounding.AwayFromZero);
            int startBar = (int)Math.Floor(
                (map.SampleToQuarterPosition(start) - bars[0].QuarterStart)
                / quartersPerBar);
            if (period < 2 || period > MaxRepeatPeriodBars
                || startBar < 0
                || Math.Abs(periodExact - period) > 0.25)
            {
                continue;
            }

            if (startBar + period * 2 > bars.Length)
            {
                // Boundary-only loop: the second occurrence lies outside (or only
                // partially inside) the capture, so content validation cannot
                // run. Never fabricate Similarity/Coverage — emit only when both
                // boundaries and the period land on clean values, and mark the
                // block unvalidated.
                double boundaryError = Math.Max(
                    BoundaryBarError(map, start, bars),
                    BoundaryBarError(map, end, bars));
                if (boundaryError <= RestartBoundaryErrorLimit
                    && Math.Abs(periodExact - period) <= RestartBoundaryErrorLimit)
                {
                    candidates.Add((2, new RepeatedBlock(
                        startBar,
                        period,
                        RepeatCount: 1,
                        Similarity: 0,
                        SpanCoverage: Math.Clamp((double)period / bars.Length, 0.0, 1.0),
                        MaterialCoverage: 0,
                        SourceSupported: true,
                        ContentValidated: false,
                        BoundaryErrorBars: boundaryError)));
                }
                continue;
            }

            // Content validation can run: require real similarity anchored at the
            // source boundary instead of fabricating a perfect block. The block
            // extends block1-block2, block1-block3, ... until the first failing
            // pair, so a source period that repeats three or more times reports
            // the full RepeatCount instead of a truncated two-pass span.
            RepeatedBlock? measured = MeasureRepeatedBlock(bars, startBar, period);
            if (measured is not null)
            {
                double boundaryError = Math.Max(
                    BoundaryBarError(map, start, bars),
                    BoundaryBarError(map, end, bars));
                candidates.Add((2, measured with
                {
                    SourceSupported = true,
                    ContentValidated = true,
                    BoundaryErrorBars = boundaryError,
                }));
            }
        }

        // Single-restart inference (no entry marker): content on both sides of a
        // restart boundary validates an inferred loop period.
        RestartBoundaryEvidence? restartEvidence = CaptureRestartBoundaryEvidence(map, evidence, bars);
        if (restartEvidence is not null)
        {
            candidates.Add((1, new RepeatedBlock(
                restartEvidence.InferredStartBar,
                restartEvidence.PeriodBars,
                RepeatCount: 2,
                restartEvidence.Similarity,
                SpanCoverage: Math.Clamp(
                    (2.0 * restartEvidence.PeriodBars) / bars.Length, 0.0, 1.0),
                restartEvidence.MaterialCoverage,
                SourceSupported: true,
                ContentValidated: true,
                BoundaryErrorBars: BoundaryBarError(map, restartEvidence.RestartSample, bars))));
        }

        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        (long Start, long End)[] sourcePeriods = SourcePeriods(evidence).ToArray();
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? candidate = FindBestRepeatedBlock(bars, period);
            if (candidate is null)
                continue;
            if (sourcePeriods.Length == 0)
            {
                candidates.Add((0, candidate));
                continue;
            }
            double sourceDistance = sourcePeriods
                .Select(value => PeriodBarsExact(map, value.Start, value.End, bars))
                .Min(value => Math.Abs(value - period));
            if (sourceDistance <= 1.0)
                candidates.Add((0, candidate with { SourceSupported = true }));
        }

        // No valid source-supported candidate survived: fall back to pure
        // content-only detection in Analyze (the caller's ?? DetectRepeatedBlocks).
        if (candidates.Count == 0)
            return null;

        // Spec P0-6 ranking: content-validated source loops, then source
        // boundary-supported loops, then loopScore (fundamental period preferred
        // within tolerance), then span coverage, then earlier start.
        return candidates
            .OrderByDescending(entry => entry.Anchor)
            .ThenByDescending(entry => entry.Block.ContentValidated)
            .ThenByDescending(entry => entry.Block.SourceSupported)
            .ThenBy(entry => entry.Block, Comparer<RepeatedBlock>.Create(CompareBlocks))
            .Take(1)
            .Select(entry => entry.Block)
            .ToArray();
    }

    /// <summary>
    /// Infers the best single-restart loop for the map's grid: the restart must
    /// fall within <see cref="RestartBoundaryErrorLimit"/> bars of a bar boundary,
    /// and for some period 2..64 the spans [restartBar-period, restartBar) and
    /// [restartBar, restartBar+period) must both lie fully inside the capture and
    /// match in corresponding-bar order. A restart marker alone never establishes
    /// a loop — content on both sides must validate it. Null when the map has no
    /// downbeat/meter or no restart qualifies.
    /// </summary>
    private static RestartBoundaryEvidence? CaptureRestartBoundaryEvidence(
        MusicalTimeMap map,
        SourceLoopEvidence evidence,
        BarFeature[] bars)
    {
        if (evidence.EntrySample is not null
            || evidence.RestartSamples.Count == 0
            || map.Meter is not Meter meter
            || meter.QuartersPerBar <= 0
            || map.FirstDownbeatQuarter is not double downbeat
            || bars.Length == 0)
        {
            return null;
        }

        double quartersPerBar = meter.QuartersPerBar;
        // The restart-to-restart source span is the strongest prior for the
        // inferred period: coincidental sub-periods (e.g. modulo-12 pitch
        // coincidences) must not displace the period the markers actually assert.
        double[] sourcePeriodBars = SourcePeriods(evidence)
            .Select(value => PeriodBarsExact(map, value.Start, value.End, bars))
            .Where(value => value > 0)
            .ToArray();
        RestartBoundaryEvidence? best = null;
        foreach (long restart in evidence.RestartSamples)
        {
            double restartBarExact =
                (map.SampleToQuarterPosition(restart) - downbeat) / quartersPerBar;
            int restartBar = (int)Math.Round(restartBarExact, MidpointRounding.AwayFromZero);
            double boundaryError = Math.Abs(restartBarExact - restartBar);
            if (boundaryError > RestartBoundaryErrorLimit)
                continue;

            for (int period = 2; period <= MaxRepeatPeriodBars; period++)
            {
                int leftStart = restartBar - period;
                int rightEnd = restartBar + period;
                if (leftStart < 0 || rightEnd > bars.Length)
                    continue;
                double similarity = BlockSimilarity(bars, leftStart, restartBar, period);
                double materialCoverage = BlockCoverage(bars, leftStart, restartBar, period);
                if (similarity < SingleRestartSimilarityThreshold
                    || materialCoverage < SingleRestartMaterialCoverage)
                {
                    continue;
                }

                double boundaryFit = Math.Exp(
                    -(boundaryError * boundaryError) / (2 * RestartBoundarySigmaBars * RestartBoundarySigmaBars));
                var candidate = new RestartBoundaryEvidence(
                    restart,
                    restartBar,
                    period,
                    InferredStartBar: leftStart,
                    boundaryFit,
                    similarity,
                    SpanCoverage: 1.0,
                    materialCoverage,
                    BoundaryErrorBars: boundaryError);
                if (best is null || IsBetterRestartEvidence(candidate, best, sourcePeriodBars))
                    best = candidate;
            }
        }
        return best;
    }

    private static bool IsBetterRestartEvidence(
        RestartBoundaryEvidence candidate,
        RestartBoundaryEvidence current,
        IReadOnlyList<double> sourcePeriodBars)
    {
        bool candidateMatchesSource = MatchesSourcePeriod(candidate.PeriodBars, sourcePeriodBars);
        bool currentMatchesSource = MatchesSourcePeriod(current.PeriodBars, sourcePeriodBars);
        if (candidateMatchesSource != currentMatchesSource)
            return candidateMatchesSource;

        double candidateScore = candidate.Similarity * candidate.MaterialCoverage;
        double currentScore = current.Similarity * current.MaterialCoverage;
        if (candidateScore != currentScore)
            return candidateScore > currentScore;
        if (candidate.PeriodBars != current.PeriodBars)
            return candidate.PeriodBars > current.PeriodBars;
        return candidate.InferredStartBar < current.InferredStartBar;
    }

    /// <summary>True when the inferred period is within rounding tolerance of a
    /// restart-to-restart source span, in bars.</summary>
    private static bool MatchesSourcePeriod(int period, IReadOnlyList<double> sourcePeriodBars)
    {
        foreach (double source in sourcePeriodBars)
        {
            if (Math.Abs(source - period) <= 0.25)
                return true;
        }
        return false;
    }

    /// <summary>Distance of a sample's bar position from the nearest integer bar, in bars.</summary>
    private static double BoundaryBarError(MusicalTimeMap map, long sample, BarFeature[] bars)
    {
        double quartersPerBar = bars[0].QuarterEnd - bars[0].QuarterStart;
        double barExact = (map.SampleToQuarterPosition(sample) - bars[0].QuarterStart)
            / quartersPerBar;
        return Math.Abs(barExact - Math.Round(barExact, MidpointRounding.AwayFromZero));
    }

    private static double PeriodBarsExact(
        MusicalTimeMap map,
        long startSample,
        long endSample,
        BarFeature[] bars)
    {
        if (endSample <= startSample || bars.Length == 0)
            return 0;
        double quarters = map.SampleToQuarterPosition(endSample)
            - map.SampleToQuarterPosition(startSample);
        double quartersPerBar = bars[0].QuarterEnd - bars[0].QuarterStart;
        return quartersPerBar > 0 ? quarters / quartersPerBar : 0;
    }

    private static RepeatedBlock[] DetectRepeatedBlocks(BarFeature[] bars)
    {
        var loops = new List<RepeatedBlock>();
        int maxPeriod = Math.Min(MaxRepeatPeriodBars, bars.Length / 2);
        for (int period = 2; period <= maxPeriod; period++)
        {
            RepeatedBlock? candidate = FindBestRepeatedBlock(bars, period);
            if (candidate is not null)
                loops.Add(candidate);
        }

        return loops
            .OrderByDescending(loop => loop.SourceSupported)
            .ThenBy(loop => loop, Comparer<RepeatedBlock>.Create(CompareBlocks))
            .ToArray();
    }

    private static RepeatedBlock? FindBestRepeatedBlock(BarFeature[] bars, int period)
    {
        if (period < 2 || bars.Length < period * 2)
            return null;

        RepeatedBlock? best = null;
        for (int start = 0; start + period * 2 <= bars.Length; start++)
        {
            RepeatedBlock? candidate = MeasureRepeatedBlock(bars, start, period);
            if (candidate is null)
                continue;
            if (best is null || BlockOutranks(candidate, best))
                best = candidate;
        }
        return best;
    }

    /// <summary>
    /// Measures one candidate repeated block at <paramref name="start"/> with the
    /// given <paramref name="period"/>. The anchor span is compared against each
    /// following occurrence in order (block1-block2, then block1-block3, ...) and
    /// extension stops at the first pair whose corresponding-bar similarity falls
    /// below <see cref="SimilarityThreshold"/>. <see cref="RepeatedBlock.RepeatCount"/>
    /// counts the validated occurrences; SpanCoverage and MaterialCoverage follow
    /// the spec definitions (RepeatCount*LengthBars over the total bar count;
    /// barsWithMaterial over the compared bars). Null when any rejection gate fails
    /// (RepeatCount&lt;2, Similarity&lt;0.85, MaterialCoverage&lt;0.40,
    /// SpanCoverage&lt;0.25).
    /// </summary>
    private static RepeatedBlock? MeasureRepeatedBlock(BarFeature[] bars, int start, int period)
    {
        int repeatCount = 1;
        double similarityTotal = 0;
        int comparedPairs = 0;
        for (int occurrence = 1; ; occurrence++)
        {
            int rightStart = start + occurrence * period;
            if (rightStart + period > bars.Length)
                break;
            double pairSimilarity = BlockSimilarity(bars, start, rightStart, period);
            if (pairSimilarity < SimilarityThreshold)
                break;
            repeatCount = occurrence + 1;
            similarityTotal += pairSimilarity;
            comparedPairs++;
        }
        if (repeatCount < 2)
            return null;

        int comparedBars = repeatCount * period;
        double similarity = similarityTotal / comparedPairs;
        double materialCoverage = SpanMaterialCoverage(bars, start, comparedBars);
        double spanCoverage = (double)comparedBars / bars.Length;
        if (similarity < SimilarityThreshold
            || materialCoverage < RepeatedMaterialCoverageThreshold
            || spanCoverage < RepeatedSpanCoverageThreshold)
        {
            return null;
        }
        return new RepeatedBlock(
            start,
            period,
            repeatCount,
            similarity,
            spanCoverage,
            materialCoverage);
    }

    /// <summary>Share of bars carrying material across the span [start, start+length).</summary>
    private static double SpanMaterialCoverage(BarFeature[] bars, int start, int length)
    {
        int material = 0;
        for (int offset = 0; offset < length; offset++)
        {
            if (!bars[start + offset].IsRest)
                material++;
        }
        return (double)material / length;
    }

    private static double BlockCoverage(
        BarFeature[] bars,
        int left,
        int right,
        int length)
    {
        int material = 0;
        for (int offset = 0; offset < length; offset++)
        {
            if (!bars[left + offset].IsRest || !bars[right + offset].IsRest)
                material++;
        }
        return (double)material / length;
    }

    private static double BlockSimilarity(BarFeature[] bars, int left, int right, int length)
    {
        double total = 0;
        bool hasMaterial = false;
        for (int offset = 0; offset < length; offset++)
        {
            BarFeature first = bars[left + offset];
            BarFeature second = bars[right + offset];
            if (!first.IsRest || !second.IsRest)
                hasMaterial = true;
            total += first.Similarity(second);
        }
        return hasMaterial ? total / length : 0;
    }

    private static string PhraseLabel(int distinctIndex)
        => distinctIndex < 26
            ? $"PHRASE_{(char)('A' + distinctIndex)}"
            : $"PHRASE_S{distinctIndex - 26}";
}