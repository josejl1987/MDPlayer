# MDPlayer MIDI Export Timing Hardening

**Status:** Proposed
**Type:** Brownfield hardening
**Source of truth:** This specification defines required behavior, constraints, and acceptance criteria. Implementation planning and task decomposition are downstream concerns.

---

## 1. Mission

Fix and complete the existing MIDI export timing implementation so exported Standard MIDI Files:

* preserve source timeline timing accurately;
* derive musical position from existing driver beat/timing information whenever possible;
* have correct BPM;
* have correct beat phase;
* preserve pickup notes;
* handle tempo changes without drift;
* map melodic notes, pitch bends, rhythm events, and YM2612 DAC events through the same musical-time model;
* import into a DAW with beats aligned to the intended musical grid;
* remain deterministic.

This is not a greenfield implementation.

The existing timing architecture must be extended and corrected rather than replaced.

---

## 2. Existing-System Context

The current implementation already contains the timing architecture that this work must use, including:

* `MusicalTimeMap`
* `MusicalTimeMapBuilder`
* `MusicalTimeMapOptions`
* `BeatGridFitter`
* `TimingDiagnostics`
* `TempoSegment`
* `TimingSource`
* `MusicalMidiExporter`
* `MidiFileWriter`
* `MidiEvent`

The visualization timeline exposes driver timing evidence including:

* `DriverTimingEvent.SamplePosition`
* `DriverTimingEvent.TimerBValue`
* `DriverTimingEvent.ValidatedBpm`
* `BeatEvent.SamplePosition`
* `BeatEvent.BeatIndex`

Notes and pitch changes are also timestamped using source sample positions.

`MusicalMidiExporter` already consumes a `MusicalTimeMap`.

The implementation must preserve and harden this architecture.

---

## 3. Scope

### 3.1 In Scope

This work covers:

* timing-source semantics;
* producer timestamp semantics;
* beat-index semantics;
* beat phase;
* tempo recovery;
* tempo transitions;
* musical-time continuity;
* MIDI origin translation;
* conductor-track timing;
* melodic notes;
* note endings and retriggers;
* pitch changes and pitch bend;
* rhythm events;
* YM2612 DAC events;
* loop and marker timing;
* MIDI event ordering;
* MIDI serialization correctness;
* CLI timing configuration;
* strict and fallback timing behavior;
* timing diagnostics;
* automated regression and acceptance testing.

### 3.2 Out of Scope

The following are explicitly outside this work:

* audio BPM detection;
* FFT/onset-based timing recovery;
* default quantization;
* replacing the existing timing subsystem;
* replacing `BeatGridFitter` with a different framework;
* unrelated percussion allocation changes;
* visualization rendering changes;
* video rendering changes;
* UI refactors;
* chip-emulation refactors;
* unrelated playback changes;
* broad style cleanup.

---

# 4. Core Domain Invariants

## INV-001 — Tempo, Beat Phase, and Meter Are Separate Concepts

Tempo defines the rate of musical time.

Beat phase defines where the beat grid lies relative to playback sample zero.

Meter/downbeat defines bar grouping and bar phase.

These concepts MUST remain independent.

The implementation MUST NOT assume:

```text
BPM => beat 0 => bar 1
```

Correct BPM with incorrect phase is still incorrectly aligned MIDI.

Knowing beat locations does not imply knowledge of a downbeat.

---

## INV-002 — MusicalTimeMap Is the Sole Timing Authority

Every source event must follow:

```text
source sample position
        ↓
MusicalTimeMap
        ↓
absolute quarter-note position
        ↓
absolute MIDI tick
```

There must be no independent sample-to-MIDI-tick timing implementation elsewhere.

---

## INV-003 — Source Timing Is Absolute

Every event must derive its musical position independently from its original absolute source sample position.

MIDI delta times may only be calculated during final serialization.

Repeatedly accumulating rounded MIDI tick deltas is prohibited.

---

## INV-004 — Exactly One MIDI Origin Translation Exists

Negative musical positions are valid.

A single global MIDI tick offset may translate the entire timeline so exported MIDI ticks are nonnegative.

That same translation MUST apply to every track and every event type.

---

# 5. Functional Requirements

## Producer and Source Semantics

**FR-001 — Common sample clock**

All timing-bearing source events MUST ultimately use the same final playback/output sample clock, including:

* driver timing events;
* beat events;
* note starts;
* note ends;
* pitch changes;
* rhythm events;
* DAC events.

If a producer uses another clock, normalization MUST occur at one clearly defined producer/boundary layer rather than being compensated for inside the MIDI writer.

---

**FR-002 — Beat producer semantics**

Every producer of beat events MUST be audited before beat fitting behavior is changed.

The implementation MUST establish and document:

* what one `BeatIndex` increment represents;
* whether the unit is a quarter note, driver beat, timer subdivision, or another unit;
* whether the value can be fractional;
* whether it resets;
* whether it can begin at a nonzero value;
* whether values can jump;
* event ordering relative to timing changes at the same sample.

These semantics MUST be protected by regression tests.

---

**FR-003 — Beat-unit normalization**

The implementation MUST NOT assume:

```text
driver beat == MIDI quarter note
```

If driver beat units differ from MIDI quarter-note units, conversion MUST occur once while constructing `MusicalTimeMap`.

The scale MUST NOT be hidden inside `MusicalMidiExporter`.

---

**FR-004 — ValidatedBpm semantics**

The implementation MUST establish whether `DriverTimingEvent.ValidatedBpm` represents effective playback BPM after all relevant driver timing behavior.

It MUST also establish exactly when that value becomes active relative to `DriverTimingEvent.SamplePosition`.

The resulting convention MUST be unambiguous and covered by regression tests.

`TimerBValue` MUST NOT be independently reverse-engineered into MIDI timing where validated driver timing is already available.

---

## Musical-Time Mapping

**FR-005 — Shared timing map**

The following MUST all use the same `MusicalTimeMap`:

* note starts;
* note ends;
* retriggers;
* pitch changes;
* rhythm events;
* DAC triggers;
* loop markers;
* section markers;
* tempo transitions.

---

**FR-006 — Absolute conversion**

Source event timing MUST NOT be calculated by accumulating rounded MIDI deltas.

Each event MUST map directly from its original absolute source sample position.

---

**FR-007 — Continuous tempo segments**

Adjacent `TempoSegment`s MUST be continuous in musical time.

At a transition sample `S`:

```text
newSegment.QuarterPositionAtStart
==
previousSegment.SampleToQuarterPosition(S)
```

within normal floating-point tolerance.

Continuity MUST be established in musical time before MIDI tick rounding occurs.

---

## Timing-Source Selection

**FR-008 — Deterministic timing-source precedence**

Automatic source selection MUST be deterministic.

Unless producer semantics establish a stronger source, priority MUST be:

1. authoritative driver beat anchors;
2. explicit sequencer/driver clock or bar information;
3. validated driver tempo information;
4. explicit user override;
5. existing symbolic timeline inference.

Authoritative phase established from beat anchors MUST NOT be discarded when validated BPM information is incorporated.

---

**FR-009 — Auto mode**

`auto` MUST select the strongest trustworthy timing evidence available.

Verified driver timing MUST take precedence over heuristics.

A fallback such as:

```text
120 BPM
sample 0 = beat 0
```

MUST NOT be described as authoritative or beat aligned.

---

**FR-010 — Driver mode**

`driver` MUST require usable authoritative driver timing.

If a trustworthy timing map cannot be constructed, export MUST fail.

It MUST NOT silently switch to symbolic inference.

---

**FR-011 — Symbolic mode**

`symbolic` MUST explicitly use the existing symbolic timing-inference path.

Symbolic inference MUST remain fallback behavior and MUST NOT override valid driver timing in automatic mode.

---

**FR-012 — Fixed mode**

`fixed` MUST require an explicit BPM.

If actual beat alignment is required, an explicit beat-phase convention MUST also be provided.

Fixed BPM without phase may produce correctly spaced MIDI but MUST NOT be reported as necessarily beat aligned.

---

## Beat Fitting

**FR-013 — Anchor validation**

Before fitting, beat anchors MUST be:

* sorted by source sample;
* checked for duplicate sample positions;
* checked for conflicting musical positions;
* checked for impossible backwards motion;
* interpreted according to documented loop/reset semantics.

Identical duplicate anchors may be deduplicated harmlessly.

Conflicting anchors MUST NOT be silently averaged.

---

**FR-014 — Loop-reset normalization**

A backwards `BeatIndex` MUST NOT automatically be treated as corruption.

If producer semantics establish that the driver intentionally resets `BeatIndex` at loop boundaries, repeated local indexes MUST be normalized once into a continuous musical timeline before fitting.

---

**FR-015 — Robust fitting**

Small timestamp jitter MUST NOT create unstable BPM values or unnecessary tempo segments.

The existing robust-fit approach MUST:

1. estimate a robust timing rate;
2. identify likely inliers;
3. fit rate and phase/intercept;
4. calculate residuals;
5. reject sufficiently severe isolated outliers;
6. report rejected anchors.

The implementation MUST NOT overfit minor timing jitter.

---

## Diagnostics and Strictness

**FR-016 — Timing diagnostics**

`TimingDiagnostics` MUST expose at least:

* selected timing source;
* sample rate;
* raw beat-anchor count;
* accepted anchor count;
* rejected anchor count;
* estimated BPM;
* tempo-segment count;
* RMS anchor residual;
* maximum anchor residual;
* beat phase/intercept;
* whether phase is authoritative;
* whether tempo is authoritative;
* meter status;
* downbeat status;
* warnings.

For rejected anchors it MUST expose enough information to identify:

* sample position;
* beat position;
* residual;
* rejection reason.

Diagnostics MUST remain compact by default.

---

**FR-017 — Strict timing mode**

Strict timing mode MUST fail export when a fundamental timing ambiguity remains.

This includes, where applicable:

* incompatible beat anchors;
* unknown beat-unit conversion;
* unresolved required beat phase;
* pathological fit residual;
* invalid tempo;
* impossible tempo-segment ordering;
* conflicting duplicate anchors.

Strict mode MUST NOT silently fall back to a fixed 120 BPM timeline.

---

## Tempo Changes

**FR-018 — Piecewise tempo**

Real tempo changes MUST be represented as piecewise `TempoSegment`s.

Minor beat/timer observation jitter MUST NOT create a new MIDI tempo event for every observation.

Explicit validated driver transitions SHOULD be preferred where available.

Beat anchors SHOULD verify phase and continuity.

---

**FR-019 — Tempo-boundary continuity**

For an accepted tempo change at source sample `S`, the new segment MUST begin at `S`.

The musical position at `S` MUST be identical on both sides of the segment boundary.

---

**FR-020 — MIDI Set Tempo representation**

MIDI Set Tempo MUST use microseconds per quarter note:

```text
µsPerQuarter = round(60_000_000 / BPM)
```

The emitted value MUST fit the MIDI Set Tempo 24-bit representation.

Adjacent segments that produce the same integer MIDI tempo value MUST NOT emit unnecessary duplicate Set Tempo events.

Deduplication MUST compare the actual emitted MIDI tempo value, not raw floating-point BPM.

---

## Phase, Origin, Meter, and Pickup

**FR-021 — Nonzero phase**

`MusicalTimeMap` MUST support fractional and negative quarter-note positions at source sample zero.

The implementation MUST NOT assume:

```text
source sample 0 == quarter position 0
```

---

**FR-022 — Global tick origin**

After all relevant source events have been mapped, the exporter MUST calculate one global nonnegative tick offset.

The same offset MUST apply to:

* conductor events;
* notes;
* pitch bends;
* rhythm;
* DAC;
* markers;
* loops.

Per-track or per-segment origin shifting is prohibited.

---

**FR-023 — Pickup preservation**

Pickup notes MUST remain pickups relative to the intended musical grid after global origin translation.

They MUST NOT be snapped to the first beat or downbeat.

---

**FR-024 — Meter and downbeat**

Meter MUST only be emitted when supported by:

1. explicit driver/format bar information;
2. explicit user override;
3. an already-existing high-confidence meter source.

If meter is unknown, the MIDI Time Signature event MUST be omitted.

The implementation MUST NOT fabricate 4/4.

The first observed beat MUST NOT automatically be treated as a downbeat.

---

## MIDI Structure

**FR-025 — Standard MIDI File structure**

Exported files MUST use:

```text
SMF Format 1
```

Default PPQ MUST be:

```text
960
```

PPQ MUST remain configurable.

---

**FR-026 — Conductor track**

Track 0 MUST contain timeline-wide information only.

At minimum:

* Track Name;
* Set Tempo;
* Time Signature only when known;
* loop/section markers where applicable;
* End of Track.

Per-channel musical controller/note data MUST NOT be placed on Track 0.

---

**FR-027 — Musical tracks**

Musical tracks MUST share the same:

* PPQ;
* `MusicalTimeMap`;
* global origin offset.

Existing logical voice grouping SHOULD be preserved unless existing behavior intentionally differs.

---

## Quantization and Notes

**FR-028 — Unquantized export**

Default export MUST remain unquantized.

Timing reconstruction MUST preserve actual source scheduling relative to the recovered beat grid.

No default snapping of note, rhythm, or DAC events to musical subdivisions is allowed.

---

**FR-029 — Independent note endpoints**

For every source note:

```text
startTick = map(StartSample) + origin
endTick   = map(EndSample)   + origin
```

Start and end MUST be mapped independently.

`endTick` MUST NOT be derived from `startTick` plus a fixed-tempo converted duration.

---

**FR-030 — Minimum positive note duration**

If:

```text
EndSample > StartSample
```

but both endpoints round to the same MIDI tick, the exported note MUST have a minimum duration of one tick:

```text
endTick = max(endTick, startTick + 1)
```

This correction MUST be local to the affected note.

---

**FR-031 — Retrigger ordering**

For same-pitch retriggers on the same MIDI channel and tick:

```text
Note Off
Note On
```

MUST be emitted in that order.

The result MUST NOT depend on incidental collection insertion order.

---

## Pitch

**FR-032 — Absolute pitch-change timing**

Every `PitchChange.SamplePosition` MUST map independently through `MusicalTimeMap`.

Pitch event ticks MUST NOT be interpolated from a note's starting tick.

---

**FR-033 — Pitch-bend configuration**

If a non-default pitch-bend range is used, it MUST be explicitly configured using standard MIDI RPN Pitch Bend Sensitivity semantics.

The configuration MUST NOT be redundantly resent before every note when channel state is unchanged.

---

**FR-034 — Pitch-bend range policy**

The exporter MUST have one explicit documented bend-range policy.

For each source pitch it MUST:

1. choose a MIDI base note;
2. calculate bend offset;
3. clamp to the legal MIDI bend range;
4. never wrap arithmetic.

Where possible, the nearest representable base note SHOULD be used when the pitch exceeds the selected range.

---

## Same-Tick Ordering

**FR-035 — Explicit event priority**

Events sharing a tick MUST use explicit deterministic semantic priority:

1. Note Off;
2. conductor state changes effective at the tick:

   * Set Tempo;
   * Time Signature;
3. bank/program/controller setup;
4. pitch bend;
5. Note On;
6. non-state textual/marker metadata.

Events with equal tick and equal priority MUST use a stable deterministic secondary ordering.

---

**FR-036 — Tempo-boundary event ordering**

A tempo event beginning at tick `T` MUST become active at tick `T`.

At equal ticks, Set Tempo MUST precede musical Note On events.

---

## Rhythm and DAC

**FR-037 — Rhythm timing**

Rhythm events MUST use the shared `MusicalTimeMap` and global origin shift.

There MUST be no rhythm-specific samples-per-tick conversion.

---

**FR-038 — DAC timing**

DAC sample identity and DAC trigger timing MUST remain separate concerns.

DAC trigger timing MUST be:

```text
DAC trigger source sample
        ↓
MusicalTimeMap
        ↓
MIDI tick
```

A DAC-specific exporter/helper MUST NOT independently establish BPM or sample-to-tick conversion.

---

**FR-039 — DAC identity independence**

Changing valid DAC PCM deduplication or sample-to-note assignment MUST NOT change event timing.

Changing timing recovery MUST NOT alter DAC sample identity.

---

## Loops and Markers

**FR-040 — Loop BeatIndex behavior**

Actual driver `BeatIndex` behavior across playback loops MUST be explicitly established and covered by tests.

If reset behavior exists by design, it MUST be normalized intentionally before fitting.

---

**FR-041 — Loop and section markers**

Loop and section marker samples MUST be mapped through the same `MusicalTimeMap`.

Markers MUST retain their recovered musical position whether they occur on or off the beat.

---

## Symbolic Fallback

**FR-042 — Symbolic ambiguity**

When symbolic inference produces materially plausible half/double-tempo alternatives, ambiguity SHOULD be reported rather than hidden behind arbitrary confidence.

Strict mode MUST reject materially ambiguous symbolic timing unless resolved by explicit user input.

No substantially new symbolic-inference framework is required.

---

## MIDI Serialization

**FR-043 — Writer responsibility**

`MidiFileWriter` MUST only encode an already-resolved MIDI event stream.

It MUST NOT know about:

* source sample timing;
* beat fitting;
* BPM inference.

---

**FR-044 — MIDI writer invariants**

Generated files MUST have:

* valid `MThd`;
* SMF Format 1;
* configured PPQ division;
* valid `MTrk` chunks;
* correct big-endian lengths;
* valid VLQ delta times;
* events sorted by absolute tick and explicit priority;
* nonnegative delta times;
* exactly one effective End of Track per track.

---

**FR-045 — VLQ handling**

MIDI Variable Length Quantity encoding MUST correctly support representable boundary values.

Values outside the MIDI VLQ representable range MUST be rejected rather than truncated.

---

## CLI

**FR-046 — Existing MIDI command**

The existing MIDI export command/framework MUST be extended.

A second parallel MIDI command framework MUST NOT be introduced.

---

**FR-047 — CLI timing options**

The existing CLI MUST expose equivalents of:

```text
--ppq
--tempo-source auto|driver|symbolic|fixed
--bpm
--beat-offset-samples
--meter
--first-downbeat-sample
--timing-report
--strict-timing
```

Exact option naming SHOULD follow existing CLI conventions.

---

**FR-048 — PPQ option**

PPQ MUST default to 960 and MUST require a positive MIDI-valid division value.

---

**FR-049 — BPM option**

Explicit BPM MUST be finite and positive.

The following MUST be rejected:

* zero;
* negative values;
* NaN;
* Infinity.

---

**FR-050 — Beat-phase option**

The explicit beat-phase option MUST have one documented sign/convention and use it consistently.

Multiple synonymous internal phase conventions MUST NOT be introduced.

---

**FR-051 — Meter/downbeat options**

Meter MUST remain optional.

No 4/4 default is allowed.

Explicit first-downbeat information MUST require compatible meter information.

---

## Fallback and Reporting

**FR-052 — Non-strict fallback transparency**

If compatibility requires a non-strict fixed-tempo fallback, diagnostics MUST explicitly state:

* tempo source;
* that authoritative beat phase was unavailable;
* that the resulting MIDI is not guaranteed to align to the intended beat grid.

Such output MUST NOT be described as beat aligned.

---

**FR-053 — Timing report**

`--timing-report` MUST serialize compact, stable timing diagnostics.

The report SHOULD include:

* sample rate;
* PPQ;
* selected source;
* authoritative tempo/phase flags;
* global origin offset;
* meter/downbeat status;
* anchor counts and residuals;
* tempo segments;
* warnings.

Property naming SHOULD follow existing project conventions where practical.

---

**FR-054 — Actionable errors**

Timing failures MUST report the actual unresolved condition rather than a generic export failure.

Diagnostics and errors SHOULD distinguish independently between:

* tempo known/unknown;
* phase known/unknown;
* meter known/unknown.

---

# 6. Non-Functional Requirements

**NFR-001 — Deterministic output**

The same timeline, options, and PPQ MUST produce byte-identical `.mid` output across repeated exports.

No ordering may depend on:

* dictionary/hash enumeration;
* hash codes;
* object identity;
* thread scheduling;
* unstable equal-key sorting.

---

**NFR-002 — No cumulative timing drift**

A constant-tempo timeline of at least ten minutes MUST show no error that grows proportionally with file duration.

Final timing accuracy MUST remain comparable to initial timing accuracy apart from normal MIDI integer-tick quantization.

---

**NFR-003 — Natural timing tolerance**

Musical timing correctness MUST be evaluated primarily in MIDI ticks/quarter-note position rather than arbitrary millisecond tolerances.

---

**NFR-004 — Compact diagnostics**

Timing diagnostics and reports MUST provide enough evidence to debug alignment without dumping large internal data structures by default.

---

**NFR-005 — Maintainability**

The timing architecture MUST remain conceptually centered around one source-sample-to-musical-time authority.

New parallel timing paths are prohibited.

---

**NFR-006 — Reasonable complexity**

Normal deterministic sorting with conventional complexity such as `O(n log n)` is acceptable.

Premature optimization is not required.

---

# 7. Constraints

**C-001** — Reuse the existing `MusicalTimeMap`.

**C-002** — Do not introduce another timing abstraction or parallel MIDI timing implementation.

**C-003** — Do not rewrite `BeatGridFitter` wholesale unless a failing invariant proves the current design fundamentally incapable of satisfying the requirement.

**C-004** — Do not implement audio BPM or onset detection.

**C-005** — Do not assume 4/4.

**C-006** — Do not assume the first observed beat is a downbeat.

**C-007** — Do not assume sample zero is beat zero.

**C-008** — Do not assume BPM establishes beat phase.

**C-009** — Do not assume a driver beat is a MIDI quarter note.

**C-010** — Do not calculate source MIDI timing incrementally.

**C-011** — Do not calculate DAC timing independently.

**C-012** — Do not calculate rhythm timing independently.

**C-013** — Do not tie timing to FM instrument mapping.

**C-014** — Do not tie DAC event timing to DAC sample deduplication.

**C-015** — Do not quantize by default.

**C-016** — Do not add new external dependencies unless existing project capabilities cannot reasonably support the required validation.

**C-017** — Do not perform unrelated rendering, UI, emulator, playback, or style refactors.

**C-018** — Do not silently use fixed 120 BPM in strict mode.

**C-019** — Do not describe BPM-only output with unknown phase as beat aligned.

**C-020** — Do not generate tempo-event spam from harmless source timing jitter.

---

# 8. Required Discoveries / Unresolved Source Semantics

The following are intentionally not guessed by this specification.

They MUST be established from the existing implementation and protected by tests:

**RD-001** — Exact unit represented by `BeatEvent.BeatIndex`.

**RD-002** — Whether `BeatIndex` resets, jumps, or continues monotonically across loops.

**RD-003** — Whether all relevant event sample positions use the same final output sample clock.

**RD-004** — Exact activation semantics of `DriverTimingEvent.ValidatedBpm` relative to `SamplePosition`.

**RD-005** — Whether explicit driver/format meter or bar/downbeat information already exists.

No implementation may resolve these questions merely by assumption.

---

# 9. Acceptance Scenarios

## AC-001 — Constant tempo

**Covers:** FR-005, FR-006, FR-020, NFR-002

Given:

```text
sample rate = 48000 Hz
tempo       = 120 BPM
PPQ         = 960
```

one quarter note equals 24,000 samples.

Require, subject only to an explicitly configured global origin translation:

```text
sample 0     -> tick 0
sample 24000 -> tick 960
sample 48000 -> tick 1920
sample 72000 -> tick 2880
```

---

## AC-002 — Nonzero beat phase

**Covers:** FR-021, FR-023

Given authoritative anchors:

```text
sample 12000 -> quarter 0
sample 36000 -> quarter 1
sample 60000 -> quarter 2
```

at 48 kHz and 120 BPM:

```text
sample 0 -> quarter -0.5
```

The exported timeline MUST preserve this phase.

---

## AC-003 — Nonzero initial BeatIndex

**Covers:** FR-002, FR-003, FR-021

Given:

```text
sample 0     -> beat 128
sample 24000 -> beat 129
sample 48000 -> beat 130
```

the relative musical timeline MUST be preserved.

The first observed BeatIndex MUST NOT be treated as zero merely because it is first.

---

## AC-004 — Pickup preservation

**Covers:** FR-022, FR-023

Given a note at quarter `-0.5` and a downbeat at quarter `0`:

* every exported MIDI tick MUST be nonnegative;
* the note MUST remain exactly half a quarter before the downbeat;
* the global origin translation MUST be applied once.

---

## AC-005 — Identical duplicate beat anchor

**Covers:** FR-013

Given two identical anchors at the same sample and musical position:

* they MAY be deduplicated;
* timing MUST remain unchanged;
* no error MUST be produced solely because of the harmless duplicate.

---

## AC-006 — Conflicting beat anchor

**Covers:** FR-013, FR-017

Given:

```text
sample 24000 -> beat 1
sample 24000 -> beat 2
```

the conflict MUST be diagnosed.

Strict timing mode MUST fail.

The positions MUST NOT be averaged.

---

## AC-007 — Harmless beat jitter

**Covers:** FR-015, FR-018

Given a long constant-tempo anchor sequence with approximately ±2 samples of timing noise:

* one stable effective tempo segment SHOULD result;
* recovered BPM MUST remain near the source truth;
* a Set Tempo event MUST NOT be emitted per beat;
* residual diagnostics MUST remain low.

---

## AC-008 — Isolated timing outlier

**Covers:** FR-015, FR-016

Given one severe incorrect beat timestamp within otherwise valid timing:

* the robust fitter MUST reject it when appropriate;
* recovered tempo MUST remain close to truth;
* the rejected anchor MUST appear in diagnostics;
* strict-mode behavior MUST follow the configured residual policy.

---

## AC-009 — Real tempo transition

**Covers:** FR-018, FR-019, FR-020

Given:

```text
120 BPM for 16 quarters
150 BPM afterward
```

require:

* exactly the necessary effective tempo transition;
* continuous quarter-note position at the boundary;
* Set Tempo at the correct tick;
* no jitter-driven extra tempo segments.

---

## AC-010 — Note crosses tempo boundary

**Covers:** FR-029

Given a note from quarter `15.5` to quarter `16.5` with a tempo transition at quarter `16`:

* note start and end MUST map independently from source samples;
* duration MUST remain musically correct across the transition.

---

## AC-011 — Pitch change at tempo boundary

**Covers:** FR-032, FR-035, FR-036

A pitch change whose source sample equals a tempo transition boundary MUST:

* map to the correct tick;
* preserve deterministic same-tick ordering;
* produce no one-tick discontinuity.

---

## AC-012 — Positive source duration rounds to one tick

**Covers:** FR-030

Given:

```text
EndSample > StartSample
```

but both source samples round to the same MIDI tick:

```text
endTick == startTick + 1
```

MUST result.

---

## AC-013 — Retrigger ordering

**Covers:** FR-031, FR-035

For the same channel and MIDI pitch:

```text
old note ends tick 960
new note begins tick 960
```

the encoded order MUST be:

```text
Note Off
Note On
```

---

## AC-014 — Unknown meter

**Covers:** FR-024

Given high-confidence beat and tempo information but no bar/meter information:

* beat alignment MUST remain correct;
* no fabricated 4/4 Time Signature event may be emitted.

---

## AC-015 — Known meter and downbeat

**Covers:** FR-024

Given authoritative:

```text
4/4
first downbeat sample S
```

the MIDI MUST contain:

* a valid Time Signature event;
* correct bar phase after global origin translation.

Note timing MUST NOT be altered merely to force a convenient bar-one location.

---

## AC-016 — Loop behavior

**Covers:** FR-014, FR-040, FR-041

Tests MUST cover:

* loop marker exactly on a beat;
* loop marker off beat;
* actual driver `BeatIndex` behavior at loops.

A legitimate loop reset MUST NOT be interpreted as a huge negative tempo.

---

## AC-017 — Shared melodic/rhythm/DAC timing

**Covers:** FR-005, FR-037, FR-038

Given at source sample `S`:

```text
melodic note
DAC trigger
rhythm trigger
```

all three MUST map to the same musical tick before semantic event-priority differences.

---

## AC-018 — DAC identity does not affect timing

**Covers:** FR-039

Export the same timed DAC sequence using two different valid sample-to-note mappings.

MIDI note values MAY differ.

Event ticks and the tempo track MUST remain identical.

---

## AC-019 — Ten-minute drift

**Covers:** FR-006, NFR-002

For a synthetic constant-tempo timeline of at least ten minutes:

```text
expectedTick =
    round(
        anchorQuarter * PPQ
        + originTickOffset
    )
```

For every accepted anchor, including the final one:

```text
abs(actualTick - expectedTick) <= 1
```

There MUST be no duration-proportional drift.

---

## AC-020 — Independent MIDI parser validation

**Covers:** FR-025, FR-026, FR-044

Where an appropriate MIDI parser already exists in the project, generated output SHOULD be parsed independently.

Verify:

```text
format == 1
division == configured PPQ
track count correct
tempo values correct
event ticks correct
EOT present
```

A major new dependency MUST NOT be introduced solely for this test unless unavoidable.

---

## AC-021 — Anchor accuracy

**Covers:** FR-003, FR-005, FR-021

For every authoritative accepted beat anchor:

```text
abs(
    exportedTick(anchor.SamplePosition)
    -
    round(
        normalizedQuarterPosition(anchor)
        * PPQ
        + originTickOffset
    )
) <= 1
```

One MIDI tick is the normal maximum discretization tolerance.

A larger error MUST have an explicit diagnostic explanation.

---

## AC-022 — Temporal reconstruction accuracy

**Covers:** FR-006

Except for the deliberate minimum-one-tick note-duration correction, reconstructed source timing error SHOULD be no greater than approximately half one MIDI tick after rounding.

---

## AC-023 — VLQ boundaries

**Covers:** FR-045

At minimum validate:

```text
0
0x7F
0x80
0x3FFF
0x4000
0x1FFFFF
0x200000
0x0FFFFFFF
```

against expected MIDI VLQ behavior.

Values outside the valid representable range MUST be rejected.

---

## AC-024 — Byte-identical deterministic output

**Covers:** NFR-001

Exporting the exact same:

* timeline;
* options;
* PPQ;

multiple times MUST produce byte-identical MIDI files.

---

# 10. Success Criteria

The feature is accepted only when all applicable requirements, constraints, and acceptance scenarios are satisfied.

At minimum:

### Architecture

* every MIDI event uses the existing `MusicalTimeMap`;
* no independent DAC/rhythm sample-to-tick conversion remains;
* BPM inference is outside `MusicalMidiExporter`;
* MIDI serialization contains no source timing inference.

### Timing

* constant tempo maps correctly;
* beat phase maps correctly;
* pickups survive;
* tempo changes remain continuous;
* long files accumulate no timing drift;
* authoritative beat anchors remain within one MIDI tick of their expected musical positions.

### Musical correctness

* unknown meter is not fabricated;
* known meter/downbeat is preserved;
* notes crossing tempo changes remain correct;
* pitch changes crossing tempo changes remain correct;
* retriggers have deterministic Note-Off-before-Note-On ordering.

### MIDI correctness

* output is SMF Format 1;
* configured PPQ is honored;
* Set Tempo values are correct;
* VLQs are valid;
* no negative delta times exist;
* every track contains an effective End of Track;
* repeated exports are byte-identical.

### Driver correctness

* `BeatIndex` semantics are documented and regression-tested;
* loop behavior is intentionally handled;
* `ValidatedBpm` activation semantics are documented and regression-tested.

### DAC and rhythm

* DAC and rhythm triggers use the same timing grid as melodic voices;
* changing DAC sample-to-note assignment does not alter timing.

### User-facing behavior

* `auto` selects the strongest available evidence;
* `driver` fails without usable authoritative driver timing;
* `fixed` requires explicit BPM;
* strict mode rejects unresolved required alignment;
* fallback timing is visibly reported;
* diagnostics distinguish timing source, tempo, phase, meter, fit quality, and warnings.

---

# 11. Non-Goals

Successful completion does NOT require:

* a new MIDI timing subsystem;
* a new beat-fitting architecture;
* audio BPM detection;
* default MIDI quantization;
* broad cleanup of unrelated code;
* unrelated UI/rendering/playback changes;
* speculative extensibility work.

The intended end state remains conceptually:

```text
VisualizationTimeline
        │
        ├── Beats[]
        └── Timing[]
             │
             ▼
    MusicalTimeMapBuilder
             │
             ▼
      MusicalTimeMap
   sample ↔ quarter time
             │
       ┌─────┼─────┐
       │     │     │
     notes rhythm  DAC
       │     │     │
       └─────┼─────┘
             │
             ▼
    MusicalMidiExporter
             │
             ▼
   absolute MIDI events
             │
             ▼
      MidiFileWriter
             │
             ▼
            .mid
```

There is exactly one source-sample → musical-time authority:

```text
MusicalTimeMap
```

There is exactly one global MIDI origin translation.

Tempo, beat phase, and meter/downbeat remain separate concepts.

---

## Pitch Normalization

**FR-054 — Pitch-normalization mode flag**

The CLI MUST expose `--pitch-normalization fidelity|daw|off`:

* `fidelity` (default) — subtracts the accepted per-domain tuning bias and
  restores it via RPN channel tuning (RPN 0x0002 fine, plus RPN 0x0001 coarse
  beyond ±100c) at tick 0; bends carry only expressive deviation; played pitch
  equals source pitch on RPN-supporting targets.
* `daw` — opt-in; snaps accepted biases up to the snap cap to equal
  temperament and emits NO tuning events (deliberately changes absolute pitch
  presentation).
* `off` — byte-identical legacy output.

The acceptance of a tuning center is deliberately conservative (coverage,
distinct notes, MAD, persistence, per-chip caps) so synthetic fixtures and
in-tune domains are structural no-ops.

**FR-055 — Pitch report flag**

The CLI MUST expose `--pitch-report PATH`, mirroring `--timing-report`, writing
per-domain JSON with at least: attacks, retrigger attacks, raw pitch samples,
residual mode (cents), stable residual MAD (cents), baseline confidence,
raw bend transitions, after-dedup, after-deadband and expressive transitions,
plus the accepted tuning and warnings. Thresholds are configurable and
calibrated from real corpus reports — never hardcoded final values.
