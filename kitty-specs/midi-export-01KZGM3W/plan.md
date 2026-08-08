# Implementation Plan: MDPlayer MIDI Export Timing Hardening

**Branch**: `feature/linux-fmp-renderer` | **Date**: 2026-08-08 | **Spec**: `kitty-specs/midi-export-01KZGM3W/spec.md`
**Input**: Authoritative coding-agent specification at the path above (FR-001..FR-005).

## Summary

Harden the existing MDPlayer MIDI timing pipeline rather than introducing a second timing system. The implementation will audit the timeline producers first, establish the actual meaning of `BeatEvent.BeatIndex` and `DriverTimingEvent.ValidatedBpm`, and verify that source events share the final playback/output sample clock. When a producer clock differs, exactly one explicit normalization adapter will run at the producer boundary; ambiguous or unprovable clocks will be rejected instead of being compensated for in MIDI serialization.

The existing `MusicalTimeMapBuilder` remains the sole coordinator for timing-source precedence, beat-unit normalization, phase, validated tempo transitions, segment continuity, strict failures, and diagnostics. `MusicalTimeMap` remains the sole source-sample-to-quarter/tick authority. `MusicalMidiExporter` will consume that map for all melodic, pitch, rhythm, DAC, marker, loop, and conductor events, apply one global nonnegative tick-origin shift, and sort events with explicit deterministic priorities. `MidiFileWriter` remains a serialization-only Format 1 writer and will be hardened for VLQ, chunk, delta, tempo, ordering, and EOT invariants. Existing CLI wiring will expose the required source/BPM/phase/meter/downbeat/report/strict semantics without creating another command framework.

## Technical Context

**Language/Version**: C# in the existing MDPlayer .NET solution; nullable annotations are enabled in timing files. Use the repository's existing target framework and language settings; no version migration is part of this mission.

**Primary Dependencies**: Existing MDPlayer FMP Core/Application/CLI projects, `System.Text.Json` for timeline/report JSON, and the existing MIDI event/writer types. Do not add a timing library, audio beat detector, or major external dependency.

**Storage**: File-based artifacts only: source timelines may be loaded through `VisualizationJsonWriter.Read`, MIDI output is a Standard MIDI File, and `--timing-report` writes compact JSON. No database or new persistence layer.

**Testing**: Extend the existing `MDPlayer/tests/MDPlayer.Fmp.Tests` test project and its current unit/integration conventions. Cover producer semantics, map invariants, robust fitting, tempo boundaries, conductor/event ordering, DAC/rhythm sharing, writer/VLQ/EOT validity, deterministic bytes, CLI/report semantics, and an independent parser round trip where an existing parser is available. The required ten-minute no-drift and one-tick anchor criteria come from the spec; no new validation command is introduced by planning.

**Target Platform**: Existing MDPlayer Linux-oriented FMP renderer/CLI branch and its supported runtime targets. The output contract is platform-neutral Standard MIDI File Format 1 with configurable positive PPQ (default 960).

**Project Type**: Existing multi-project C# application/library solution: FMP Core owns timing and event construction, FMP Application owns export orchestration, FMP CLI owns command parsing/reporting, and the FMP test project owns regression/integration coverage.

**Performance Goals**: Preserve normal `O(n log n)` stable event sorting and linear map lookup preparation; use absolute conversion from original samples so error does not grow with timeline duration. Do not trade correctness for speculative optimization.

**Constraints**: Keep one `MusicalTimeMap`; do not rewrite `BeatGridFitter` wholesale; do not infer audio BPM; do not assume sample zero is beat zero, FMP beat equals quarter, first beat is downbeat, or BPM establishes phase; do not default-quantize; preserve pickup notes, unknown meter/downbeat, and explicit strict/non-strict fallback behavior. Reject conflicting anchors, invalid/ambiguous clocks, invalid BPM/PPQ, unresolved strict alignment, and impossible segment state with diagnostic errors.

**Scale/Scope**: One existing MIDI export path and its shared visualization timeline, including notes, pitch changes, rhythm events, YM2612 DAC triggers, markers/loops, timing segments, CLI options, diagnostics, and the existing test corpus. No UI, video, visualization-rendering, chip-emulation, or unrelated playback refactor.

## Charter Check

**Skipped** — no charter file is present in the mission or repository paths inspected. No charter-derived gates or violations are asserted; the authoritative spec and the explicit producer-boundary normalization decision govern this plan.

## Project Structure

### Documentation (this mission)

```
kitty-specs/midi-export-01KZGM3W/
├── plan.md                 # this grounded implementation plan
├── research.md             # Phase 0 producer/source audit and decisions
├── data-model.md           # Phase 1 timing/event/report model
├── quickstart.md           # Phase 1 implementation and review sequence
└── contracts/
    └── timing-report.schema.json  # compact stable --timing-report contract
```

`tasks.md` and `tasks/` are intentionally not created or modified by this planning assignment.

### Source Code (repository root)

```
MDPlayer/src/MDPlayer.Fmp.Core/
├── Timing/
│   ├── MusicalTimeMap.cs              # absolute sample ↔ quarter/tick authority
│   ├── MusicalTimeMapBuilder.cs       # source precedence, normalization, fit/map assembly
│   ├── MusicalTimeMapOptions.cs       # source/BPM/phase/meter/downbeat/strict options
│   ├── BeatGridFitter.cs               # validated robust anchor fitting and residuals
│   ├── TimingDiagnostics.cs            # compact diagnostics and rejected-anchor details
│   ├── TempoSegment.cs                 # continuous piecewise musical-time segments
│   ├── TimingSource.cs                  # source selection values
│   ├── MusicalTimingException.cs        # actionable timing failures
│   └── Midi/
│       ├── MusicalMidiExporter.cs      # shared-map events, origin, conductor, priorities
│       ├── MidiEvent.cs                 # stable event metadata/priority if needed
│       └── MidiFileWriter.cs            # serialization-only SMF Format 1/VLQ/EOT
├── Visualization/
│   ├── VisualizationTimeline.cs        # timeline event contracts and sample positions
│   ├── TimelineBuilder.cs              # timing/beat ingestion and ordering boundary
│   ├── ChipTimelineDecoderRegistry.cs  # capture sink and decoder producer routing
│   ├── Ym2608TimelineDecoder.cs        # observed runtime DriverTimingEvent producer
│   └── Dac/DacMidiExporter.cs          # DAC identity-to-note with shared timing mapper
└── Analysis/FmpSymbolicNormalizer.cs   # existing symbolic fallback input validation

MDPlayer/src/MDPlayer.Fmp.Application/Export/
├── MidiExportRequest.cs                # application request/options model
└── MidiExportService.cs                # application map/export orchestration

MDPlayer/src/MDPlayer.Fmp.Cli/
├── MidiOptions.cs                      # existing MIDI option parser/validation
├── MidiCommand.cs                      # map/export/report/failure wiring
└── TimelineCaptureService.cs           # captured or JSON timeline boundary

MDPlayer/tests/MDPlayer.Fmp.Tests/
├── MusicalMidiExporterTests.cs         # map-to-MIDI event/export regressions
├── DacMidiExporterTests.cs             # DAC timing/identity regressions
├── MidiExportServiceTests.cs            # application integration behavior
├── MidiPitchAccuracyTests.cs           # pitch/bend behavior
├── Visualization/VisualizationV3ContractTests.cs # timeline builder contracts
└── Analysis/AnalysisCoreTests.cs       # symbolic/timing input validation
```

**Structure Decision**: Keep the existing separation. Timing evidence enters through `VisualizationTimeline`/`TimelineBuilder`; `MDPlayer.Fmp.Core/Timing` constructs the one map; `Timing/Midi` emits absolute events and serializes them; application/CLI only translate existing request/options and report results; tests stay in the existing FMP test project. The only new planning contract is the stable JSON shape for `--timing-report`.

## Complexity Tracking

No Charter Check violations identified; this section is intentionally empty.

## Implementation Concern Map

Implementation concerns are architectural boundaries for later task decomposition, not work packages or executable task files.

### IC-01 — Producer semantics and sample-clock normalization

- **Purpose**: Audit every `AddBeat`/`AddTiming` path and every timed note/pitch/rhythm/DAC event, document units/activation/loop behavior, and normalize a differing producer clock once at its producer boundary while rejecting ambiguous clocks.
- **Relevant requirements**: FR-001, FR-002, FR-005.
- **Affected surfaces**: `Visualization/TimelineBuilder.cs`, `Visualization/VisualizationTimeline.cs`, `Visualization/Ym2608TimelineDecoder.cs`, `Visualization/ChipTimelineDecoderRegistry.cs`, `Visualization/VisualizationJsonWriter.cs`, capture/timeline JSON boundary, producer-audit tests.
- **Sequencing/depends-on**: none; this is the prerequisite for fitting changes.
- **Risks**: Current source search finds runtime `DriverTimingEvent` creation in YM2608 timer-B decoding, while `BeatEvent` is ingested/merged and supplied by timeline data/tests rather than constructed by a runtime decoder. Do not infer a clock conversion or BeatIndex unit from record names; reject an unresolved clock rather than hiding it in the writer.

### IC-02 — Canonical beat fitting, source precedence, and diagnostics

- **Purpose**: Make `MusicalTimeMapBuilder` and the existing `BeatGridFitter` enforce explicit source modes, one-time BeatIndex-to-quarter normalization, robust anchor validation/outlier reporting, authoritative phase, strict failures, and compact diagnostics.
- **Relevant requirements**: FR-001, FR-002, FR-003, FR-005.
- **Affected surfaces**: `Timing/MusicalTimeMapBuilder.cs`, `Timing/BeatGridFitter.cs`, `Timing/MusicalTimeMapOptions.cs`, `Timing/TimingDiagnostics.cs`, `Timing/TimingSource.cs`, `Timing/MusicalTimingException.cs`, fitter/builder tests.
- **Sequencing/depends-on**: IC-01.
- **Risks**: Preserve phase established by authoritative anchors when validated BPM supplies rate; deduplicate identical anchors but never average conflicts; handle actual loop resets only after producer semantics are known; keep symbolic inference as fallback and do not expand it into a new engine.

### IC-03 — Absolute musical map and continuous tempo segments

- **Purpose**: Preserve negative/fractional quarter positions, absolute sample-based conversion, piecewise tempo continuity, segment-boundary semantics, and no cumulative rounding drift.
- **Relevant requirements**: FR-001, FR-003.
- **Affected surfaces**: `Timing/MusicalTimeMap.cs`, `Timing/TempoSegment.cs`, `Timing/MusicalTimeMapBuilder.cs`, `Timing/BeatGridFitter.cs`, long-duration/continuity tests.
- **Sequencing/depends-on**: IC-02.
- **Risks**: Build the next segment quarter origin from the preceding segment in double precision, then round only final MIDI ticks; never independently recalculate rounded segment origins or incrementally accumulate deltas.

### IC-04 — Shared MIDI event projection and conductor/origin policy

- **Purpose**: Map all source event kinds through the shared map, apply exactly one global nonnegative tick shift, preserve pickups/unknown meter/downbeat, emit conductor tempo/meter/markers, and enforce minimum positive note duration.
- **Relevant requirements**: FR-001, FR-003, FR-005.
- **Affected surfaces**: `Timing/Midi/MusicalMidiExporter.cs`, `Timing/Midi/MidiEvent.cs`, `Visualization/Dac/DacMidiExporter.cs`, timeline event contracts, exporter tests.
- **Sequencing/depends-on**: IC-03.
- **Risks**: Map note endpoints and pitch samples independently across tempo transitions; route rhythm/DAC/loops/markers through the same origin; do not snap pickup notes or fabricate 4/4; preserve DAC sample identity independence from timing.

### IC-05 — Deterministic event ordering and pitch policy

- **Purpose**: Encode explicit same-tick semantic ordering, stable secondary keys, retrigger Off→On behavior, and one documented/clamped pitch-bend range with RPN setup.
- **Relevant requirements**: FR-001, FR-003, FR-004.
- **Affected surfaces**: `Timing/Midi/MidiEvent.cs`, `Timing/Midi/MusicalMidiExporter.cs`, `Timing/Midi/MidiFileWriter.cs`, pitch/retrigger tests.
- **Sequencing/depends-on**: IC-04.
- **Risks**: Never rely on insertion, dictionary, hash, object identity, or unstable equal-key sorting; tempo at a boundary must precede Note On at the same tick while Note Off remains first.

### IC-06 — Standard MIDI serialization correctness

- **Purpose**: Keep `MidiFileWriter` timing-agnostic while enforcing Format 1 headers, configured PPQ, valid chunks, nonnegative deltas, 24-bit tempo values, VLQ boundaries/range rejection, deterministic bytes, and one effective EOT per track.
- **Relevant requirements**: FR-001, FR-003, FR-004.
- **Affected surfaces**: `Timing/Midi/MidiFileWriter.cs`, `Timing/Midi/MidiEvent.cs`, writer boundary/round-trip tests and existing parser if available.
- **Sequencing/depends-on**: IC-05.
- **Risks**: Writer must consume already-sorted absolute events only; it must not know source samples, BPM fitting, or phase. Do not mask invalid negative/overflow values by truncating or silently clamping outside the specified representable range.

### IC-07 — Existing CLI/application behavior and timing report

- **Purpose**: Wire current `MidiOptions`, `MidiCommand`, and application request/service paths to source modes, BPM, phase, meter/downbeat, PPQ, strict behavior, actionable fallback errors/warnings, and the compact report contract.
- **Relevant requirements**: FR-003, FR-004, FR-005.
- **Affected surfaces**: `MDPlayer.Fmp.Cli/MidiOptions.cs`, `MDPlayer.Fmp.Cli/MidiCommand.cs`, `MDPlayer.Fmp.Application/Export/MidiExportRequest.cs`, `MidiExportService.cs`, `contracts/timing-report.schema.json`, CLI/service tests.
- **Sequencing/depends-on**: IC-02, IC-04, IC-06.
- **Risks**: Extend the existing command; do not create another command framework. `auto` must choose strongest evidence, `driver` must fail when unavailable, `fixed` must require finite positive BPM, strict mode must reject ambiguity, and non-strict fallback must never be called beat aligned. Report PPQ must reflect configured PPQ rather than a placeholder.

### IC-08 — End-to-end regression matrix and acceptance evidence

- **Purpose**: Protect the complete FR-001..FR-005 contract with focused tests for constant/nonzero phase, nonzero BeatIndex, pickups, duplicates/conflicts, jitter/outliers, tempo boundaries, zero-tick durations, retriggers, meter/downbeat, loops, DAC/rhythm sharing, ten-minute drift, parser round trip, and byte determinism.
- **Relevant requirements**: FR-001, FR-002, FR-003, FR-004, FR-005.
- **Affected surfaces**: `MDPlayer/tests/MDPlayer.Fmp.Tests/` existing timing/export/visualization/analysis test files and any narrowly necessary additions within that project.
- **Sequencing/depends-on**: IC-01 through IC-07 as applicable; tests must lock discovered semantics before downstream fitting changes.
- **Risks**: Tests must assert observable MIDI/timing behavior and one-tick musical tolerances, not incidental implementation details. Do not add acceptance scope beyond the authoritative spec.
