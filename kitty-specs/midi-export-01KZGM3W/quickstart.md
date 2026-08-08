# Phase 1 Quickstart — MIDI Export Timing Hardening

This is the implementation sequence for the existing branch. It deliberately follows the authoritative spec's Batch A–H order and does not create task files.

## Before implementation

1. Read `spec.md`, `research.md`, and `data-model.md` together.
2. Keep all work inside the existing FMP Core/Application/CLI MIDI path and `MDPlayer/tests/MDPlayer.Fmp.Tests`.
3. Do not create a second timing map, command framework, audio BPM detector, default quantizer, or unrelated renderer/UI refactor.
4. Treat a producer clock as valid only when its source and destination relationship are explicit. Otherwise reject it before map construction.

## Batch A — lock producer semantics first

Inspect every `TimelineBuilder.AddBeat` and `AddTiming` path and the timed event producers identified in `research.md`.

Record and test:

- `BeatIndex` unit, fractional/jump/nonzero behavior, loop reset behavior, and same-sample ordering;
- `ValidatedBpm` effective-playback semantics and the sample at which a value activates;
- sample-clock relationship for timing, beats, note endpoints, pitch changes, rhythm, DAC, markers, and loops;
- exactly one producer-boundary normalization adapter for a proven clock mismatch;
- actionable rejection for an ambiguous clock or unit.

Do not compensate for bad source timestamps in the MIDI writer.

## Batch B — map invariants

Use the existing `MusicalTimeMap` and `MusicalTimeMapBuilder` to lock:

- absolute sample-to-quarter and sample-to-tick conversion;
- negative quarter positions and nonzero phase;
- segment boundary continuity in double precision;
- ten-minute constant-tempo no-drift behavior;
- one global origin offset only after all raw musical positions are known.

## Batch C — driver fitting

Harden `BeatGridFitter` without replacing it:

- normalize the audited beat unit once;
- sort anchors, deduplicate identical anchors, reject/report conflicts;
- normalize documented loop resets once, not throughout MIDI code;
- retain robust inlier/outlier behavior and diagnostics;
- establish authoritative phase separately from tempo;
- make strict mode fail for unresolved fundamental ambiguity.

## Batch D — tempo segments

Integrate validated tempo transitions and anchor evidence:

- create a segment at each accepted sustained transition;
- thread quarter origin from the previous segment;
- suppress jitter-driven segment spam;
- preserve phase through tempo changes;
- test notes/pitch/anchors exactly at and across boundaries.

## Batch E — origin and conductor

In `MusicalMidiExporter`:

- map all events to absolute ticks first;
- compute one global nonnegative shift, including pickups;
- emit Set Tempo from segment MIDI values, deduplicating adjacent equal emitted values;
- emit Time Signature only for known meter and preserve unknown meter/downbeat;
- map markers/loops with no quantization;
- keep conductor-only state on track 0.

## Batch F — musical events

- map note starts and ends independently;
- enforce one tick for a positive source note that rounds to zero duration;
- map every pitch sample independently;
- use one explicit clamped bend-range policy and RPN setup;
- enforce same-tick Off, conductor state, setup, bend, On, metadata priority;
- route rhythm and DAC triggers through the same map and origin;
- keep DAC identity-to-note mapping independent from timing.

## Batch G — writer

Keep `MidiFileWriter` serialization-only and harden:

- Format 1 header and configured PPQ;
- sorted absolute event stream and nonnegative deltas;
- VLQ boundary/range behavior;
- tempo 24-bit representability;
- chunk lengths and exactly one effective EOT per track;
- byte-identical output for repeated exports.

Use an independent existing MIDI parser for round-trip checks if one is already available; do not add a major dependency solely for this test.

## Batch H — CLI/application/report

Extend the existing `MidiOptions`, `MidiCommand`, `MidiExportRequest`, and `MidiExportService` paths:

- `--ppq` (positive, default 960);
- `--tempo-source auto|driver|symbolic|fixed`;
- finite positive `--bpm` required for fixed;
- documented `--beat-offset-samples` convention;
- optional `--meter` and compatible `--first-downbeat-sample`;
- `--timing-report` using `contracts/timing-report.schema.json`;
- `--strict-timing` failures and non-strict explicit fallback diagnostics.

Do not call BPM-only, phase-unknown output beat aligned.

## Evidence checklist for implementation review

The eventual implementation should be reviewable against the five functional requirements:

- all event families use `MusicalTimeMap` and show no cumulative drift;
- every timing/beat producer and clock relationship is documented, normalized once when proven, or rejected when ambiguous;
- source precedence, phase, meter/downbeat, segments, origin, ordering, retriggers, zero-tick notes, DAC/rhythm, CLI, and diagnostics follow the spec;
- output is deterministic, valid SMF Format 1 with configured PPQ, valid VLQs, nonnegative deltas, and EOT on every track;
- unknown alignment/meter/downbeat remains unknown, default export is unquantized, and source-mode/strict/fallback behavior is visible.
