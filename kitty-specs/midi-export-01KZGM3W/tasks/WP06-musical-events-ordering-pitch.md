---
work_package_id: WP06
title: Musical Events, Ordering & Pitch Policy
dependencies: []
requirement_refs:
- FR-001
- FR-003
- FR-004
subtasks:
- T026
- T027
- T028
- T029
- T030
phase: Phase 6 - Musical events
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/
create_intent: []
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiFileWriter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Dac/DacMidiExporter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/VisualizationTimeline.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/DacMidiExporterTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiPitchAccuracyTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP06 – Musical Events, Ordering & Pitch Policy

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

This WP hardens musical event projection, deterministic ordering, and pitch policy (plan IC-04, IC-05; spec §28–§37, §60–§63, §66–§68). When complete:

1. Note start and end map **independently** from their absolute source samples (`endiTick = map(EndSample) + origin`, never `startTick + duration`) so notes crossing a tempo change get the correct musical duration (§28).
2. A source note with positive duration that rounds to the same tick emits `endTick = startTick + 1` (minimum note duration; not a global quantization) (§29, test §62).
3. Every `PitchChange.SamplePosition` maps independently through the map; pitch events crossing tempo changes remain correct (§31).
4. One explicit, documented bend-range policy with RPN setup; bend offsets clamped to the 14-bit range, no wrapped arithmetic (§32, §33).
5. Explicit deterministic same-tick ordering with stable secondary keys; retriggers emit Note-Off before Note-On (§30, §34, test §63).
6. Rhythm and YM2612 DAC triggers route through the **same** map + origin; no independent samples-per-tick calculation (§36, §37, test §67). DAC sample→note identity is independent of trigger→tick (§38, test §68).

## Context & Constraints

- **Spec**: §28–§38, §60–§63, §66–§68.
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-04, IC-05.
- **Data model**: `data-model.md` §4 (event priority, origin, note/pitch, rhythm/DAC).
- **Quickstart**: Batch F.
- **Constraint**: `MusicalMidiExporter` must not do BPM inference; DAC timing responsibility must not return (§7, §37, §38).

## Grounded source evidence (verified 2026-08-08)

- `MusicalMidiExporter(map, ppq, options)`: `Export` computes nonnegative origin, emits conductor tempo/meter/markers/metadata, note events with pitch bends, rhythm short hits (PPQ/32 percussion), and quantizes only note-ons (off by default). `MapTick = map.SampleToQuarterPosition + origin` then `AwayFromZero`.
- `MidiEvent.cs`: `MidiEventBase` (mutable `Tick`); records `MidiNoteEvent(TickIn,Track,Channel,Note,Velocity,NoteOn)`, `Tempo`, `TimeSignature`, `MetaText`, `Marker`, `Program`, `Bank`, `PitchBend`, `BendRange`; `MidiEventOrder.Rank`: note-off 0, tempo/time-sig/marker/text 1, bank/program/range 2, bend 3, note-on 4. **No explicit priority field** — ordering currently relies on `MidiEventOrder.Rank`.
- `Visualization/Dac/DacMidiExporter.cs` (in `Visualization/Dac/`): `DacMidiExporter(map, ppqn=480)`, `BuildEvents`, `Write`; maps ticks via `map.SampleToTick` **only** (no origin shift / negative protection — a known contradiction vs the core exporter); emits `DacMidiEvent(Tick,Track,Channel,NoteOn,Note,Velocity,Text)`; sorts track/tick/off-before-on; asset `DisplayBank` → track=bank/16, channel=bank%16.
- Existing `MidiPitchAccuracyTests` cover `MidiChannelPitchState` controller sequences (RPN/NRPN/fine/coarse/reset) and `TimelineBuilder(48k)+VisualizationDeviceCatalog.Midi()` for bend-range changes.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

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
- **Notes**: This is the deterministic-ordering foundation; WP07's writer consumes it.

### Subtask T030 – Route rhythm + DAC through shared map + origin

- **Purpose**: No independent rhythm/DAC samples-per-tick math (spec §36, §37, §38; tests §67, §68).
- **Steps**:
  1. Verify rhythm events map via `MusicalTimeMap.SampleToTick` + the global origin; verify no rhythm-specific samples-per-tick calculation exists.
  2. Verify DAC trigger timing uses the shared map + origin; the DAC exporter (`DacMidiExporter`) must already receive an established mapper (it does — `map`), and must not establish tempo or convert from sampleRate/BPM itself. Confirm the shared `MusicalMidiExporter`/event builder handles DAC triggers where owned here.
  3. Add §67 test: melodic `NoteEvent`, DAC trigger, and rhythm trigger all at the same sample → same musical tick (before event-priority differences).
  4. Add §68 DAC-identity-independence test: same timed DAC sequence with two different valid sample-ID/note mappings → MIDI note numbers may differ, **event ticks identical**, **tempo track identical**.
  5. Fix the DAC exporter's missing origin-shift/negative-tick gap: if a DAC trigger maps to a negative tick (no origin), route it through the shared origin policy (coordinate with WP05/WP07 so the fix is consistent).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MusicalMidiExporter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Dac/DacMidiExporter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/DacMidiExporterTests.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: No — depends on T026–T029 + DAC surface.
- **Notes**: DAC sample identity (`raw PCM → canonical ID → MIDI note`) is independent from trigger timing; changing dedup must never move events, and changing BPM/phase recovery must never change DAC identity (§38).

---

## Test Strategy

Required tests across `MusicalMidiExporterTests.cs`, `DacMidiExporterTests.cs`, and `MidiPitchAccuracyTests.cs`:

- Note/min-duration/note-crossing-tempo: §60, §62.
- Pitch mapping/boundary: §61; bend-range clamp/RPN in `MidiPitchAccuracyTests`.
- Retrigger/ordering: §30, §63, §34, §35.
- Shared-tick alignment (melodic/DAC/rhythm): §67.
- DAC identity independence: §68.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalMidiExporterTests|FullyQualifiedName~DacMidiExporterTests|FullyQualifiedName~MidiPitchAccuracyTests"
```

## Risks & Mitigations

- **Risk**: Deriving `endTick` from `startTick + duration`. → Mitigation: T026 §60 test.
- **Risk**: Zero-duration notes. → Mitigation: T026 one-tick floor.
- **Risk**: Unstable same-tick ordering. → Mitigation: T029 stable keys + deterministic sort.
- **Risk**: DAC path re-derives timing or bypasses origin. → Mitigation: T030 §67/§68 + origin-shift fix.
- **Risk**: Bend overflow/wrap. → Mitigation: T028 clamp + RPN once.

## Review Guidance

- Verify independent endpoint mapping and no `startTick+duration` (§28).
- Verify deterministic ordering with stable keys (§34) and retrigger Off→On (§30).
- Verify rhythm/DAC share the map + origin (§36–§38) and the DAC origin gap is closed.
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