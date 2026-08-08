---
work_package_id: WP03
title: Fitting, Source Precedence & Tempo Segments
dependencies: []
requirement_refs:
- FR-001
- FR-002
- FR-003
- FR-005
planning_base_branch: feature/linux-fmp-renderer
merge_target_branch: feature/linux-fmp-renderer
branch_strategy: Planning artifacts for this mission were generated on feature/linux-fmp-renderer. During /spec-kitty.implement this WP may branch from a dependency-specific base, but completed changes must merge back into feature/linux-fmp-renderer unless the human explicitly redirects the landing branch.
subtasks:
- T011
- T012
- T013
- T014
- T015
- T016
- T017
- T018
- T019
- T020
phase: Phase 3 - Beat fitting
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Core/Timing/
create_intent:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatAnchor.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimeMapBuilderTests.cs
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatGridFitter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapOptions.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingDiagnostics.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingSource.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimingException.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatAnchor.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimeMapBuilderTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP03 – Fitting, Source Precedence & Tempo Segments

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

And this WP turns validated tempo transitions into continuous piecewise `TempoSegment`s (plan IC-03, spec §17, §18, §19, §9, §58–§61). When complete:

8. Each accepted sustained tempo transition creates a new segment beginning at the transition sample; the musical position at that sample is identical across both segments (continuous).
9. The next segment's `QuarterPositionAtStart` is threaded from the previous segment (double precision), never recalculated from rounded MIDI ticks.
10. Jitter-driven segment spam is suppressed: small timer/observation jitter does not spawn per-beat tempo segments (only real sustained changes do).
11. Validated driver tempo transitions are preferred when available; beat anchors verify phase/continuity.
12. `TempoSegment.MicrosecondsPerQuarter` is computed via `round(60_000_000 / BPM)` and validated as representable in the MIDI 24-bit value (spec §19, WP07 authorizes emission).
13. Tests cover notes/pitch/anchors before, at, and after a transition, plus the real tempo change (e.g. 120→150 BPM) and exact-boundary cases (§59–§61).

## Context & Constraints

- **Spec**: §5 (BeatIndex normalization), §10 (precedence), §11 (modes), §12 (BeatGridFitter), §13 (input validation), §14 (robust fit), §15 (diagnostics), §16 (strict mode).
- **Spec**: §9 (continuity), §17 (tempo changes vs jitter), §18 (transition requirements + boundary tests), §19 (µs/qn representation), §58–§61 (real change, note crossing, pitch at boundary).
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-02, IC-03.
- **Data model**: `data-model.md` §2 (`BeatAnchor`, options, `TempoSegment` continuity) and §3 (diagnostics, strict/non-strict).
- **Quickstart**: Batches C and D.
- **Constraint**: Do **not** rewrite `BeatGridFitter` wholesale (spec §12, §73). Harden only. Establish phase separately from tempo.
- **Constraint**: Do not emit hundreds of tempo events because source timer observations jitter slightly (§75). Prefer explicit validated driver tempo transitions.

## Grounded source evidence (verified 2026-08-08)

- `MusicalTimeMapOptions` (all init): `QuartersPerBeat` double=1, `FixedBpm` double?, `BeatOffsetQuarter` double?, `BeatOffsetSamples` long?, `Meter`?, `FirstDownbeatSample` long?, `Source` TimingSource?, `DetectTempoChanges` bool=false, `StrictTiming` bool=false.
- `TimingSource` enum: `DriverBeatAnchors`, `DriverValidatedTempo`, `SequencerClock`, `UserOverride`, `SymbolicInference`, `AudioInference`.
- `MusicalTimeMapBuilder.Build`: `BeatOffsetQuarter` wins; `BeatOffsetSamples` only >0 converted using `FixedBpm` or the first validated BPM; anchors built from `timeline.Beats` where finite `BeatIndex` & sample ≥ `timeline.StartSample` ⇒ `BeatIndex * QuartersPerBeat`; tempo changes from `DriverTimingEvent.ValidatedBpm` grouped by sample. Source resolution: forced `Source` first, else any `FixedBpm`/offset ⇒ `UserOverride`, ≥2 anchors ⇒ `DriverBeatAnchors`, first validated ⇒ `DriverValidatedTempo`, else `SymbolicInference`. `DriverBeatAnchors` calls `BeatGridFitter` with tempo changes/detection; other sources use a fixed grid from fixed/first validated BPM. No anchors / no explicit phase ⇒ `PhaseUnknown`, and `StrictTiming` throws. `AssembleMap` threads continuity.
- `BeatGridFitter.Fit`: validates finite/nondecreasing anchors, exact duplicate dedup, partitions explicit or detected changes. `RobustLinearFit` computes median adjacent slopes/intercept then LS up to 3 passes with 0.45-quarter sample tolerance; residual >0.45 quarter rejected; returns `PiecewiseFit { SegmentFit[], Diagnostics }`. Fixed BPM with 0 anchors = phase unknown unless an override; single anchor = phase inferred.
- `TimingDiagnostics` props: `AnchorCount`, `RejectedAnchorCount`, `SegmentCount`, `MaxResidualQuarters`, `RmsResidualQuarters`, `RmsResidualSamples`, `TempoSource`/`PhaseSource`, `TempoInferred`/`PhaseInferred`, `PhaseUnknown`, `SampleZeroQuarter?`, `Warnings`, `IsTrustworthy` (= !PhaseUnknown && !TempoInferred && !PhaseInferred && Rms<.25). This is a **data-only** record (spec §73).
- `MusicalTimingException` (internal): `InvalidOperationException` with 2 ctors.
- `TempoSegment` (record): `StartSample`, `EndSample` (exclusive), `QuarterPositionAtStart`, `SamplesPerQuarter`, `BeatsPerMinute`, `TimingSource`, `Confidence`; helpers `IsEmpty`, `MicrosecondsPerQuarter` (rounded/clamped), `QuarterPositionAtEnd`, `QuarterPositionAt` (inclusive).
- `MusicalTimeMapBuilder.Build` currently groups tempo changes from `DriverTimingEvent.ValidatedBpm` by sample; `DriverBeatAnchors` calls `BeatGridFitter` with tempo changes/detection; `AssembleMap` threads continuity.
- `BeatGridFitter` partitions explicit or detected changes via `RobustLinearFit` (median adjacent slopes then LS, 0.45-quarter tolerance).
- `TimingDiagnostics` exposes `SegmentCount`, `RmsResidualQuarters/Samples`, `TempoSource`, `TempoInferred`, authority flags.
- Existing tests in `MusicalTimingTests.cs` already build direct `BeatAnchor[]` + `BeatGridFitter.Fit(Sr)` for phase/outliers/jitter/segmentation. New builder-level tests go in the new `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimeMapBuilderTests.cs`.

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

### Subtask T016 – Integrate validated tempo transitions into segments

- **Purpose**: Real sustained tempo changes become distinct segments (spec §17, §18).
- **Steps**:
  1. Verify a transition at sample `S` (`old → new`) constructs a new segment beginning at `S`.
  2. Prefer explicit validated driver tempo transitions (`DriverTimingEvent.ValidatedBpm`) when available; use them as authoritative transition points.
  3. Add the §59 real tempo change test (120 BPM for 16 quarters → 150 BPM) asserting the required transition, continuous quarter position at the boundary, and a Set Tempo event at the correct tick (emission is WP05/WP07; here assert segment construction and continuity).
  4. Verify no individual beat arrival early/late creates a segment (§14 cross-check).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatGridFitter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes — core transition integration.
- **Notes**: The transition sample `S` is included in the new segment; musical position at `S` is identical across both segments.

### Subtask T017 – Preserve fitted phase through tempo changes

- **Purpose**: Tempo changes must not reset or shift phase (spec §20, §59 continuity; plan IC-03).
- **Steps**:
  1. Verify that after a tempo transition the fitted beat phase (intercept) carries forward unchanged.
  2. Add a regression: anchors establish phase; a later 120→150 BPM transition must keep `sample 12000 = quarter 0` (or whatever phase was fitted) on both sides.
  3. Ensure the new segment's origin is computed from the previous segment's `SampleToQuarterPosition(StartSample)`, not from `BPM × elapsed`.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/TempoSegment.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: No — depends on T016.
- **Notes**: This is the phase-preservation rule from §10 carried into tempo changes.

### Subtask T018 – Guarantee continuity; suppress jitter-driven segment spam

- **Purpose**: Adjacent segments are continuous in double precision; jitter never spawns segments (spec §9, §17).
- **Steps**:
  1. Assert `next.QuarterPositionAtStart == previous.SampleToQuarterPosition(next.StartSample)` within tolerance for every adjacent pair (reinforce WP02 T008 at the builder level).
  2. Add jitter suppression: many small BPM perturbations around one constant tempo must yield **one** segment (or the documented tolerance), never per-beat segments.
  3. Confirm segment construction uses absolute sample positions threaded forward, not accumulated intermediate quarter units.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: No — depends on T016/T017.
- **Notes**: Do not emit a MIDI tempo event for every small variation (spec §17, §75).

### Subtask T019 – Crossing-boundary tests

- **Purpose**: Events that cross or sit on a tempo boundary must remain correct and ordered (spec §18, §60, §61).
- **Steps**:
  1. §60 note crossing a tempo change: note `start = quarter 15.5`, `end = quarter 16.5`, tempo change at quarter 16 → assert start/end ticks derive independently from their source samples (protects against `endTick = startTick + fixedDuration`).
  2. §61 pitch at boundary: pitch change whose sample equals the tempo segment boundary → correct tick, stable same-tick ordering, no one-tick discontinuity.
  3. Add: note starts exactly at transition; note ends exactly after transition; beat anchor exactly at transition. All must remain correctly ordered (spec §18).
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs` (segments/ordering), and later `MusicalMidiExporterTests.cs` for tick-level assertions (coordinate with WP04).
- **Parallel?**: No — depends on T016–T018.
- **Notes**: Tempo at boundary must be the new tempo; events at that tick use the new timeline position (spec §35).

### Subtask T020 – µs/qn 24-bit representation validation

- **Purpose**: MIDI Set Tempo is `µs per quarter`; must be representable in 24 bits (spec §19).
- **Steps**:
  1. Verify `MicrosecondsPerQuarter` = `round(60_000_000 / BPM)` for each segment.
  2. Add a validation test for a value that would overflow/underflow the representable range (very fast/slow BPM) asserting rejection/a clear error rather than truncation.
  3. Ensure the exported tempo uses the validated µs/qn integer value; do not compare raw floating BPM for dedup (dedup of emitted values happens in WP05).
  4. Keep `TempoSegment`'s `SamplesPerQuarter`/`BeatsPerMinute` as the source of truth; µs/qn is a derived representation.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/TempoSegment.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes — independent of T016–T019.
- **Notes**: If a segment's µs/qn cannot be represented, fail with an actionable diagnostic (spec §76 good-message style), not silent clamp.

---

## Test Strategy

All timing/fitting tests belong in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs` using the existing direct-`BeatAnchor[]`→`BeatGridFitter.Fit(Sr)` convention, with builder-level tests in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimeMapBuilderTests.cs`. If a test asserts end-to-end origin behavior, place it in `MusicalMidiExporterTests.cs` (owned by WP04) and coordinate with WP04.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalTimingTests|FullyQualifiedName~MusicalTimeMapBuilderTests"
```

## Risks & Mitigations

- **Risk**: Averaging conflicting anchors. → Mitigation: T012 strict failure + diagnostic, never average.
- **Risk**: BPM destroying fitted phase. → Mitigation: T014 regression.
- **Risk**: Jitter → per-beat tempo segments. → Mitigation: T013 robust-fit test.
- **Risk**: Rewriting `BeatGridFitter`. → Mitigation: exercise discipline per spec §12.
- **Risk**: One tempo event per jitter beat. → Mitigation: T018 suppression + §17.
- **Risk**: Phase reset on tempo change. → Mitigation: T017 regression.
- **Risk**: Note crossing a transition gets wrong duration. → Mitigation: T019 §60 test.
- **Risk**: Unrepresentable tempo silently clamped. → Mitigation: T020 rejection.

## Review Guidance

- Verify source precedence and the "rate without destroying phase" rule (§10).
- Verify conflict → diagnostic + strict failure, never average (§13, §56).
- Verify jitter/outlier tests (§57/§58) and strict-mode set (§16).
- Verify `TimingDiagnostics` is data-only with per-reject reason.
- Verify continuity gained via threaded double-precision origins, not rounded tick origins.
- Verify §59–§61 tests exist and pass.
- Verify jitter suppression yields one segment.
- Verify µs/qn 24-bit validation rejects unrepresentable values.

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