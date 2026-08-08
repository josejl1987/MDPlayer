---
work_package_id: WP06
title: CLI, Timing Report & Integration Matrix
dependencies: ["WP03", "WP04", "WP05"]
requirement_refs:
- FR-003
- FR-004
- FR-005
planning_base_branch: feature/linux-fmp-renderer
merge_target_branch: feature/linux-fmp-renderer
branch_strategy: Planning artifacts for this mission were generated on feature/linux-fmp-renderer. During /spec-kitty.implement this WP may branch from a dependency-specific base, but completed changes must merge back into feature/linux-fmp-renderer unless the human explicitly redirects the landing branch.
subtasks:
- T036
- T037
- T038
- T039
- T040
phase: Phase 6 - CLI & integration
history:
- at: '2026-08-08T12:37:11Z'
  actor: system
  action: Prompt generated via /spec-kitty.tasks
agent_profile: implementer-ivan
authoritative_surface: MDPlayer/src/MDPlayer.Fmp.Cli/
create_intent:
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiIntegrationRoundTripTests.cs
execution_mode: code_change
model: ''
owned_files:
- MDPlayer/src/MDPlayer.Fmp.Cli/MidiOptions.cs
- MDPlayer/src/MDPlayer.Fmp.Cli/MidiCommand.cs
- MDPlayer/src/MDPlayer.Fmp.Cli/TimelineCaptureService.cs
- MDPlayer/src/MDPlayer.Fmp.Application/Export/MidiExportRequest.cs
- MDPlayer/src/MDPlayer.Fmp.Application/Export/MidiExportService.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiIntegrationRoundTripTests.cs
- MDPlayer/tests/MDPlayer.Fmp.Tests/MidiExportServiceTests.cs
role: implementer
tags: []
task_type: implement
tracker_refs: []
---

# Work Package Prompt: WP06 – CLI, Timing Report & Integration Matrix

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

This WP delivers the user-facing surface and the end-to-end acceptance matrix (plan IC-07, IC-08; spec §11, §47–§50, §70). When complete:

1. The existing MIDI command/options (no new command framework) expose `--ppq`, `--tempo-source auto|driver|symbolic|fixed`, `--bpm`, `--beat-offset-samples`, `--meter`, `--first-downbeat-sample`, `--timing-report`, `--strict-timing` with the documented semantics (§47, §48).
2. `--tempo-source auto` selects the strongest trustworthy evidence and reports it; `driver` fails when authoritative timing is unavailable; `symbolic` forces the existing inference; `fixed` requires finite positive BPM (§48).
3. There is **no silent 120 BPM fallback**; strict mode rejects unresolved alignment; any non-strict fallback is explicit in diagnostics and never labeled beat-aligned (§16, §48, §49).
4. `--timing-report` writes the compact, stable JSON contract in `contracts/timing-report.schema.json` with the configured PPQ (no placeholder) (§50).
5. `MidiExportService`/`MidiExportRequest` align to the same map/options surface (GUI parity).
6. End-to-end integration: parse the generated MIDI with an independent existing parser (if available) and verify Format 1, configured division, track count, tempo values, event ticks, and EOT; cover the FR-005 acceptance matrix (auto/driver/fixed/strict/fallback behavior).

## Context & Constraints

- **Spec**: §11, §47–§50, §70; FR-005.
- **Plan**: `kitty-specs/midi-export-01KZGM3W/plan.md` IC-07, IC-08.
- **Data model**: `data-model.md` §3 (strict vs non-strict), §6 (timing-report contract).
- **Contracts**: `kitty-specs/midi-export-01KZGM3W/contracts/timing-report.schema.json`.
- **Quickstart**: Batch H.
- **Constraint**: Do **not** create a second MIDI command or rewrite the CLI framework (§47). Extend the existing command/options. Do not add a major external dependency solely for the parser test (§70).

## Grounded source evidence (verified 2026-08-08)

- `MidiOptions : BatchRenderSettings` props: `Input` string (no default), `Output` string, `Timeline` string, `Ppq` int=960, `TempoSource MidiTempoSource=Auto`, `Bpm double?`, `BeatOffsetSamples long?`, `Meter Meter?`, `FirstDownbeatSample long?`, `Quantize string="off"`, `TimingReport string?`, `StrictTiming bool=false`, `EmitPitchBend bool=true`, `BendRange int=2`, `UsePercussionChannel bool=true`, `OutputWriter TextWriter=Console.Out`. Parser already maps `--timeline/--output/-o/--ppq/--tempo-source/--bpm/--beat-offset-samples/--meter/--first-downbeat-sample/--quantize/--timing-report/--strict-timing/--no-pitch-bend/--bend-range/--no-percussion-channel`.
- `MidiCommand.Run`: `TimelineCaptureService.Capture(options.Input, options.Timeline, …)`; maps tempo source `Driver→DriverBeatAnchors`, `Symbolic→SymbolicInference`, `Fixed→UserOverride` (fixed requires `Bpm`); builds `MusicalTimeMapOptions{FixedBpm,BeatOffsetSamples,Meter,FirstDownbeatSample,Source,StrictTiming,DetectTempoChanges=true}`; `MusicalTimeMapBuilder.Build`; builds `MusicalMidiExportOptions{Quantize,EmitPitchBend,BendRangeSemitones,UsePercussionChannel}` + `MusicalMidiExporter(map,Ppq,opts)`; `Export(timeline)`; writes bytes + tempo/phase/warnings; optional JSON timing report. Errors: `MusicalTimingException` → exit 5 (strict) / 4 (non-strict), other → 7.
- `TimelineCaptureService.Capture`: existing timeline path → `VisualizationJsonWriter.Read`; FMP ext → `CaptureFmpTimeline`; backend path probes backend native rate if >0 else settings rate.
- `MidiExportRequest` (app): `Ppq int=960`, `TempoSource Fmp.Application.Export.MidiTempoSource=Auto`, `Bpm double?`, `BeatOffsetSamples long?`, `Meter string?`, `FirstDownbeatSample long?`, `Quantize string="off"`, `EmitPitchBend bool=true`, `BendRangeSemitones int=2`, `UsePercussionChannel bool=true`, `Title string?`, `SourceFormat string?`, `Velocity int=90`, `EmitMarkers bool=true`, `EmitConductorMetadata bool=true`, `VoiceOptions IReadOnlyList<MidiVoiceOption>`. `MidiExportService.Export(VisualizationTimeline, MidiExportRequest)` builds the map via `ToMapOptions`, uses core `MusicalMidiExporter`, returns bytes/segments+sources/report; public `ExportFromTimelinePath` reads JSON then exports.

## Branch Strategy

- **Strategy**: Planning artifacts were generated on `feature/linux-fmp-renderer`; completed changes must merge back into `feature/linux-fmp-renderer`.
- **Planning base branch**: `feature/linux-fmp-renderer`
- **Merge target branch**: `feature/linux-fmp-renderer`

> These fields are populated automatically by `spec-kitty agent mission tasks`.
> Do NOT change them manually unless you are certain the branch topology has changed.

---

## Subtasks & Detailed Guidance

### Subtask T036 – CLI option semantics and validation

- **Purpose**: Expose the spec's option set with correct validation (§47, §48).
- **Steps**:
  1. Verify `--ppq` is positive and MIDI-valid (≤32767); default 960 (§48).
  2. Verify `--tempo-source` values `auto|driver|symbolic|fixed` map to the right `TimingSource` (the existing `MidiTempoSource` mapping is the guide; confirm `fixed → UserOverride` requires `Bpm`).
  3. Verify `--bpm` is finite and positive for `fixed`; reject `0`, negative, NaN, Infinity (§48).
  4. Verify `--beat-offset-samples` documents one sign convention ("sample position at which quarter position zero occurs") and is used consistently everywhere (no multiple synonymous phase options) (§48).
  5. Verify `--meter` does not default to 4/4, and `--first-downbeat-sample` requires compatible meter info (§48).
  6. Add `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs` with parameterized option-validation cases (invalid ppq/bpm/meter, source constraints, offset-sign convention).
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Cli/MidiOptions.cs`, `MDPlayer/src/MDPlayer.Fmp.Cli/MidiCommand.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs` (new).
- **Parallel?**: Yes.
- **Notes**: Extend the existing command; do not create a second one (§47).

### Subtask T037 – Timing-source wiring, no silent 120 BPM, non-strict fallback reporting

- **Purpose**: Honor `auto`/`driver`/`fixed`/`symbolic` and strict/non-strict semantics with visible fallback (spec §11, §16, §48, §49).
- **Steps**:
  1. Verify `auto` selects the strongest trustworthy source and reports which was selected (§48; no `120 BPM, sample 0 = beat 0` silent claim).
  2. Verify `driver` fails when authoritative driver timing is unavailable (throws `MusicalTimingException`), never silently switching to symbolic inference (§11).
  3. Verify `fixed` requires `Bpm`, and that a fixed BPM with no phase is reported as non-beat-aligned in diagnostics (§11, §49).
  4. Verify strict mode has **no** silent 120 BPM fallback (§16), and non-strict fallback (if retained) is explicit in `Warnings` and never labeled beat-aligned (§49).
  5. Add CLI/service tests asserting: `auto` picks the strongest source; `driver` fails without authority; `fixed` without BPM fails; strict fails on unresolved alignment; fallback output is visibly reported.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Cli/MidiCommand.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingDiagnostics.cs` (coordination with WP03), `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiExportServiceTests.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs`.
- **Parallel?**: No — depends on T036 + WP03 diagnostics.
- **Notes**: Error messages must say what is actually wrong (§76), e.g. "Cannot establish MIDI beat phase… Provide an explicit beat offset or disable strict timing."

### Subtask T038 – `--timing-report` JSON contract

- **Purpose**: Serialize compact, stable diagnostics to the contract (spec §50; `contracts/timing-report.schema.json`).
- **Steps**:
  1. Wire `Options.TimingReport` to write the JSON report per the schema in `kitty-specs/midi-export-01KZGM3W/contracts/timing-report.schema.json`.
  2. Fields must include `sampleRate`, `ppq` (the **configured** PPQ, not a placeholder), `source`, `phaseAuthoritative`/`tempoAuthoritative`, `originTickOffset`, nullable `meter`/`downbeatKnown`, anchor counts/residuals, segment starts/quarter origins/BPM, and warnings (§50).
  3. Use existing property naming conventions where practical; do not introduce an elaborate diagnostics framework (§50).
  4. Add a test asserting the emitted JSON conforms to the schema and reports the configured PPQ.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Cli/MidiCommand.cs`, `MDPlayer/src/MDPlayer.Fmp.Core/Timing/TimingDiagnostics.cs` (coordination with WP03), `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs`.
- **Parallel?**: Yes.
- **Notes**: The report is a projection of resolved map/diagnostics, not a second source of truth (data-model §6).

### Subtask T039 – GUI service parity

- **Purpose**: `MidiExportService`/`MidiExportRequest` align to the same map/options/CLI surface so GUI and CLI behave identically (§11, plan IC-07).
- **Steps**:
  1. Confirm `MidiExportService.Export` builds the map through the same `MusicalTimeMapBuilder`/`MusicalTimeMapOptions` path as the CLI.
  2. Align source-mode resolution (`MidiTempoSource`) and strict-timing behavior across app and CLI (no divergent defaults).
  3. Align `MidiExportRequest` tempo/phase/meter/downbeat/strict fields to the same semantics.
  4. Add/extend `MidiExportServiceTests` covering the app path with the same acceptance checks as the CLI.
- **Files**: `MDPlayer/src/MDPlayer.Fmp.Application/Export/MidiExportRequest.cs`, `MDPlayer/src/MDPlayer.Fmp.Application/Export/MidiExportService.cs`, `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiExportServiceTests.cs`.
- **Parallel?**: Yes.
- **Notes**: GUI parity prevents the two entry points from drifting (plan IC-07, §11).

### Subtask T040 – End-to-end acceptance matrix + parser round trip

- **Purpose**: Prove FR-005 behavior end to end and validate generated MIDI with an independent parser (spec §70, §77 DoD).
- **Steps**:
  1. Add `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiIntegrationRoundTripTests.cs` that runs the full timeline→map→exporter→writer path and, if an independent existing MIDI parser is available in the repo, loads the output and verifies format==1, division==PPQ, track count, tempo values, event ticks, and EOT.
  2. If no parser exists, do not add a major dependency solely for this test (§70) — fall back to a raw-byte structural check (header, chunks, VLQs) and note the substitution.
  3. Add the FR-005 acceptance matrix tests: `auto` strongest evidence; `driver` fails without authority; `fixed` requires BPM; strict rejects unresolved alignment; non-strict fallback visibly reported.
  4. Add a byte-determinism test at the end-to-end level (same input → same bytes).
- **Files**: `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiIntegrationRoundTripTests.cs` (new), `MDPlayer/src/MDPlayer.Fmp.Application/Export/MidiExportService.cs`.
- **Parallel?**: No — depends on T036–T039 + WP03–WP05 surfaces.
- **Notes**: This is the acceptance-evidence WP; it asserts the full DoD (§77) end to end.

---

## Test Strategy

- `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiCliOptionTests.cs` (new): CLI option validation.
- `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiExportServiceTests.cs`: source-mode/strict/fallback behavior + GUI parity.
- `MDPlayer/tests/MDPlayer.Fmp.Tests/MidiIntegrationRoundTripTests.cs` (new): parser round trip + FR-005 matrix.

Run:

```bash
cd /home/jose/MDPlayer && dotnet test tests/MDPlayer.Fmp.Tests/ --filter "FullyQualifiedName~MidiCliOptionTests|FullyQualifiedName~MidiExportServiceTests|FullyQualifiedName~MidiIntegrationRoundTripTests"
```

## Risks & Mitigations

- **Risk**: New CLI framework or second MIDI command. → Mitigation: §47 discipline; extend existing.
- **Risk**: Silent 120 BPM fallback. → Mitigation: T037 strict/no-fallback + §49 reporting.
- **Risk**: Report PPQ placeholder instead of configured PPQ. → Mitigation: T038 schema-conformance test.
- **Risk**: GUI/CLI divergence. → Mitigation: T039 parity tests.
- **Risk**: Major dependency for the parser test. → Mitigation: T040 uses existing parser or byte-structural fallback (§70).

## Review Guidance

- Verify all option semantics (§47/§48) with the option-validation tests.
- Verify no silent 120 BPM fallback and visible non-strict fallback (§16/§49).
- Verify the report conforms to the schema with configured PPQ (§50).
- Verify GUI/CLI parity and the FR-005 acceptance matrix.
- Verify the parser round trip (or documented structural fallback) per §70.

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