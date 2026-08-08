---
work_package_id: WP01
title: Producer Audit & Sample-Clock Normalization
dependencies: []
requirement_refs:
- FR-001
- FR-002
- FR-005
planning_base_branch: feature/linux-fmp-renderer
merge_target_branch: feature/linux-fmp-renderer
branch_strategy: Planning artifacts for this mission were generated on feature/linux-fmp-renderer. During /spec-kitty.implement this WP may branch from a dependency-specific base, but completed changes must merge back into feature/linux-fmp-renderer unless the human explicitly redirects the landing branch.
base_branch: kitty/mission-midi-export-01KZGM3W
base_commit: 34efa5d4fff4726df3c793144347183d69b0ab22
created_at: '2026-08-08T14:04:40.251065+00:00'
subtasks:
- T001
- T002
- T003
- T004
- T005
phase: Phase 1 - Producer semantics
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Core/Visualization/
create_intent:
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/ProducerClockNormalization.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/ProducerClockNormalizationTests.cs
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/TimelineBuilder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/VisualizationTimeline.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Ym2608TimelineDecoder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Ym2612TimelineDecoder.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/ChipTimelineDecoderRegistry.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/VisualizationJsonWriter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/ProducerClockNormalization.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/Ym2608TimelineDecoderTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/Visualization/VisualizationV3ContractTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/ProducerClockNormalizationTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP01 – Producer Audit & Sample-Clock Normalization

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

This is the mandatory **producer audit** (spec §4, §5, §6) and the single sample-clock boundary (plan IC-01, FR-002). When this work package is complete:

1. Every `TimelineBuilder.AddTiming(...)` and `TimelineBuilder.AddBeat(...)` producer is enumerated and its semantics documented (do not infer from record names).
2. The exact unit of `BeatEvent.BeatIndex` (quarter / driver beat / timer tick / subdivision), its fractional/jump/nonzero/loop-reset behaviour, and whether it resets on loops are determined and locked by a regression test.
3. The exact meaning of `DriverTimingEvent.ValidatedBpm` (effective playback BPM after tempo/speed/timer/multiplier/song rules) and the sample at which a new value becomes effective are determined and locked by a regression test.
4. All timed events (`DriverTimingEvent.SamplePosition`, `BeatEvent.SamplePosition`, `NoteEvent.StartSample`/`EndSample`, `PitchChange.SamplePosition`, rhythm, DAC) are verified to share the final playback/output sample clock.
5. A single `ProducerClockNormalization` adapter exists at the producer boundary for a proven clock mismatch; **ambiguous clocks are rejected** with an actionable diagnostic. No MIDI component compensates for a clock mismatch.
6. No downstream compensation is introduced anywhere.

## Context & Constraints

- **Charter**: `.kittify/charter/charter.md` is absent — do not invent charter gates.
- **Spec**: `kitty-specs/midi-export-01KZGM3W/spec.md` §4 (mandatory producer audit), §5 (BeatIndex semantics), §6 (ValidatedBpm semantics), §7, §13, §39.
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-01.
- **Research**: `kitty-specs/midi-export-01KZGM3W/research.md` (producer audit evidence).
- **Data model**: `kitty-specs/midi-export-01KZGM3W/data-model.md` §1 (`ProducerClockNormalization` concept).
- **Quickstart**: `kitty-specs/midi-export-01KZGM3W/quickstart.md` Batch A.
- **Approved decision**: normalize a differing producer clock **once at its producer boundary** when both clocks are explicit; **reject ambiguous clocks**. This was decided in planning (decision `01KZGN2DH6C26YQ7NTXGCN4MT7`) — follow it.

## Grounded source evidence (verified 2026-08-08)

- `Visualization/TimelineBuilder.cs`: `AddTiming(DriverTimingEvent)` and `AddBeat(BeatEvent)` null-check and append; `Build` orders by `SamplePosition`; `Merge` forwards existing timeline arrays. This is the single producer-boundary ingestion point.
- `Visualization/Ym2608TimelineDecoder.cs`: `ApplyYm2608(..., long samplePosition)` appends `new DriverTimingEvent(samplePosition, value, ComputeTimerBpm(value))` for YM2608 port 0 register `0x26` (Timer B) writes. Its comments assume one Timer-B interrupt == one quarter note; that is a claim to verify, not proof.
- `Visualization/ChipTimelineDecoderRegistry.cs` / `TimelineDecoderEventSink`: `OnChipWrite` validates and forwards `TimedChipWrite` to the selected decoder; `Complete` merges decoder timelines and builds the final `VisualizationTimeline`.
- `Visualization/VisualizationTimeline.cs`: `DriverTimingEvent`, `BeatEvent`, note/rhythm/pitch/DAC records carry source sample positions; `VisualizationTimeline.SampleRate` accompanies the arrays. Record names do **not** establish clock equivalence.
- `Visualization/VisualizationJsonWriter.cs` / `MDPlayer.Fmp.Cli/TimelineCaptureService.cs`: external timeline input can load a serialized `VisualizationTimeline`; beats can enter from JSON.
- **No runtime `new BeatEvent(...)` constructor exists under `MDPlayer/src`**; the only non-contract `AddBeat` calls are the builder's own `Merge` path and direct test setup. Treat timeline-provided beats as an input contract and audit their producer metadata/clock at the timeline boundary.
- `TimelineCaptureService` picks the capture timeline rate from the backend's native probe rate when >0, else the configured output rate.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

### Subtask T001 – Enumerate and document every AddTiming/AddBeat producer

- **Purpose**: The spec mandates locating every producer of `TimelineBuilder.AddTiming(...)` and `AddBeat(...)` **before** changing fitting. Do not infer semantics from record names.
- **Steps**:
  1. Grep the entire `MDPlayer/src` tree for `AddBeat(` and `AddTiming(` (balanced versions included). List every call site.
  2. Grep for `new BeatEvent(` and `new DriverTimingEvent(` to find constructors.
  3. For each producer, record: path, decoder/format, what invokes it, the originating sample clock, and what fields it sets.
  4. Record the current `Ym2608TimelineDecoder` Timer-B assumption (one interrupt == one quarter) as an **unverified hypothesis**.
  5. Note which `BeatEvent` inputs come from serialized timelines vs decoder capture.
  6. Write a short audit note inside `ProducerClockNormalization.cs` XML docs (or a `// Producer audit` comment block) enumerating findings.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/` (read-only except new `ProducerClockNormalization.cs`), `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/TimelineBuilder.cs`.
- **Parallel?**: No — prerequisite shape for T002/T003.
- **Notes**: Do not "fix" any producer yet. This pass is documentation + structure only.

### Subtask T002 – Document BeatIndex semantics with a regression test

- **Purpose**: Lock the true meaning of `BeatEvent.BeatIndex` (spec §5). Determine: one increment == quarter? driver beat? timer tick? subdivision? fractional? resets at loops? first value nonzero? values jump? emitted before/after a timing change at the same sample?
- **Steps**:
  1. Read `Ym2608TimelineDecoder` and any Timer-B incrementing logic; determine what actually increments `BeatIndex` and when.
  2. Determine the driver-specific beat-unit relationship and whether a `QuartersPerBeat` scale (builder `QuartersPerBeat` default 1) is the right normalization.
  3. Add a regression test in `ProducerClockNormalizationTests.cs` (or extend `Ym2608TimelineDecoderTests.cs`) that emits a real decoder event sequence and asserts the observed `BeatIndex` unit/fractional/nonzero/loop semantics exactly.
  4. If the driver defines loop reset, record the documented reset convention (do not infer one).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Ym2608TimelineDecoder.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/Ym2608TimelineDecoderTests.cs`.
- **Parallel?**: No — depends on T001.
- **Notes**: The spec forbids assuming `driver beat == MIDI quarter`. If they differ, the scale factor must be applied once during `MusicalTimeMap` construction (WP03), never inside the MIDI writer (WP05–WP07).

### Subtask T003 – Document ValidatedBpm semantics with a regression test

- **Purpose**: Lock the meaning of `DriverTimingEvent.ValidatedBpm` (spec §6): does it reflect actual effective playback BPM after driver tempo/speed/timer/multipliers/song rules, and at what exact sample does a new value become effective?
- **Steps**:
  1. Trace how `ComputeTimerBpm(value)` (YM2608) derives BPM and whether it accounts for effective playback factors.
  2. Determine whether the event sample is "new tempo begins here" or "value observed here". Adopt one convention and document it.
  3. Add a regression test asserting the activation sample semantics for a `DriverTimingEvent`.
  4. Note that `ValidatedBpm` is usable only when finite and positive; record that `TimerBValue` must not be reverse-engineered in MIDI code.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Ym2608TimelineDecoder.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/Ym2608TimelineDecoderTests.cs`.
- **Parallel?**: No — depends on T001.
- **Notes**: Prefer `ValidatedBpm` over re-deriving from the timer in the MIDI layer.

### Subtask T004 – Verify shared sample clock and add ProducerClockNormalization adapter

- **Purpose**: Confirm all timed event families share the final playback/output sample clock (spec §4.1). Where a producer clock provably differs, normalize once at the producer boundary with `ProducerClockNormalization`; reject ambiguous clocks.
- **Steps**:
  1. For each event family (timing, beats, notes, pitch, rhythm, DAC, markers), confirm the sample positions are on the final timeline clock (`VisualizationTimeline.SampleRate`).
  2. Specifically examine `TimelineCaptureService`: native-probe-rate-vs-configured-rate selection, and the serialized-JSON timeline path where `SampleRate` metadata may drift.
  3. Create `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/ProducerClockNormalization.cs` with a narrowly scoped method/type that, given a producer identity + source clock + destination clock + deterministic rounding policy + covered event families, converts samples once and reports whether the relationship is unambiguous.
  4. If either clock or the relationship is unknown/ambiguous, throw `MusicalTimingException` with an actionable message (e.g. "Cannot establish timing sample clock for producer X: source rate unknown").
  5. Wire the adapter at the producer boundary (timeline ingestion) **before** `MusicalTimeMapBuilder` sees anchors/events. Preserve source ordering.
  6. Confirm **no** compensation logic is added to `MidiFileWriter`, `MusicalMidiExporter`, DAC, or rhythm conversion (they must stay downstream-clean).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/ProducerClockNormalization.cs` (new), `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/TimelineBuilder.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/MusicalTimingException.cs`.
- **Parallel?**: No.
- **Notes**: Reject an unresolved clock rather than hide it. Do not infer a clock conversion from record names.

### Subtask T005 – Regression tests locking producer semantics; no downstream compensation

- **Purpose**: Protect the discovered semantics (spec mandates "Add a regression test that locks the discovered semantics") and prove no clock compensation leaks downstream.
- **Steps**:
  1. In `ProducerClockNormalizationTests.cs`, test: an unambiguous clock mismatch converts correctly once; an ambiguous clock (missing/unknown rate) throws; a matching clock passes through unchanged; event ordering is preserved.
  2. Add a guard test in `VisualizationV3ContractTests.cs` (or a new test) asserting the timeline sample-rate metadata accompanies serialized timelines so the boundary can be validated on load.
  3. Add an audit assertion covering all timed event families so a future producer on a different clock is caught.
  4. Run the relevant existing tests (`Ym2608TimelineDecoderTests`, `VisualizationV3ContractTests`) to confirm no regressions.
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/ProducerClockNormalizationTests.cs` (new), `MDPlayer/tests/MDPlayer.Fmp.Tests/Visualization/VisualizationV3ContractTests.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/Ym2608TimelineDecoderTests.cs`.
- **Parallel?**: No — depends on T001–T004.
- **Notes**: These tests are the guardrail that prevents a subtle second clock from returning in later WPs.

---

## Test Strategy

Tests are required for this WP (spec explicitly mandates regression tests for producer semantics), but add **only** the tests described above; avoid broad new test files. Use the existing `MDPlayer/tests/MDPlayer.Fmp.Tests` project conventions.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~Ym2608TimelineDecoderTests|FullyQualifiedName~VisualizationV3ContractTests|FullyQualifiedName~ProducerClockNormalizationTests"
```

## Risks & Mitigations

- **Risk**: Inferring `BeatIndex` unit or clock equivalence from record names. → Mitigation: T002 and T004 drive proofs from actual decoder/ingestion behaviour, never names.
- **Risk**: Hiding a clock mismatch inside the MIDI writer. → Mitigation: T007 (WP07) later enforces writer purity; this WP adds no compensation.
- **Risk**: Treating driver loop resets as corruption. → Mitigation: T002 records documented reset semantics instead of inferring.
- **Risk**: A serialized timeline loses rate metadata and loads with a wrong clock. → Mitigation: T005 guard test + rejection on ambiguity.

## Review Guidance

- Verify the producer enumeration is exhaustive (`AddBeat`/`AddTiming`/`new BeatEvent`/`new DriverTimingEvent`).
- Verify `BeatIndex` and `ValidatedBpm` semantics are documented and test-locked, not assumed.
- Verify exactly one `ProducerClockNormalization` boundary, with rejection for ambiguous clocks, and zero compensation in MIDI/downstream code.
- Verify no parallel timing abstraction was introduced.

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