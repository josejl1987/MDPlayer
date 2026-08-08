---
work_package_id: WP04
title: Tempo Segments & Continuity
dependencies: []
requirement_refs:
- FR-001
- FR-003
subtasks:
- T016
- T017
- T018
- T019
- T020
phase: Phase 4 - Tempo segments
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Core/Timing/
create_intent: []
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/TempoSegment.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/BeatGridFitter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimingException.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMap.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP04 – Tempo Segments & Continuity

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

This WP turns validated tempo transitions into continuous piecewise `TempoSegment`s (plan IC-03, spec §17, §18, §19, §9, §58–§61). When complete:

1. Each accepted sustained tempo transition creates a new segment beginning at the transition sample; the musical position at that sample is identical across both segments (continuous).
2. The next segment's `QuarterPositionAtStart` is threaded from the previous segment (double precision), never recalculated from rounded MIDI ticks.
3. Jitter-driven segment spam is suppressed: small timer/observation jitter does not spawn per-beat tempo segments (only real sustained changes do).
4. Validated driver tempo transitions are preferred when available; beat anchors verify phase/continuity.
5. `TempoSegment.MicrosecondsPerQuarter` is computed via `round(60_000_000 / BPM)` and validated as representable in the MIDI 24-bit value (spec §19, WP07 authorizes emission).
6. Tests cover notes/pitch/anchors before, at, and after a transition, plus the real tempo change (e.g. 120→150 BPM) and exact-boundary cases (§59–§61).

## Context & Constraints

- **Spec**: §9 (continuity), §17 (tempo changes vs jitter), §18 (transition requirements + boundary tests), §19 (µs/qn representation), §58–§61 (real change, note crossing, pitch at boundary).
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-03.
- **Data model**: `data-model.md` §2 (`TempoSegment` continuity).
- **Quickstart**: Batch D.
- **Constraint**: Do not emit hundreds of tempo events because source timer observations jitter slightly (§75). Prefer explicit validated driver tempo transitions.

## Grounded source evidence (verified 2026-08-08)

- `TempoSegment` (record): `StartSample`, `EndSample` (exclusive), `QuarterPositionAtStart`, `SamplesPerQuarter`, `BeatsPerMinute`, `TimingSource`, `Confidence`; helpers `IsEmpty`, `MicrosecondsPerQuarter` (rounded/clamped), `QuarterPositionAtEnd`, `QuarterPositionAt` (inclusive).
- `MusicalTimeMapBuilder.Build` currently groups tempo changes from `DriverTimingEvent.ValidatedBpm` by sample; `DriverBeatAnchors` calls `BeatGridFitter` with tempo changes/detection; `AssembleMap` threads continuity.
- `BeatGridFitter` partitions explicit or detected changes via `RobustLinearFit` (median adjacent slopes then LS, 0.45-quarter tolerance).
- `TimingDiagnostics` exposes `SegmentCount`, `RmsResidualQuarters/Samples`, `TempoSource`, `TempoInferred`, authority flags.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

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
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs` (segments/ordering), and later `MusicalMidiExporterTests.cs` for tick-level assertions (coordinate with WP06).
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

Tests belong in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs` for segment construction/continuity, with tick-level emission assertions coordinated into WP06/`MusicalMidiExporterTests.cs`.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalTimingTests"
```

## Risks & Mitigations

- **Risk**: One tempo event per jitter beat. → Mitigation: T018 suppression + §17.
- **Risk**: Phase reset on tempo change. → Mitigation: T017 regression.
- **Risk**: Note crossing a transition gets wrong duration. → Mitigation: T019 §60 test.
- **Risk**: Unrepresentable tempo silently clamped. → Mitigation: T020 rejection.

## Review Guidance

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