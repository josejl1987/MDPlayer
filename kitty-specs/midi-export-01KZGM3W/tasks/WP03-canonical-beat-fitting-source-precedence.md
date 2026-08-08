---
work_package_id: WP03
title: Canonical Beat Fitting & Source Precedence
dependencies: []
requirement_refs:
- FR-001
- FR-002
- FR-003
- FR-005
subtasks:
- T011
- T012
- T013
- T014
- T015
phase: Phase 3 - Beat fitting
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Core/Timing/
create_intent:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatAnchor.cs
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatGridFitter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapOptions.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingDiagnostics.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingSource.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimingException.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP03 – Canonical Beat Fitting & Source Precedence

## ⚡ Do This First: Load Agent Profile

Use the `/ad-hoc-profile-load` skill to load the agent profile specified in the frontmatter (or any user-defined profile), and behave according to its guidance before parsing the rest of this prompt.

- **Profile**: `implementer-ivan`
- **Role**: `implementer`
- **Agent/tool**: `claude`

If no profile is specified, run `spec-kitty agent profile list` and select the best match for this work package's `task_type` and `authoritative_surface`.

---

## ⚠️ IMPORTANT: Review Feedback

**Read this first if you are implementing this task!**

- **Has review feedback?**: Check the `review_ref` field in the event log (via `spec-kitty agent status` or the Activity Log below).
- **You must address all feedback** before your work is complete. Feedback items are your implementation TODO list.
- **Report progress**: As you address each feedback item, update the Activity Log explaining what you changed.

---

## Review Feedback

*[If this WP was returned from review, the reviewer feedback reference appears in the Activity Log below or in the status event log.]*

---

## Markdown Formatting

Wrap HTML/XML tags in backticks: `` `<div>` ``, `` `<script>` ``
Use language identifiers in code blocks: ````csharp`,````bash`

---

## Objectives & Success Criteria

This WP hardens beat fitting and source precedence (plan IC-02, spec §10–§16, §12, §13, §14, §15). When complete:

1. `MusicalTimeMapBuilder` has explicit deterministic source precedence (§10): authoritative driver beat anchors → sequencer/driver clock → validated driver tempo → explicit user override → existing symbolic inference.
2. `BeatIndex` is normalized to quarter units **once** via an explicit scale in the builder (spec §5); `MusicalMidiExporter` never interprets `BeatIndex`.
3. Duplicate identical anchors are deduplicated harmlessly; conflicting anchors are reported (diagnostic) and fail in strict mode — never averaged (§13).
4. `BeatGridFitter` robust fit rejects severe outliers, keeps tempo stable under ±small jitter (one stable segment, no tempo event per beat), and reports residuals (§14).
5. Phase is established from authoritative anchors; validated BPM may establish/validate rate **without destroying** the fitted phase (§10 "Important combination rule").
6. Strict mode fails on fundamental ambiguity (incompatible anchors, unknown beat-unit conversion, unresolved phase, pathological fit, invalid tempo, impossible segment order) and never silently falls back to 120 BPM (§16).
7. Timing diagnostics expose accepted/rejected anchor counts, rejected-anchor sample/beat/residual/reason, estimated BPM, segment count, RMS/max residual, phase/intercept, authority flags, meter/downbeat status, and warnings (§15).

## Context & Constraints

- **Spec**: §5 (BeatIndex normalization), §10 (precedence), §11 (modes), §12 (BeatGridFitter), §13 (input validation), §14 (robust fit), §15 (diagnostics), §16 (strict mode).
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-02.
- **Data model**: `data-model.md` §2 (`BeatAnchor`, options) and §3 (diagnostics, strict/non-strict).
- **Quickstart**: Batch C.
- **Constraint**: Do **not** rewrite `BeatGridFitter` wholesale (spec §12, §73). Harden only. Establish phase separately from tempo.

## Grounded source evidence (verified 2026-08-08)

- `MusicalTimeMapOptions` (all init): `QuartersPerBeat` double=1, `FixedBpm` double?, `BeatOffsetQuarter` double?, `BeatOffsetSamples` long?, `Meter`?, `FirstDownbeatSample` long?, `Source` TimingSource?, `DetectTempoChanges` bool=false, `StrictTiming` bool=false.
- `TimingSource` enum: `DriverBeatAnchors`, `DriverValidatedTempo`, `SequencerClock`, `UserOverride`, `SymbolicInference`, `AudioInference`.
- `MusicalTimeMapBuilder.Build`: `BeatOffsetQuarter` wins; `BeatOffsetSamples` only >0 converted using `FixedBpm` or the first validated BPM; anchors built from `timeline.Beats` where finite `BeatIndex` & sample ≥ `timeline.StartSample` ⇒ `BeatIndex * QuartersPerBeat`; tempo changes from `DriverTimingEvent.ValidatedBpm` grouped by sample. Source resolution: forced `Source` first, else any `FixedBpm`/offset ⇒ `UserOverride`, ≥2 anchors ⇒ `DriverBeatAnchors`, first validated ⇒ `DriverValidatedTempo`, else `SymbolicInference`. `DriverBeatAnchors` calls `BeatGridFitter` with tempo changes/detection; other sources use a fixed grid from fixed/first validated BPM. No anchors / no explicit phase ⇒ `PhaseUnknown`, and `StrictTiming` throws. `AssembleMap` threads continuity; `FirstDownbeatSample` + `Meter` snaps to nearest bar.
- `BeatGridFitter.Fit`: validates finite/nondecreasing anchors, exact duplicate dedup, partitions explicit or detected changes. `RobustLinearFit` computes median adjacent slopes/intercept then LS up to 3 passes with 0.45-quarter sample tolerance; residual >0.45 quarter rejected; returns `PiecewiseFit { SegmentFit[], Diagnostics }`. Fixed BPM with 0 anchors = phase unknown unless an override; single anchor = phase inferred.
- `TimingDiagnostics` props: `AnchorCount`, `RejectedAnchorCount`, `SegmentCount`, `MaxResidualQuarters`, `RmsResidualQuarters`, `RmsResidualSamples`, `TempoSource`/`PhaseSource`, `TempoInferred`/`PhaseInferred`, `PhaseUnknown`, `SampleZeroQuarter?`, `Warnings`, `IsTrustworthy` (= !PhaseUnknown && !TempoInferred && !PhaseInferred && Rms<.25). This is a **data-only** record (spec §73).
- `MusicalTimingException` (internal): `InvalidOperationException` with 2 ctors.
- Existing tests in `MusicalTimingTests.cs` already build direct `BeatAnchor[]` + `BeatGridFitter.Fit(Sr)` for phase/outliers/jitter/segmentation.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

### Subtask T011 – Normalize BeatIndex → quarter once in the builder

- **Purpose**: Ensure the driver beat unit never leaks into MIDI code (spec §5 normalization rule).
- **Steps**:
  1. Confirm the builder's current `BeatIndex * QuartersPerBeat` normalization is the single conversion point.
  2. If any other code multiplies/derives quarter fractions from `BeatIndex` (search the whole branch), route it through the builder's normalized path.
  3. Add a test asserting that a `QuartersPerBeat` scale (e.g. 0.5 for half-beats or 2 for subdivisions) produces the correct quarter grid.
  4. Assert `MusicalMidiExporter` (later WPs) never interprets `BeatIndex` — this WP just locks the builder boundary.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes — isolated from T012/T013.
- **Notes**: The scale factor lives in `MusicalTimeMap` construction only, per spec.

### Subtask T012 – Duplicate/conflict anchor handling + BeatAnchor type

- **Purpose**: Deduplicate identical anchors harmlessly; never average conflicting ones (spec §13, tests §55/§56).
- **Steps**:
  1. If `BeatAnchor` is not already a named type, add `MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatAnchor.cs` with `(Sample, QuarterPosition, Confidence?)`.
  2. Verify anchors are sorted by sample then deterministic quarter key before fitting.
  3. Verify exact same-sample/same-quarter duplicates deduplicate harmlessly (add test: `sample 24000/beat 1` twice → no distortion).
  4. For same-sample conflicting quarters (`beat 1` vs `beat 2`), emit a diagnostic conflict and, in strict mode, throw (`MusicalTimingException`) — never average. Add the §56 test.
  5. Reject non-finite positions and impossible backward motion (except documented loop resets, which are normalized in T015/§39).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatAnchor.cs` (new), `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatGridFitter.cs`.
- **Parallel?**: Yes.
- **Notes**: Loop-reset normalization belongs here/once, not scattered through MIDI code (spec §13, §39).

### Subtask T013 – Robust-fit hardening: outliers, residuals, no per-beat segments

- **Purpose**: Small timestamp jitter must not create unstable BPM or per-beat tempo segments (spec §14, tests §57/§58).
- **Steps**:
  1. Run the beat-jitter test: a long set of anchors around constant tempo with ±2 samples noise → assert one stable tempo segment, BPM near truth, no tempo event per beat, low residual.
  2. Run the outlier test: one severe bad beat timestamp → assert the robust fitter rejects it, resulting BPM stays close to correct, and the rejected anchor appears in diagnostics; strict behaviour follows configured residual policy.
  3. If the 0.45-quarter tolerance or pass-count needs adjusting to satisfy these, make a targeted correction (do not rewrite the fitter).
  4. Ensure the fitter does not create a new tempo segment merely because one beat arrives a few samples early.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatGridFitter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes.
- **Notes**: Harden, don't replace. No new fitting framework (spec §12).

### Subtask T014 – Establish phase from anchors; rate from validated BPM without destroying phase

- **Purpose**: Beat anchors give rate + phase; BPM alone gives rate (spec §10 combination rule; §20 beat phase).
- **Steps**:
  1. Verify the builder fits anchors to establish both intercept (phase) and slope (rate) for `DriverBeatAnchors`.
  2. Verify that when `ValidatedBpm` is present, it validates/establishes segment rate but does **not** rebuild the map from BPM starting at quarter zero (which would destroy alignment).
  3. Add a regression test: anchors establish `sample 12000 = quarter 0`; adding `ValidatedBpm 120` must NOT shift that phase to sample 0 = quarter 0.
  4. Keep `SampleZeroQuarter?` in diagnostics reflecting the true phase (may be negative/fractional).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes.
- **Notes**: Never do: `fit beat phase → see ValidatedBpm → rebuild map from BPM at quarter zero` (spec forbids).

### Subtask T015 – Strict-mode failures + rejected-anchor diagnostics

- **Purpose**: Strict mode fails on any fundamental ambiguity; diagnostics expose rejection detail (spec §15, §16).
- **Steps**:
  1. Add strict-mode tests that fail export for: incompatible beat anchors, unknown beat-unit conversion, unresolved beat phase when alignment is required, pathological fit residual, invalid tempo, impossible tempo segment ordering, conflicting duplicate anchors.
  2. Ensure strict mode never silently falls back to 120 BPM (§16).
  3. Ensure each rejected anchor records `sample`, `beat position`, `residual`, and `reason` (spec §15). `TimingDiagnostics` is data-only; add fields if missing but keep it formatting-free.
  4. Verify `non-strict` fallback (if retained) is explicit in `Warnings` and never labeled "beat aligned" (spec §49).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingDiagnostics.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: No — depends on T011–T014.
- **Notes**: Strict failures throw `MusicalTimingException` (reused by WP08 CLI exit codes).

---

## Test Strategy

All timing tests belong in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs` using the existing direct-`BeatAnchor[]`→`BeatGridFitter.Fit(Sr)` convention. If a test asserts end-to-end origin behavior, place it in `MusicalMidiExporterTests.cs` and coordinate with WP05.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalTimingTests"
```

## Risks & Mitigations

- **Risk**: Averaging conflicting anchors. → Mitigation: T012 strict failure + diagnostic, never average.
- **Risk**: BPM destroying fitted phase. → Mitigation: T014 regression.
- **Risk**: Jitter → per-beat tempo segments. → Mitigation: T013 robust-fit test.
- **Risk**: Rewriting `BeatGridFitter`. → Mitigation: exercise discipline per spec §12.

## Review Guidance

- Verify source precedence and the "rate without destroying phase" rule (§10).
- Verify conflict → diagnostic + strict failure, never average (§13, §56).
- Verify jitter/outlier tests (§57/§58) and strict-mode set (§16).
- Verify `TimingDiagnostics` is data-only with per-reject reason.

## Activity Log

> **CRITICAL**: Activity log entries MUST be in chronological order (oldest first, newest last).

### How to Add Activity Log Entries

**When adding an entry**:

1. Scroll to the bottom of this Activity Log section
2. **APPEND the new entry at the END** (do NOT prepend or insert in middle)
3. Use exact format: `- YYYY-MM-DDTHH:MM:SSZ – agent_id – <action>`
4. Timestamp MUST be current time in UTC (check with `date -u "+%Y-%m-%dT%H:%M:%SZ"`)
5. Agent ID should identify who made the change (claude-sonnet-4-5, codex, etc.)

**Initial entry**:

- 2026-08-08T12:37:11Z – system – Prompt created.

---

### Updating Status

Status is managed via `status.events.jsonl`. Use `spec-kitty agent tasks move-task <WPID> --to <status>` to change WP status.

### Optional Phase Subdirectories

For large features, organize prompts under `tasks/` to keep bundles grouped while maintaining lexical ordering.