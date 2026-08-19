#nullable enable

namespace Fmp.Core.Timing;

/// <summary>A single rejected beat anchor and why it was not trusted.</summary>
internal sealed record RejectedAnchorInfo(
    long Sample,
    double QuarterPosition,
    double ResidualQuarters,
    string Reason);

/// <summary>
/// Per-signal breakdown of one grid candidate's structural score. Patch 1
/// observability: fields the current <c>GridScore</c> does not carry yet default
/// to 0/null until Patch 3 restructures scoring — never fabricating values.
/// Patch 8A adds the concrete evidence fields the gate needs to be explainable:
/// the restart boundary error/period and the repeated-block search outcome
/// (start/length/count) behind <see cref="RepeatedContentFit"/>.
/// </summary>
internal sealed record GridScoreBreakdown(
    double CandidatePrior,
    double OnsetFit,
    double? RhythmRoleFit,
    double? RestartBoundaryFit,
    double? RepeatedContentFit,
    double? PhraseRegularityFit,
    double CombinedScore,
    int KnownRhythmRoleHits,
    int DistinctRhythmRoles,
    double SpanCoverage,
    double MaterialCoverage,
    double? RestartBoundaryErrorBars = null,
    int? RestartPeriodBars = null,
    int? RepeatStartBar = null,
    int? RepeatLengthBars = null,
    int? RepeatCount = null);

/// <summary>
/// Observable snapshot of one grid candidate at selection time. Patch 8A expands
/// the receipt with the separate margins (tempo / meter / downbeat are never
/// merged), the per-candidate gate flags, and explicit rejection reasons — a
/// candidate is never dismissed with a bare "no-gate-evidence".
/// </summary>
internal sealed record GridCandidateReceipt(
    double Bpm,
    Meter Meter,
    double QuarterAtSourceStart,
    double? FirstDownbeatQuarter,
    double CandidatePrior,
    double CombinedScore,
    GridScoreBreakdown? Breakdown,
    double? TempoMargin = null,
    double? MeterMargin = null,
    double? DownbeatMargin = null,
    bool GateByRestart = false,
    bool GateByRepetition = false,
    bool GateByRhythm = false,
    IReadOnlyList<string>? RejectionReasons = null);

/// <summary>
/// Outcome of the structural grid-selection pass: what was attempted, what
/// resolved, why it was rejected, and the winning breakdown. Observability only —
/// recording this must never alter selection behavior or thresholds.
/// </summary>
internal sealed record GridSelectionDiagnostics
{
    public bool Attempted { get; init; }
    public bool TempoResolved { get; init; }
    public bool MeterResolved { get; init; }
    public bool DownbeatResolved { get; init; }
    public double? SelectedBpm { get; init; }
    public Meter? SelectedMeter { get; init; }
    public double? SelectedDownbeatQuarter { get; init; }
    public double WinnerScore { get; init; }
    public double TempoMargin { get; init; }
    public double MeterMargin { get; init; }
    public double DownbeatMargin { get; init; }
    public GridScoreBreakdown? Breakdown { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<GridCandidateReceipt> TopCandidates { get; init; }
}

/// <summary>
/// The outcome of a beat-grid fit, exposed so timing consumers can report — never
/// hide — how confident it is and what it had to infer. Phase and tempo carry
/// separate confidence because "BPM known" does not imply "grid known".
/// </summary>
internal sealed class TimingDiagnostics
{
    public TimingDiagnostics()
    {
        Warnings = new List<string>();
    }

    public int AnchorCount { get; set; }

    public int RejectedAnchorCount { get; set; }

    public int SegmentCount { get; set; }

    /// <summary>Maximum absolute residual of a retained anchor, in quarters.</summary>
    public double MaxResidualQuarters { get; set; }

    /// <summary>RMS residual of retained anchors, in quarters.</summary>
    public double RmsResidualQuarters { get; set; }

    /// <summary>RMS residual of retained anchors, in samples.</summary>
    public double RmsResidualSamples { get; set; }

    /// <summary>Maximum absolute residual of a retained anchor, in samples.</summary>
    public double MaxResidualSamples { get; set; }

    public TimingSource TempoSource { get; set; }

    public TimingSource PhaseSource { get; set; }

    /// <summary>Raw beat anchors fed to the fitter before any validation.</summary>
    public int RawAnchorCount { get; set; }

    /// <summary>Estimated BPM for the single dominant tempo, when determinable.</summary>
    public double? EstimatedBpm { get; set; }

    /// <summary>Encoded MIDI Set Tempo value for the selected quarter duration.</summary>
    public int? TempoMicrosecondsPerQuarter { get; set; }

    /// <summary>Meter status: known vs unknown.</summary>
    public bool MeterKnown { get; set; }

    /// <summary>Downbeat status: resolved vs unknown/locked.</summary>
    public bool DownbeatKnown { get; set; }

    private readonly List<RejectedAnchorInfo> _rejectedAnchors = new();

    /// <summary>Per-anchor rejection detail (sample, quarter, residual, reason).</summary>
    public IReadOnlyList<RejectedAnchorInfo> RejectedAnchors => _rejectedAnchors;

    /// <summary>Records a rejected anchor with its reason.</summary>
    public void AddRejectedAnchor(BeatAnchor anchor, double residualQuarters, string reason) =>
        _rejectedAnchors.Add(new RejectedAnchorInfo(anchor.Sample, anchor.QuarterPosition, residualQuarters, reason));

    /// <summary>
    /// Clears residual + rejected-anchor state accumulated from a discarded
    /// validated-tempo/alternative fit. Used when an anchors-first override
    /// rebuilds the map (§10): the stale residual/rejection diagnostics from the
    /// thrown-away fit must not poison IsTrustworthy / strict-mode gate.
    /// </summary>
    public void ResetAnchorDiagnostics()
    {
        _rejectedAnchors.Clear();
        RejectedAnchorCount = 0;
        MaxResidualQuarters = 0;
        RmsResidualQuarters = 0;
        RmsResidualSamples = 0;
        MaxResidualSamples = 0;
    }

    /// <summary>True when the phase is authoritative (anchors or explicit override), not inferred.</summary>
    public bool PhaseAuthoritative { get; set; }

    /// <summary>True when the tempo is authoritative (driver-validated or user override).</summary>
    public bool TempoAuthoritative { get; set; }

    /// <summary>True when two anchors claimed the same sample with different quarter positions.</summary>
    public bool HasConflictingAnchors { get; set; }

    /// <summary>
    /// True when inferred tempo is materially ambiguous (e.g. half/double-tempo
    /// alternative competes with the selected BPM, §42). Only meaningful for the
    /// symbolic-inference path.
    /// </summary>
    public bool TempoAmbiguous { get; set; }

    /// <summary>Selected BPM from symbolic inference (Patch D diagnostics).</summary>
    public double? SelectedBpm { get; set; }

    /// <summary>Nearest half/double-tempo alternative BPM, when ambiguous (Patch D).</summary>
    public double? AlternativeBpm { get; set; }

    /// <summary>Normalized onset+subdivision score of the selected BPM (0..1, Patch D).</summary>
    public double? SelectedScore { get; set; }

    /// <summary>Normalized score of the alternative BPM (Patch D).</summary>
    public double? AlternativeScore { get; set; }

    /// <summary>Confidence derived from absolute normalized fit AND alias margin (Patch D).</summary>
    public double? TempoConfidence { get; set; }

    /// <summary>Sample where quarter 0 occurs (negative => pickup).</summary>
    public long? PhaseSample { get; set; }

    /// <summary>Smallest recurring symbolic timing quantum, in source samples.</summary>
    public double? TatumDurationSamples { get; set; }

    /// <summary>Alias used by timing reports and tests for the source tatum duration.</summary>
    public double? TatumDuration => TatumDurationSamples;

    /// <summary>Number of inferred tatums in one musical beat.</summary>
    public int? TatumsPerBeat { get; set; }

    /// <summary>Inferred musical beat duration, in source samples.</summary>
    public double? BeatDurationSamples { get; set; }

    /// <summary>Alias used by timing reports and tests for the source beat duration.</summary>
    public double? BeatDuration => BeatDurationSamples;

    /// <summary>Source sample of the selected beat boundary phase.</summary>
    public long? BeatPhaseSample { get; set; }

    /// <summary>Alias used by timing reports and tests for beat phase.</summary>
    public long? BeatPhase => BeatPhaseSample;

    /// <summary>Normalized confidence of the tatum/beat hierarchy decision.</summary>
    public double? MetricalConfidence { get; set; }

    /// <summary>Combined metrical salience score of the selected beat level.</summary>
    public double? MetricalScore { get; set; }

    /// <summary>Beat number within the inferred meter, when a downbeat is known.</summary>
    public int? DownbeatPhase { get; set; }

    /// <summary>True when tempo had to be estimated (not driver-validated and not user override).</summary>
    public bool TempoInferred => TempoSource is TimingSource.SymbolicInference or TimingSource.AudioInference;

    /// <summary>True when the phase (sample-zero quarter position) had to be estimated.</summary>
    public bool PhaseInferred => PhaseSource is TimingSource.SymbolicInference or TimingSource.AudioInference;

    /// <summary>True when phase is unknown and there was no override to settle it.</summary>
    public bool PhaseUnknown { get; set; }

    /// <summary>Absolute quarter position of sample zero as fitted.</summary>
    public double? SampleZeroQuarter { get; set; }

    /// <summary>
    /// Score margin between the selected tempo and its nearest half/double
    /// alternative: SelectedScore - AlternativeScore when both exist, else null.
    /// Pure derivation from existing fields (FR-9 / request 21: never fabricate a
    /// margin — no invented 1.0).
    /// </summary>
    public double? AliasMargin =>
        SelectedScore is double selected && AlternativeScore is double alternative
            ? selected - alternative
            : null;

    public List<string> Warnings { get; }

    /// <summary>
    /// Outcome of the structural grid-selection pass (Patch 1 observability).
    /// Null when grid selection was never attempted (no symbolic candidates,
    /// no material, or no finite-score candidate).
    /// </summary>
    public GridSelectionDiagnostics? GridSelection { get; set; }

    /// <summary>
    /// True when the fit should be relied on for grid alignment: phase resolved and
    /// authoritative, no tempo guess, no unresolved conflict, and residuals small.
    /// </summary>
    public bool IsTrustworthy =>
        !PhaseUnknown
        && !HasConflictingAnchors
        && !TempoInferred
        && !PhaseInferred
        && RmsResidualQuarters < 0.25;
}
