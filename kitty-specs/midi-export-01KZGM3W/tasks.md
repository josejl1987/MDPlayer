# Work Packages — MIDI Export Timing Hardening

**Branch**: `feature/linux-fmp-renderer` | **Date**: 2026-08-08 | **Spec**: `spec.md` | **Plan**: `plan.md`

Execution root: repository checkout at `/home/jose/MDPlayer` on branch `feature/linux-fmp-renderer`. Execution worktrees are allocated per computed lane from `lanes.json` after `finalize-tasks`. Every work package below lands back on `feature/linux-fmp-renderer`.

## Subtask Index

| ID | Description | WP | Parallel |
|----|-------------|----|----------|
| T001 | Audit every AddTiming/AddBeat producer; document BeatIndex unit/loop/reset/nonzero semantics | WP01 | |
| T002 | Document DriverTimingEvent.ValidatedBpm effective semantics + activation sample | WP01 | |
| T003 | Verify all timed events share the final playback/output sample clock | WP01 | |
| T004 | Implement one ProducerClockNormalization adapter at producer boundary; reject ambiguous clocks | WP01 | |
| T005 | Lock producer semantics with regression tests; no downstream compensation | WP01 | |
| T006 | Absolute sample→quarter/→tick conversion tests incl. negative/fractional quarters | WP02 | [P] |
| T007 | Segment lookup at exact boundaries; extrapolation behaviour | WP02 | [P] |
| T008 | TempoSegment continuity invariant (double precision; next origin threaded from previous) | WP02 | [P] |
| T009 | Ten-minute constant-tempo no-drift test | WP02 | [P] |
| T010 | Harden MusicalTimeMap.cs from discovered failures | WP02 | |
| T011 | Normalize BeatIndex→quarter via explicit scale in builder; §53 nonzero-start test | WP03 | [P] |
| T012 | Deduplicate identical anchors; reject/report conflicts (no averaging) | WP03 | [P] |
| T013 | Harden BeatGridFitter robust fit: outliers, residuals, no per-beat tempo segments | WP03 | [P] |
| T014 | Establish phase from anchors; use validated BPM as rate without destroying phase | WP03 | [P] |
| T015 | Strict-mode failure cases + rejected-anchor diagnostics fields | WP03 | |
| T016 | Integrate validated tempo transitions into segments | WP03 | [P] |
| T017 | Preserve fitted phase through tempo changes | WP03 | [P] |
| T018 | Guarantee continuity; suppress jitter-driven segment spam | WP03 | [P] |
| T019 | Crossing-boundary tests (note/pitch/anchor before/at/after transition) | WP03 | [P] |
| T020 | Harden TempoSegment µs/qn 24-bit validation | WP03 | |
| T021 | Compute one global nonnegative origin tick offset incl. pickups | WP04 | [P] |
| T022 | Preserve pickup notes (negative quarter positions); not snapped | WP04 | [P] |
| T023 | Set Tempo with µs/qn dedup; Time Signature only when meter known | WP04 | [P] |
| T024 | Map markers/loops through same map; conductor track timeline-wide only | WP04 | [P] |
| T025 | Conductor tests: SMF Format 1 track 0, EOT | WP04 | |
| T026 | Map note start/end independently; minimum one-tick duration | WP04 | [P] |
| T027 | Map pitch changes independently through map | WP04 | [P] |
| T028 | Explicit bend-range policy + RPN setup; clamp 14-bit, no wrap | WP04 | [P] |
| T029 | Explicit same-tick ordering + deterministic secondary keys; retrigger Off→On | WP04 | [P] |
| T030 | Route rhythm + DAC through shared map + origin; same-tick alignment test | WP04 | |
| T031 | Harden MidiFileWriter: Format 1, PPQ, MTrk lengths, nonnegative deltas | WP05 | [P] |
| T032 | VLQ boundary tests (0…0x0FFFFFFF) + range rejection | WP05 | [P] |
| T033 | EOT exactly once per track; tempo precedes note-on same tick | WP05 | [P] |
| T034 | Byte-identical deterministic output regression | WP05 | [P] |
| T035 | DAC identity→note independent of trigger→tick; DAC origin-shift fix + regression | WP05 | |
| T036 | Extend CLI: --ppq/--tempo-source/--bpm/--beat-offset-samples/--meter/--first-downbeat-sample/--strict-timing | WP06 | [P] |
| T037 | Wire source mapping; no silent 120 BPM; §42 symbolic-ambiguity strict handling; non-strict fallback reported | WP06 | [P] |
| T038 | Integrate --timing-report with contracts/timing-report.schema.json | WP06 | [P] |
| T039 | Align MidiExportService/MidiExportRequest to same map/options | WP06 | [P] |
| T040 | End-to-end integration: parser round trip + FR-005 acceptance matrix | WP06 | |

Total: 40 subtasks across 6 work packages (WP01=5, WP02=5, WP03=10, WP04=10, WP05=5, WP06=5).

---

## Work Package WP01 — Producer Audit & Sample-Clock Normalization

- **Goal**: Run the mandatory producer audit, document BeatIndex/ValidatedBpm/sample-clock semantics, and implement the user-approved single producer-boundary clock normalization adapter with rejection of ambiguous clocks.
- **Priority**: Highest — prerequisite for every downstream WP.
- **Independent test**: Existing `Ym2608TimelineDecoderTests` still pass; new regression tests lock documented semantics.
- **Included subtasks**: T001, T002, T003, T004, T005
- **Implementation sketch**: Enumerate producers → document event units → verify shared sample clock → add ProducerClockNormalization → regression tests.
- **Parallel opportunities**: T001/T002 can run before T003/T004 settles; keep inside WP01.
- **Dependencies**: None.
- **Risks**: Inferred BeatIndex unit or clock from record names instead of proven semantics; compensating in the writer (forbidden).
- **Estimated prompt size**: ~450 lines.

---

## Work Package WP02 — MusicalTimeMap & TempoSegment Invariants

- **Goal**: Lock the absolute map's sample→quarter→tick conversions, negative/fractional quarters, exact-boundary lookup, and double-precision segment continuity; add the ten-minute no-drift test; harden `MusicalTimeMap.cs`.
- **Priority**: High.
- **Independent test**: `MusicalTimeMapInvariantsTests` constant-tempo and continuity cases.
- **Included subtasks**: T006, T007, T008, T009, T010
- **Implementation sketch**: Add conversion/continuity/drift tests first, then fix only discovered failures inside `MusicalTimeMap`.
- **Parallel opportunities**: All conversion tests share one file but touch distinct methods.
- **Dependencies**: WP01.
- **Risks**: Introducing cumulative rounding; rounding segment origins independently.
- **Estimated prompt size**: ~380 lines.

---

## Work Package WP03 — Fitting, Source Precedence & Tempo Segments

- **Goal**: Normalize BeatIndex→quarter once, deduplicate/reject conflict anchors, harden robust fitting, establish source precedence + phase from anchors, make strict mode fail on fundamental ambiguity, integrate validated tempo transitions into continuous segments, suppress jitter-driven segment spam, and validate 24-bit µs/qn representation.
- **Priority**: High.
- **Independent test**: `BeatGridFitter`-based duplicate/conflict/jitter/outlier/tempo cases in `MusicalTimingTests` and `MusicalTimeMapBuilderTests`.
- **Included subtasks**: T011, T012, T013, T014, T015, T016, T017, T018, T019, T020
- **Implementation sketch**: Batch C (normalize → dedupe → conflict → robust fit → phase → diagnostics → strict) then Batch D (transitions → continuity → suppression → boundary tests → µs/qn).
- **Parallel opportunities**: Source precedence/fitting (T011-T015) and tempo-segment work (T016-T020) are separable concerns within one WP.
- **Dependencies**: WP01.
- **Risks**: Averaging conflicts; treating loop resets as corruption; phase destroyed when validated BPM is applied; per-beat tempo segments; unrepresentable tempo silently clamped.
- **Estimated prompt size**: ~580 lines (10 subtasks at the WP ceiling).

---

## Work Package WP04 — MIDI Origin, Events, Ordering & Pitch

- **Goal**: Establish one global nonnegative origin (incl. pickups), keep Track 0 conductor-only, emit Set Tempo with µs/qn dedup and Time Signature only when known, map note/pitch events independently, enforce the minimum one-tick duration, and encode deterministic same-tick ordering with explicit pitch/RPN policy.
- **Priority**: High.
- **Independent test**: `MusicalMidiExporterTests` phase-origin/conductor/event/order cases; `MidiPitchAccuracyTests`.
- **Included subtasks**: T021, T022, T023, T024, T025, T026, T027, T028, T029, T030
- **Implementation sketch**: Batch E (origin → pickups → conductor → markers/loops → conductor tests) then Batch F (note endpoints → pitch → bend policy → ordering → rhythm/DAC share).
- **Parallel opportunities**: Conductor/origin (T021-T025) and event/order/pitch (T026-T030) split across exporter surfaces; T030 DAC routing coordinates with WP05.
- **Dependencies**: WP03.
- **Risks**: Shifting tracks/segments independently; snapping pickups; fabricating 4/4; deriving endTick from startTick+duration; unstable equal-key sorting; bend wrap; DAC path bypassing origin.
- **Estimated prompt size**: ~620 lines (10 subtasks at the WP ceiling).

---

## Work Package WP05 — MIDI Writer & DAC Timing Separation

- **Goal**: Harden `MidiFileWriter` for Format 1, configured PPQ, valid chunks, nonnegative deltas, VLQ boundaries, one EOT per track, deterministic bytes; split DAC identity→note from trigger→tick and add the DAC origin-shift fix.
- **Priority**: High.
- **Independent test**: Writer VLQ/EOT/determinism cases; `DacMidiExporterTests` separation regression.
- **Included subtasks**: T031, T032, T033, T034, T035
- **Implementation sketch**: Per Batch G then the DAC timing-separation fix (T035) confirming no sampleRate/bpm in the DAC path.
- **Parallel opportunities**: VLQ/EOT (T032/T033) and DAC separation (T035) are independent files.
- **Dependencies**: WP04.
- **Risks**: Writer knowing source samples/BPM; rejecting negative VLV silently; DAC path bypassing origin shift.
- **Estimated prompt size**: ~430 lines.

---

## Work Package WP06 — CLI, Timing Report & Integration Matrix

- **Goal**: Wire the existing MIDI command/options and GUI service to source modes/BPM/phase/meter/downbeat/strict/report, remove silent 120 BPM fallback, integrate `--timing-report`, and cover the FR-005 acceptance matrix with a parser round-trip integration test.
- **Priority**: Highest (feature usability gate).
- **Independent test**: CLI option semantics; `MidiExportServiceTests`; parser round trip.
- **Included subtasks**: T036, T037, T038, T039, T040
- **Implementation sketch**: Extend options → wire timing source + strict/no-fallback → report JSON → GUI parity → integration matrix.
- **Parallel opportunities**: CLI options (T036/T037) and report (T038) are separable; GUI parity (T039) distinct.
- **Dependencies**: WP03, WP04, WP05.
- **Risks**: New CLI framework; BPM-only output called beat aligned; report PPQ placeholder.
- **Estimated prompt size**: ~500 lines.

---

## Dependencies (frontmatter)

- WP01: none
- WP02: WP01
- WP03: WP01
- WP04: WP03
- WP05: WP04
- WP06: WP03, WP04, WP05