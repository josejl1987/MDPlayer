---
work_package_id: WP04
title: MIDI Origin, Events, Ordering & Pitch
dependencies: ["WP03"]
requirement_refs:
- FR-001
- FR-003
- FR-004
planning_base_branch: feature/linux-fmp-renderer
merge_target_branch: feature/linux-fmp-renderer
branch_strategy: Planning artifacts for this mission were generated on feature/linux-fmp-renderer. During /spec-kitty.implement this WP may branch from a dependency-specific base, but completed changes must merge back into feature/linux-fmp-renderer unless the human explicitly redirects the landing branch.
subtasks:
- T021
- T022
- T023
- T024
- T025
- T026
- T027
- T028
- T029
- T030
phase: Phase 4 - MIDI origin & events
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
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiPitchAccuracyTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP04 – MIDI Origin, Events, Ordering & Pitch

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

And this WP hardens musical event projection, deterministic ordering, and pitch policy (plan IC-04, IC-05; spec §28–§37, §60–§63, §66–§68). When complete:

8. Note start and end map **independently** from their absolute source samples (`endiTick = map(EndSample) + origin`, never `startTick + duration`) so notes crossing a tempo change get the correct musical duration (§28).
9. A source note with positive duration that rounds to the same tick emits `endTick = startTick + 1` (minimum note duration; not a global quantization) (§29, test §62).
10. Every `PitchChange.SamplePosition` maps independently through the map; pitch events crossing tempo changes remain correct (§31).
11. One explicit, documented bend-range policy with RPN setup; bend offsets clamped to the 14-bit range, no wrapped arithmetic (§32, §33).
12. Explicit deterministic same-tick ordering with stable secondary keys; retriggers emit Note-Off before Note-On (§30, §34, test §63).
13. Rhythm and YM2612 DAC triggers route through the **same** map + origin; no independent samples-per-tick calculation (§36, §37, test §67). DAC sample→note identity is independent of trigger→tick (§38, test §68).

## Context & Constraints

- **Spec**: §19–§26, §34, §35, §40, §49, §54, §64, §65.
- **Spec**: §28–§38, §60–§63, §66–§68.
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-04, IC-05.
- **Data model**: `data-model.md` §4 (origin shift, conductor tracks, event priority, note/pitch, rhythm/DAC).
- **Quickstart**: Batches E and F.
- **Constraint**: `MusicalMidiExporter` must not do BPM inference (spec §73). It maps already-built segments to conductor events.
- **Constraint**: DAC timing responsibility must not return (§7, §37, §38).

## Grounded source evidence (verified 2026-08-08)

- `MusicalMidiExporter(map, ppq, options)`: `Export` computes a nonnegative origin offset from the earliest note/rhythm quarter, ceils to ≥0 and to a meter-bar ceil; emits conductor tempo per map segment, optional meter/markers/metadata, note events with pitch bends, rhythm short hit PPQ/32 percussion, and quantizes **only** note-ons (quantization is off by default). `MapTick = map.SampleToQuarterPosition + origin` then `AwayFromZero` ticks.
- `MidiEvent.cs`: `MidiEventBase` (abstract record, mutable `Tick`); concrete records `MidiNoteEvent(TickIn,Track,Channel,Note,Velocity,NoteOn)`, `Tempo`, `TimeSignature`, `MetaText`, `Marker`, `Program`, `Bank`, `PitchBend`, `BendRange`; `MidiEventOrder.Rank`: note-off 0, tempo/time-sig/marker/text 1, bank/program/range 2, bend 3, note-on 4. **No explicit priority field** — ordering currently relies on `MidiEventOrder.Rank`.
- `Meter` record: `Numerator`/`Denominator`; `QuartersPerBar = Numerator*4/Denominator`; `TryParse` positive.
- `VisualizationTimeline` (owned by WP01): contains `LoopMarkers` and note/rhythm event records (read-only reference here).
- `Visualization/Dac/DacMidiExporter.cs` (owned by WP05, in `Visualization/Dac/`): `DacMidiExporter(map, ppqn=480)`, `BuildEvents`, `Write`; maps ticks via `map.SampleToTick` **only** (no origin shift / negative protection — a known contradiction vs the core exporter); emits `DacMidiEvent(Tick,Track,Channel,NoteOn,Note,Velocity,Text)`; sorts track/tick/off-before-on; asset `DisplayBank` → track=bank/16, channel=bank%16.
- Existing `MusicalMidiExporterTests` already cover conductor SetTempo/markers, phase origin shift, and deterministic bytes.
- Existing `MidiPitchAccuracyTests` cover `MidiChannelPitchState` controller sequences (RPN/NRPN/fine/coarse/reset) and `TimelineBuilder(48k)+VisualizationDeviceCatalog.Midi()` for bend-range changes.

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
  1. Verify Set Tempo events are derived per `TempoSegment` using the validated µs/qn value (from WP03 T020).
  2. If adjacent segments emit the same µs/qn value, emit one Set Tempo event (§19). Compare the emitted MIDI integer value, not raw BPM.
  3. For unknown meter, add the §64 test: excellent beat/tempo anchors but no bar info → correct beat alignment, **no** fabricated 4/4 Time Signature.
  4. For known meter, add the §65 test: authoritative `4/4` + `firstDownbeatSample S` → valid Time Signature, correct bar phase after origin shift, and note timing NOT changed to force bar 1.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Meter.cs` (read-only coordination with WP01's TimingSource surface), `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes — policy additions.
- **Notes**: Meter priority (spec §23): driver/bar info → user override → existing high-confidence inference → unknown. Never invent 4/4.

### Subtask T024 – Map markers/loops; conductor track timeline-wide

- **Purpose**: Loop/section markers map through the shared map and are never quantized; Track 0 is conductor-only (§25, §26, §40).
- **Steps**:
  1. Verify loop markers (`LoopMarker`) map via `MusicalTimeMap.SampleToTick` + the global origin; a marker on-beat or off-beat keeps its exact recovered position (§40, test §66).
  2. On Track 0, place only timeline-wide info: Track Name, Set Tempo, optional Time Signature, loop/section markers, and (optionally) compact metadata (timing source/confidence) if consistent with the existing exporter.
  3. Assert no per-channel note/controller data lands on Track 0 (§25).
  4. If a `ConductorEvent` type would clarify conductor-only event construction, add `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/ConductorEvent.cs` (KISS; only if the current structure needs it).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
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
- **Notes**: EOT enforcement is shared with WP05's writer hardening (§43–§46) — coordinate so the writer owns EOT emission and this WP asserts it at the exporter-output level.

### Subtask T026 – Independent note endpoints + minimum note duration

- **Purpose**: Notes crossing a tempo change get correct duration; zero-tick notes are prevented (spec §28, §29; tests §60, §62).
- **Steps**:
  1. Verify `startTick = map(StartSample) + origin` and `endTick = map(EndSample) + origin` are computed independently (search for any `endTick = startTick + convertedDuration` pattern and remove it).
  2. Add the §62 test: `EndSample > StartSample` but both map to one integer tick → `endTick == startTick + 1`.
  3. Add the §60 test: note `start = quarter 15.5`, `end = quarter 16.5`, tempo change at quarter 16 → both ticks derive correctly from their source samples.
  4. Document the one-tick correction in the test docstring.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes — core note mapping.
- **Notes**: Do not globally quantize surrounding events (§29).

### Subtask T027 – Independent pitch-change mapping

- **Purpose**: Pitch events crossing tempo changes stay correct (§31).
- **Steps**:
  1. Verify each `PitchChange.SamplePosition` maps via `MusicalTimeMap` + origin independently (no interpolation from the note start).
  2. Add a test: pitch changes before and after a tempo change land on the correct absolute ticks.
  3. Add the §61 test: pitch change sample exactly at a tempo segment boundary → correct tick, stable same-tick ordering, no one-tick discontinuity.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: No — depends on exporter structure.
- **Notes**: Same-tick ordering with the tempo event is handled in T029.

### Subtask T028 – Explicit bend-range policy + RPN setup

- **Purpose**: DAW playback must not depend on coincidental bend-range assumptions (spec §32, §33).
- **Steps**:
  1. Adopt one documented bend-range policy (e.g. the existing `BendRange` semitone convention; `Options.EmitPitchBend`/`BendRangeSemitones`). If the exporter uses a non-default range, emit the standard Pitch Bend Sensitivity RPN setup once per channel/state when state changes (§32), not before every note.
  2. For each pitch: choose the MIDI base note per policy, compute the bend offset, clamp to the legal 14-bit range, and never wrap arithmetic (§33).
  3. If a pitch cannot be represented, use the nearest representable base note where possible; never silently overflow.
  4. Add a regression: legal bend offsets clamp correctly; an out-of-range offset clamps (or picks nearest base note) without wrapping; RPN is emitted once per changed channel state.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiPitchAccuracyTests.cs` (extend), `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes — independent of note/pitch mapping.
- **Notes**: Avoid resending RPN before every note if channel state hasn't changed (§32).

### Subtask T029 – Explicit same-tick ordering + retrigger Off→On

- **Purpose**: Deterministic ordering without relying on insertion/dictionary/hash (spec §30, §34, §35; test §63).
- **Steps**:
  1. Verify `MidiEventOrder.Rank` provides the required semantic order: note-off(0) → conductor state(1) → setup(2) → bend(3) → note-on(4). If any case needs a stable secondary key (track/channel/event type/source index), add a small priority/source-order field on `MidiEventBase` (KISS, per spec §73) rather than many subclasses.
  2. Guarantee the sort is deterministic with stable secondary keys — never LINQ/dictionary enumeration, hash codes, object identity, thread scheduling, or unstable equal-key sorting (§45).
  3. Assert a tempo event beginning at tick `T` is active at `T` and precedes same-tick Note-On events (§35).
  4. Add the §63 retrigger test: same voice/pitch, old note ends 960, new begins 960 → encoded order Note-Off then Note-On.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: No — depends on event structure.
- **Notes**: This is the deterministic-ordering foundation; WP05's writer consumes it.

### Subtask T030 – Route rhythm + DAC through shared map + origin

- **Purpose**: No independent rhythm/DAC samples-per-tick math (spec §36, §37, §38; tests §67, §68).
- **Steps**:
  1. Verify rhythm events map via `MusicalTimeMap.SampleToTick` + the global origin; verify no rhythm-specific samples-per-tick calculation exists.
  2. Verify DAC trigger timing uses the shared map + origin; the DAC exporter (`DacMidiExporter`, owned by WP05) must already receive an established mapper (it does — `map`), and must not establish tempo or convert from sampleRate/BPM itself. Confirm the shared `MusicalMidiExporter`/event builder handles DAC triggers where owned here.
  3. Add §67 test: melodic `NoteEvent`, DAC trigger, and rhythm trigger all at the same sample → same musical tick (before event-priority differences).
  4. Add §68 DAC-identity-independence test: same timed DAC sequence with two different valid sample-ID/note mappings → MIDI note numbers may differ, **event ticks identical**, **tempo track identical**.
  5. Fix the DAC exporter's missing origin-shift/negative-tick gap: if a DAC trigger maps to a negative tick (no origin), route it through the shared origin policy (coordinate with WP05 so the fix is consistent).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: No — depends on T026–T029 + DAC surface.
- **Notes**: DAC sample identity (`raw PCM → canonical ID → MIDI note`) is independent from trigger timing; changing dedup must never move events, and changing BPM/phase recovery must never change DAC identity (§38).

---

## Test Strategy

Required tests across `MusicalMidiExporterTests.cs` and `MidiPitchAccuracyTests.cs`:

- Origin/pickup/conductor/Track-0: §54, §64/§65, §66, §25/§26.
- Note/min-duration/note-crossing-tempo: §60, §62.
- Pitch mapping/boundary: §61; bend-range clamp/RPN in `MidiPitchAccuracyTests`.
- Retrigger/ordering: §30, §63, §34, §35.
- Shared-tick alignment (melodic/DAC/rhythm): §67.
- DAC identity independence: §68 (DAC-timing part coordinated with WP05).

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalMidiExporterTests|FullyQualifiedName~MidiPitchAccuracyTests"
```

## Risks & Mitigations

- **Risk**: Shifting tracks/segments independently, destroying alignment. → Mitigation: T021 single global offset; T022 pickup test.
- **Risk**: Fabricating 4/4. → Mitigation: T023 §64 unknown-meter test; meter priority.
- **Risk**: Snap pickups to first beat. → Mitigation: T022.
- **Risk**: Per-channel data leaking onto Track 0. → Mitigation: T024/T025 assertions.
- **Risk**: Deriving `endTick` from `startTick + duration`. → Mitigation: T026 §60 test.
- **Risk**: Zero-duration notes. → Mitigation: T026 one-tick floor.
- **Risk**: Unstable same-tick ordering. → Mitigation: T029 stable keys + deterministic sort.
- **Risk**: DAC path re-derives timing or bypasses origin. → Mitigation: T030 §67/§68 + origin-shift fix.
- **Risk**: Bend overflow/wrap. → Mitigation: T028 clamp + RPN once.

## Review Guidance

- Verify a single global origin shift across all families (§21).
- Verify pickup preservation and §54 test.
- Verify Time Signature only when meter known (§23, §64/§65).
- Verify Track 0 conductor-only + one EOT per track (§25, §46).
- Verify independent endpoint mapping and no `startTick+duration` (§28).
- Verify deterministic ordering with stable keys (§34) and retrigger Off→On (§30).
- Verify rhythm/DAC share the map + origin (§36–§38) and the DAC origin gap is closed (coordinated with WP05).
- Verify bend policy (§32, §33) with RPN once.

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