---
work_package_id: WP05
title: Global Origin, Pickups & Conductor Track
dependencies: []
requirement_refs:
- FR-001
- FR-003
- FR-005
subtasks:
- T021
- T022
- T023
- T024
- T025
phase: Phase 5 - MIDI origin & conductor
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/
create_intent:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/ConductorEvent.cs
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Meter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/VisualizationTimeline.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP05 – Global Origin, Pickups & Conductor Track

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

This WP establishes the single global MIDI origin, pickup preservation, and the conductor track (plan IC-04, spec §20–§26, §34, §35). When complete:

1. After all source events map to absolute musical positions, exactly **one** global nonnegative `originTickOffset` is computed such that every exported tick is nonnegative (§21).
2. The **same offset** applies to conductor, note, pitch-bend, rhythm, DAC, marker, and loop events — tracks/segments are never shifted independently (§21).
3. Pickup notes (negative/fractional quarter positions) survive via the global shift and are never snapped to the first beat (§20, §22; tests §54).
4. Set Tempo events are generated per segment with µs/qn dedup (compare emitted MIDI value, not raw BPM) (§19).
5. Time Signature is emitted **only** when meter is known (§23; tests §64/§65). Unknown meter → omit, never fabricate 4/4.
6. Markers/loops map through the same map and are not quantized (§40).
7. Track 0 is conductor-only (Track Name, Set Tempo, optional Time Signature, markers/metadata, EOT); no per-channel note data on Track 0 (§25, §26).

## Context & Constraints

- **Spec**: §19–§26, §34, §35, §40, §49, §54, §64, §65.
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-04.
- **Data model**: `data-model.md` §4 (origin shift, conductor tracks).
- **Quickstart**: Batch E.
- **Constraint**: `MusicalMidiExporter` must not do BPM inference (spec §73). It maps already-built segments to conductor events.

## Grounded source evidence (verified 2026-08-08)

- `MusicalMidiExporter(map, ppq, options)`: `Export` computes a nonnegative origin offset from the earliest note/rhythm quarter, ceils to ≥0 and to a meter-bar ceil; emits conductor tempo per map segment, optional meter/markers/metadata, note events with pitch bends, rhythm short hit PPQ/32 percussion, and quantizes **only** note-ons (quantization is off by default). `MapTick = map.SampleToQuarterPosition + origin` then `AwayFromZero` ticks.
- `MidiEvent.cs`: `MidiEventBase` (abstract record, mutable `Tick`); concrete records `MidiNoteEvent(TickIn,Track,Channel,Note,Velocity,NoteOn)`, `Tempo`, `TimeSignature`, `MetaText`, `Marker`, `Program`, `Bank`, `PitchBend`, `BendRange`; `MidiEventOrder.Rank`: note-off 0, tempo/time-sig/marker/text 1, bank/program/range 2, bend 3, note-on 4.
- `Meter` record: `Numerator`/`Denominator`; `QuartersPerBar = Numerator*4/Denominator`; `TryParse` positive.
- `VisualizationTimeline`: contains `LoopMarkers` and note/rhythm event records.
- Existing `MusicalMidiExporterTests` already cover conductor SetTempo/markers, phase origin shift, and deterministic bytes.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

### Subtask T021 – One global nonnegative origin tick offset

- **Purpose**: Guarantee all exported ticks are nonnegative via a single shift (spec §21).
- **Steps**:
  1. Verify `MusicalMidiExporter.Export` computes exactly one `originTickOffset >= 0` from the earliest mapped musical position.
  2. Assert the same offset is applied to the conductor track and every musical track (§21 "Do not shift each track separately").
  3. Document the origin computation (which message/event family defines the minimum tick).
  4. Add a test with a full-format export where the earliest note is at a negative quarter → all ticks nonnegative.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes — core origin logic.
- **Notes**: The origin is global; never recompute per track/segment.

### Subtask T022 – Preserve pickup notes

- **Purpose**: Pickups must remain 0.5 quarter before the downbeat after the shift (spec §22, test §54).
- **Steps**:
  1. Add the §54 pickup test: first note at quarter -0.5, first downbeat at quarter 0 → all ticks nonnegative, the note still occurs 0.5 quarter before the downbeat, and the origin shift is applied once globally.
  2. Verify `MusicalTimeMap`'s negative/fractional quarters flow through unchanged into the exporter (do not snap to the first observed beat).
  3. Confirm the origin shift is global across all event families.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: No — depends on T021.
- **Notes**: Never snap the pickup to the first beat (§22).

### Subtask T023 – Set Tempo + Time Signature policy

- **Purpose**: Emit correct Set Tempo per segment with µs/qn dedup; emit Time Signature only when meter is known (spec §19, §23, tests §64/§65).
- **Steps**:
  1. Verify Set Tempo events are derived per `TempoSegment` using the validated µs/qn value (from WP04 T020).
  2. If adjacent segments emit the same µs/qn value, emit one Set Tempo event (§19). Compare the emitted MIDI integer value, not raw BPM.
  3. For unknown meter, add the §64 test: excellent beat/tempo anchors but no bar info → correct beat alignment, **no** fabricated 4/4 Time Signature.
  4. For known meter, add the §65 test: authoritative `4/4` + `firstDownbeatSample S` → valid Time Signature, correct bar phase after origin shift, and note timing NOT changed to force bar 1.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Meter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes — policy additions.
- **Notes**: Meter priority (spec §23): driver/bar info → user override → existing high-confidence inference → unknown. Never invent 4/4.

### Subtask T024 – Map markers/loops; conductor track timeline-wide

- **Purpose**: Loop/section markers map through the shared map and are never quantized; Track 0 is conductor-only (§25, §26, §40).
- **Steps**:
  1. Verify loop markers (`LoopMarker`) map via `MusicalTimeMap.SampleToTick` + the global origin; a marker on-beat or off-beat keeps its exact recovered position (§40, test §66).
  2. On Track 0, place only timeline-wide info: Track Name, Set Tempo, optional Time Signature, loop/section markers, and (optionally) compact metadata (timing source/confidence) if consistent with the existing exporter.
  3. Assert no per-channel note/controller data lands on Track 0 (§25).
  4. If a `ConductorEvent` type would clarify conductor-only event construction, add `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/ConductorEvent.cs` (KISS; only if the current structure needs it).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/VisualizationTimeline.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes.
- **Notes**: Do not quantize markers; preserve exact musical position (§40).

### Subtask T025 – Conductor tests: Format 1 track 0, EOT

- **Purpose**: Prove the exported SMF has the correct Format 1 track-0 conductor layout and one EOT per track (§24–§26, §46).
- **Steps**:
  1. Test that output is Standard MIDI Format 1 with a conductor track 0 plus musical tracks.
  2. Assert Track 0's event order: Track Name, Set Tempo events, Time Signature only when known, markers/metadata, End of Track last.
  3. Assert every track ends with exactly one effective EOT (§46; delta computed normally, not repaired by a parser).
  4. Assert no per-channel note data on Track 0.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes.
- **Notes**: EOT enforcement is shared with WP07's writer hardening (§43–§46) — coordinate so the writer owns EOT emission and this WP asserts it at the exporter-output level.

---

## Test Strategy

Tests belong in `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs` (existing exporter tests already cover conductor/phase/determinism).

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalMidiExporterTests"
```

## Risks & Mitigations

- **Risk**: Shifting tracks/segments independently, destroying alignment. → Mitigation: T021 single global offset; T022 pickup test.
- **Risk**: Fabricating 4/4. → Mitigation: T023 §64 unknown-meter test; meter priority.
- **Risk**: Snap pickups to first beat. → Mitigation: T022.
- **Risk**: Per-channel data leaking onto Track 0. → Mitigation: T024/T025 assertions.

## Review Guidance

- Verify a single global origin shift across all families (§21).
- Verify pickup preservation and §54 test.
- Verify Time Signature only when meter known (§23, §64/§65).
- Verify Track 0 conductor-only + one EOT per track (§25, §46).

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