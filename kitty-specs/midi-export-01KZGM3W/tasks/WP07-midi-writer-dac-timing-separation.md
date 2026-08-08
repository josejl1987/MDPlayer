---
work_package_id: WP07
title: MIDI Writer & DAC Timing Separation
dependencies: []
requirement_refs:
- FR-001
- FR-003
- FR-004
subtasks:
- T031
- T032
- T033
- T034
- T035
phase: Phase 7 - Serialization
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
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiFileWriter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Dac/MidiFileWriter.cs
- MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Dac/DacMidiExporter.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/DacMidiExporterTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP07 – MIDI Writer & DAC Timing Separation

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

This WP hardens SMF serialization and splits DAC timing from DAC identity (plan IC-06; spec §43–§46, §70, plus DAC separation §37/§38). When complete:

1. `MidiFileWriter` is serialization-only: sorted absolute event stream → valid Standard MIDI File bytes; it knows nothing about source samples or BPM fitting (§43, §73).
2. Output is SMF Format 1 with the configured PPQ division; valid `MThd`/`MTrk` chunks, correct big-endian lengths, nonnegative delta times, and one effective EOT per track (§43, §46).
3. VLQ encoding is correct across boundaries and rejects out-of-range values rather than truncating (§44).
4. Identical timeline/options/PPQ produce byte-identical `.mid` output — no dependency on dictionary/hash/identity/scheduling/unstable sorting (§45).
5. DAC exporter no longer has a hidden origin-shift/negative-tick gap and performs no independent sample→tick conversion; DAC identity→note is independent of trigger→tick (§37, §38, test §68 — shared with WP06).

## Context & Constraints

- **Spec**: §43–§46, §70; DAC §37/§38 (test §68).
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-06.
- **Data model**: `data-model.md` §5 (`MidiFileWriter`), §4 (DAC independence).
- **Quickstart**: Batch G.
- **Constraint**: `MidiFileWriter` must not know about source samples, BPM fitting, or phase. It consumes already-sorted absolute events only.

## Grounded source evidence (verified 2026-08-08)

- `Core MidiFileWriter` (in `Timing/Midi/MidiFileWriter.cs`): `Write(conductor, tracks)` → Format 1 header; validates `ppq<=0 || ppq>32767` throws; ordered tick/rank; VLQ nonnegative clamp; appends EOT; tempo precedes notes at the same tick.
- `MidiEvent.cs`: `MidiEventBase` (mutable `Tick`); records `MidiNoteEvent`, `Tempo`, `TimeSignature`, `MetaText`, `Marker`, `Program`, `Bank`, `PitchBend`, `BendRange`; `MidiEventOrder.Rank` deterministic ordering.
- `Visualization/Dac/MidiFileWriter.cs`: static `Write(ppqn, map, events, trackNames)`; Format 1; conductor tempo per map segments (absolute map ticks, incremental delta); DAC tracks note/text; VLV throws on negative.
- `Visualization/Dac/DacMidiExporter.cs`: `DacMidiExporter(map, ppqn=480)`, `BuildEvents`, `Write`; maps via `map.SampleToTick` **without** origin shift / negative protection — known contradiction vs the core exporter (T035).
- Existing `MusicalMidiExporterTests` includes a writer-level test that constructs `MidiTrack`/`MidiNoteEvent` directly + `MidiFileWriter`, covering Format 1, conductor, determinism.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

### Subtask T031 – Format 1, PPQ, MTrk lengths, nonnegative deltas

- **Purpose**: The writer produces valid SMF Format 1 with the configured division and correct chunk/delta invariants (§43).
- **Steps**:
  1. Verify `MThd` header declares Format 1 and the configured division (PPQ), and that `ppq` outside the valid MIDI-valid range (≤0 or >32767) throws.
  2. Verify each `MTrk` chunk has a correct big-endian length and its data matches.
  3. Verify delta times are nonnegative (computed only at serialization from the sorted absolute tick stream).
  4. Add tests asserting the header bytes, division, chunk lengths, and nonnegative deltas for a small well-formed export.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiFileWriter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes.
- **Notes**: The writer's only job is encoding the already-resolved sorted stream (§43).

### Subtask T032 – VLQ boundary tests + range rejection

- **Purpose**: Correct VLQ across boundaries; reject unrepresentable values (spec §44).
- **Steps**:
  1. Add VLQ boundary tests for `0`, `0x7F`, `0x80`, `0x3FFF`, `0x4000`, `0x1FFFFF`, `0x200000`, `0x0FFFFFFF`.
  2. Round-trip or compare against expected byte sequences.
  3. Assert values outside the representable MIDI VLQ range are rejected with an error, not truncated (§44).
  4. Ensure no negative VLQ delta is silently produced (the writer should reject negative input).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiFileWriter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes.
- **Notes**: VLV is `MidiFileWriter`-owned encoding; do not move it elsewhere.

### Subtask T033 – EOT exactly once per track; tempo precedes note-on

- **Purpose**: Every track ends with exactly one effective EOT; tempo at a boundary precedes same-tick Note-On (§35, §46).
- **Steps**:
  1. Verify each track (conductor + musical) ends with exactly one effective EOT, its delta computed normally from the previous event (§46).
  2. Add tests asserting one EOT per track and that no EOT fix-up is delegated to a parser.
  3. Verify a tempo event at tick `T` precedes a Note-On at the same `T` (§35).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiFileWriter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes.
- **Notes**: EOT emission stays writer-owned; WP05 asserts it at the exporter-output level.

### Subtask T034 – Byte-identical deterministic output

- **Purpose**: Same timeline/options/PPQ → identical bytes, with no unstable ordering (spec §45).
- **Steps**:
  1. Add a byte-for-byte determinism regression: export the same timeline twice → identical byte arrays.
  2. Audit the writer's sort for reliance on dictionary enumeration, hash codes, object identity, thread scheduling, or unstable equal-key sorting; replace with a stable deterministic comparator if needed (§45).
  3. Add a test that runs the deterministic export under a forced shuffled input to confirm byte-identical output.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiFileWriter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiEvent.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MusicalMidiExporterTests.cs`.
- **Parallel?**: Yes.
- **Notes**: Determinism is a DoD criterion (spec §77); this test is the guard.

### Subtask T035 – DAC timing separation + origin-gap fix

- **Purpose**: DAC exporter performs no independent sample→tick math and has no hidden origin/negative-tick gap; DAC identity is independent of timing (§37, §38, test §68).
- **Steps**:
  1. Confirm `DacMidiExporter` receives an already-established map and does not establish tempo or convert from sampleRate/BPM (§37 "Preferred design").
  2. Fix the DAC exporter's missing origin shift / negative-tick gap: since the core exporter applies a global nonnegative origin, the DAC path must use the **same** origin policy so a DAC trigger at a negative source quarter cannot emit a negative tick (coordinate with WP05 T021 so DAC shares the origin; do not introduce a second origin).
  3. Add/confirm the §68 test: same timed DAC sequence under two different valid sample-ID→note mappings → note numbers may differ, **event ticks identical**, **tempo track identical**.
  4. Add a regression that a DAC trigger at a negative quarter yields a nonnegative tick after the shared origin.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Dac/DacMidiExporter.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/Dac/MidiFileWriter.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/DacMidiExporterTests.cs`.
- **Parallel?**: No — depends on T031–T034 and the WP05 origin.
- **Notes**: `raw PCM → canonical ID → MIDI note` must stay fully independent from `trigger sample → tick` (§38). The DAC-specific `MidiFileWriter` (separate from the core) may remain only if it does not become a second timing authority.

---

## Test Strategy

Required tests in `MusicalMidiExporterTests.cs` (writer level) and `DacMidiExporterTests.cs`:

- Format 1 header/division/chunk lengths/nonnegative deltas.
- VLQ boundaries + range rejection (§44).
- One EOT per track (§46); tempo-before-note-on (§35).
- Byte-for-byte determinism (§45).
- DAC §68 identity-independence + origin-gap regression.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MusicalMidiExporterTests|FullyQualifiedName~DacMidiExporterTests"
```

## Risks & Mitigations

- **Risk**: Writer learns source samples/BPM. → Mitigation: keep it serialization-only (§43); tests assert no sample math.
- **Risk**: VLQ truncation of out-of-range values. → Mitigation: T032 rejection tests.
- **Risk**: Unstable equal-key sort → non-deterministic bytes. → Mitigation: T034 byte-identical regression.
- **Risk**: DAC negative tick (no origin). → Mitigation: T035 shared-origin fix + regression.

## Review Guidance

- Verify the writer is serialization-only and deterministic (Format 1, PPQ, VLQ, EOT).
- Verify VLQ reject instead of truncate (§44).
- Verify DAC shares the origin and performs no independent sample→tick conversion; §68 test holds.

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