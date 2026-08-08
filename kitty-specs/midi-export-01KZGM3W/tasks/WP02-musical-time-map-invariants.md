---
work_package_id: WP02
title: MusicalTimeMap & TempoSegment Invariants
dependencies: []
requirement_refs:
- FR-001
- FR-003
subtasks:
- T006
- T007
- T008
- T009
- T010
phase: Phase 2 - Map invariants
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
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMap.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/TempoSegment.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimingException.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP02 – MusicalTimeMap & TempoSegment Invariants

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

This WP locks the absolute map's invariants (plan IC-03, spec §8, §9, §20, §71, §73). When complete:

1. `MusicalTimeMap` converts absolute source sample → absolute quarter position → absolute MIDI tick without cumulative rounding.
2. Negative and fractional quarter positions are supported (e.g. `sample 0 = quarter -0.25`).
3. Segment lookup is correct exactly at boundaries and deterministically for out-of-range extrapolation.
4. `TempoSegment` continuity is guaranteed in double precision: `next.QuarterPositionAtStart == previous.SampleToQuarterPosition(next.StartSample)` within floating-point tolerance.
5. A ten-minute constant-tempo timeline shows no accumulating beat drift (final beat accurate to ±1 tick).
6. `MusicalTimeMap.cs` is hardened only where the tests prove a failure; no MIDI serialization responsibilities are added.

## Context & Constraints

- **Spec**: §8 (absolute timing only, no accumulated rounded deltas), §9 (TempoSegment continuity), §20 (beat phase / negative quarters), §71 (anchor accuracy ≤1 tick), §73 (`MusicalTimeMap`, `TempoSegment` files).
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-03.
- **Data model**: `kitty-specs/midi-export-01KZGM3W/data-model.md` §2 (`MusicalTimeMap`, `TempoSegment`).
- **Quickstart**: Batch B.
- **Constraint**: Do **not** rewrite `MusicalTimeMap` wholesale; add/correct only what tests prove wrong. Never round a segment's musical origin independently in MIDI ticks — continuity exists first in double-precision musical time, rounding happens afterwards.

## Grounded source evidence (verified 2026-08-08)

- `MusicalTimeMap` (internal class): ctor `(sampleRate, startSample, segments, meter?, firstDownbeatQuarter?)`; validates sorted non-overlap (allows gaps), positive finite SPQ/BPM, and continuity ≤1e-6; `LocateSegment` binary search, extrapolates first/last outside bounds; `SampleToQuarterPosition` → `TempoSegment.QuarterPositionAt`; `QuarterPositionToTick` uses `Math.Round(AwayFromZero)`; `SampleToTick` direct absolute conversion (no accumulation); `Tick` inverse. Negative/fractional quarters already supported.
- `TempoSegment` (record): `StartSample`, `EndSample` (exclusive), `QuarterPositionAtStart` (negative/fractional allowed), `SamplesPerQuarter`, `BeatsPerMinute`, `TimingSource`, `Confidence`; helpers `IsEmpty`, `MicrosecondsPerQuarter` (rounded/clamped), `QuarterPositionAtEnd`, `QuarterPositionAt` (inclusive EndSample).
- Relevant existing tests live in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`, which already builds direct `BeatAnchor[]` + `BeatGridFitter.Fit(Sr)` and direct `PiecewiseFit → TempoSegment[] → MusicalTimeMap`, including a long-drift test.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

### Subtask T006 – Absolute conversion tests incl. negative/fractional quarters

- **Purpose**: Lock the sample→quarter→tick conversions from the spec's unit tests (§51, §52), preserving any configured global origin shift (applied later in WP05; here test raw map conversion).
- **Steps**:
  1. Add a test for the constant-tempo case: 48,000 Hz, 120 BPM → one quarter = 24,000 samples; with PPQ 960 assert `sample 0 → tick 0`, `sample 24000 → tick 960`, `sample 48000 → tick 1920`, `sample 72000 → tick 2880`.
  2. Add the nonzero-phase case: anchors `sample 12000 → quarter 0`, `36000 → quarter 1`, `60000 → quarter 2` at 48 kHz/120 BPM; assert `sample 0 → quarter -0.5` and the exported grid preserves the half-beat pickup.
  3. Assert `SampleToQuarterPosition` is exact (no accumulated error) across a broad sample range.
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes — pure addition, independent of T008/T009.
- **Notes**: Do **not** force sample zero to quarter zero in these tests; phase is independently represented.

### Subtask T007 – Segment lookup at exact boundaries; extrapolation behaviour

- **Purpose**: Lock `LocateSegment` correctness exactly at `StartSample`/`EndSample` boundaries and deterministic extrapolation for samples outside the covered range (spec §9, §73 "segment-boundary behaviour").
- **Steps**:
  1. Add tests querying a sample exactly at a segment `StartSample`, exactly at `EndSample - 1`, and at `EndSample` (which belongs to the next segment / may be extrapolation).
  2. Add tests for samples before the first segment start and after the last segment end, asserting deterministic extrapolation.
  3. Add tests for multi-segment maps with gaps, if the ctor allows them.
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes.
- **Notes**: `QuarterPositionAt(EndSample)` is documented as inclusive; verify the boundary convention is deterministic and test-locked.

### Subtask T008 – TempoSegment continuity invariant

- **Purpose**: Guarantee piecewise musical-time continuity in double precision (spec §9, plan IC-03). The next segment's origin must be threaded from the previous segment, not independently recalculated.
- **Steps**:
  1. Add an invariant test: for adjacent segments `A [startA, startB)` and `B [startB, …)`, assert `B.QuarterPositionAtStart == A.SampleToQuarterPosition(startB)` within `1e-6` (or the code's documented tolerance).
  2. Add a test that tolerates tiny floating-point drift (≤1e-6) but fails on a real discontinuity.
  3. If the map already validates continuity at construction, add an explicit coverage test; otherwise add the invariant check in debug/tests as the spec requires.
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMap.cs` (only if a debug/test invariant is missing).
- **Parallel?**: Yes.
- **Notes**: Continuity must hold in double-precision musical time first; MIDI tick rounding happens afterwards and is not part of this invariant.

### Subtask T009 – Ten-minute constant-tempo no-drift test

- **Purpose**: Prove long files have no cumulative drift (spec §8's "Required long-duration test", §69).
- **Steps**:
  1. Build a constant-tempo timeline lasting at least ten minutes (e.g. 120 BPM × 10 min = 1200 quarters at 48 kHz).
  2. Create anchors throughout (e.g. every quarter).
  3. For every anchor assert `expectedTick = round(anchorQuarter * PPQ + originTickOffset)` and `abs(actualTick - expectedTick) <= 1`, including the final anchor.
  4. Assert the final beat is as accurate as the first (no error proportional to duration).
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`.
- **Parallel?**: Yes.
- **Notes**: This is the key regression that protects against incremental `currentTick += round(delta * ticksPerSample)` bugs.

### Subtask T010 – Harden MusicalTimeMap.cs from discovered failures

- **Purpose**: Fix only failures the tests above reveal (spec §73 "Audit and harden"; plan IC-03). Avoid wholesale rewrites and avoid adding MIDI serialization responsibilities.
- **Steps**:
  1. Run the new tests from T006–T009.
  2. Fix only the failing invariants (e.g., boundary handling, tolerance, extrapolation determinism) inside `MusicalTimeMap.cs` / `TempoSegment.cs`.
  3. If a failure stems from independently recalculated rounded origins, correct the construction path in `MusicalTimeMapBuilder` (where `AssembleMap` threads continuity) — coordinate with WP04 if the change is a behavior shift.
  4. Re-run tests until green; keep the diff minimal and targeted.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMap.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/TempoSegment.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimeMapBuilder.cs` (coordination only).
- **Parallel?**: No — depends on T006–T009.
- **Notes**: Do not add serialization, BPM inference, or phase logic here. This file stays the sample-domain authority.

---

## Test Strategy

All tests in this WP belong in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalTimingTests.cs`, following the file's existing convention (direct `BeatAnchor[]` + `BeatGridFitter.Fit(Sr)` + `PiecewiseFit → TempoSegment[] → MusicalTimeMap`).

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalTimingTests"
```

## Risks & Mitigations

- **Risk**: Accumulated rounding from incremental tick computation. → Mitigation: T009 no-drift test fails on any proportional error.
- **Risk**: Segment origins rounded independently to MIDI ticks, destroying continuity. → Mitigation: T008 continuity invariant in double precision.
- **Risk**: Boundary-off-by-one in segment lookup. → Mitigation: T007 exact-boundary tests.
- **Risk**: Rewriting the map wholesale. → Mitigation: T010 fixes only proven failures.

## Review Guidance

- Verify every accepted anchor satisfies `abs(exported tick - round(quarter * PPQ + origin)) <= 1` (§71).
- Verify ten-minute drift test exists and asserts the final beat.
- Verify continuity is double-precision, with MIDI rounding applied after.
- Verify no MIDI serialization responsibilities leaked into `MusicalTimeMap`.

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