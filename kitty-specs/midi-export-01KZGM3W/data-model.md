# Phase 1 Data Model — MIDI Export Timing Hardening

This model describes the existing C# records/classes and the narrow extensions required by FR-001..FR-005. It is a design contract, not a new parallel timing abstraction.

## 1. Timeline evidence and producer boundary

### `VisualizationTimeline`

Existing location: `MDPlayer/src/MDPlayer.Fmp.Core/Visualization/VisualizationTimeline.cs`.

| Field | Meaning | Required invariant |
|---|---|---|
| `SampleRate` | Final timeline/playback sample rate used by the timeline | Positive and explicitly identified as the destination clock for all sample-position fields in the timeline. |
| `StartSample`, `EndSample` | Inclusive/exclusive source range used by capture/map construction | Valid range; event samples used for export are interpreted on this same clock. |
| `Notes`, `Rhythm`, `Timing`, `Beats`, `LoopMarkers` | Timestamped source evidence/events | Each sample position is on the final timeline clock, or has passed the one producer-boundary normalization adapter. |
| `Ppz8`, `AdpcmB`, `SamplePlayback`, `AggregateHits`, etc. | Other timeline-domain events | Their timing must not bypass the shared map if they are exported as timed MIDI events. |

### `DriverTimingEvent`

Existing record fields: `SamplePosition`, `TimerBValue`, `ValidatedBpm`. `ValidatedBpm` is usable only when finite and positive. The implementation must document whether the event sample means “new tempo starts here,” “value was observed here,” or another explicit convention. It must not silently reverse-engineer `TimerBValue` in MIDI code.

### `BeatEvent`

Existing record fields: `SamplePosition`, `BeatIndex`. The producer audit must settle the unit and normalization. The map builder may apply one explicit `QuartersPerBeat` scale (or an equivalent producer-boundary adapter when the source unit is not quarter notes), but `MusicalMidiExporter` must receive quarter positions through `MusicalTimeMap` and never interpret `BeatIndex` itself.

### `ProducerClockNormalization`

A narrowly scoped producer-boundary concept, not a second map. It is applied before timeline evidence enters map fitting when a producer's source clock is known to differ from `VisualizationTimeline.SampleRate`.

Required information:

- producer identity/path;
- source sample rate/clock and destination final sample rate/clock;
- explicit conversion direction and deterministic sample rounding policy;
- event families covered (timing, beats, notes, pitch, rhythm, DAC, markers as applicable);
- evidence that the conversion is unambiguous.

If either clock or the relationship is unknown/ambiguous, reject the timeline with an actionable diagnostic. No downstream MIDI component may compensate for a clock mismatch.

## 2. Musical timing model

### `MusicalTimeMapOptions`

Existing location: `Timing/MusicalTimeMapOptions.cs`.

The existing option surface is retained and extended only as necessary:

- source mode equivalent to `auto`, `driver`, `symbolic`, `fixed`;
- fixed finite positive BPM (`FixedBpm`);
- one explicit phase convention (quarter position at sample zero or beat-offset samples; CLI sign convention must be documented consistently);
- `QuartersPerBeat` only where the audited producer unit requires it;
- optional `Meter` and `FirstDownbeatSample`;
- `StrictTiming`;
- tempo-change detection only as an existing fitting control, not a new option bag.

No redundant phase aliases or generic dictionary options are introduced.

### `BeatAnchor`

Existing fitter input: `(Sample, QuarterPosition, Confidence?)`.

Before fitting:

1. sort by sample and deterministic secondary quarter key;
2. reject non-finite positions, impossible backward motion, and unresolved loop-reset discontinuities;
3. deduplicate exact same-sample/same-quarter anchors harmlessly;
4. report same-sample conflicting positions and fail in strict mode;
5. preserve nonzero first positions and fractional positions after explicit unit normalization.

### `TempoSegment`

Existing location: `Timing/TempoSegment.cs`. Each segment represents:

- `[StartSample, EndSample)` sample interval;
- `QuarterPositionAtStart` in absolute double-precision quarter time;
- `SamplesPerQuarter` and derived BPM;
- timing source/confidence;
- derived MIDI `MicrosecondsPerQuarter` after validating the 24-bit representation.

Adjacent segments must satisfy:

```text
next.QuarterPositionAtStart
    == previous.QuarterPositionAt(next.StartSample)
```

within the specified floating-point tolerance. The next origin is threaded from the prior segment; it is not independently rounded in MIDI ticks.

### `MusicalTimeMap`

Existing location: `Timing/MusicalTimeMap.cs`. It is the only source-domain timing authority:

```text
source sample -> absolute quarter position -> absolute MIDI tick
```

It must support negative/fractional quarter positions, segment lookup at boundaries, and absolute conversions that do not accumulate rounded deltas. It does not serialize MIDI bytes or infer BPM.

## 3. Diagnostics and failure state

### `TimingDiagnostics`

Existing location: `Timing/TimingDiagnostics.cs`. Keep this as compact data. It must expose, directly or through stable nested data:

- selected timing source;
- sample rate;
- raw/input, accepted, rejected anchor counts;
- rejected-anchor sample, beat/quarter position, residual, and reason;
- estimated BPM and tempo segment count;
- RMS and maximum residual;
- beat phase/intercept and whether phase is authoritative;
- whether tempo is authoritative;
- meter/downbeat status;
- global origin tick offset when export has resolved it;
- warnings distinguishing tempo-known/phase-unknown/meter-unknown and explicit fallback use.

### Strict vs non-strict resolution

- `auto`: choose the strongest trustworthy evidence and report the choice; never silently claim alignment from a default 120 BPM/sample-zero origin.
- `driver`: require usable authoritative driver timing; fail if unavailable or ambiguous.
- `symbolic`: explicitly use the existing symbolic fallback.
- `fixed`: require finite positive BPM; actual alignment additionally requires an explicit phase convention.
- strict mode: fail on fundamental ambiguity (clock, unit, conflicting anchor, unresolved required phase, invalid tempo/PPQ, pathological fit, impossible segment order, or other spec-listed ambiguity).
- non-strict fallback: if compatibility retains a fixed fallback, diagnostics must state the fallback and that beat alignment is not guaranteed.

## 4. MIDI projection model

### `MidiEventBase` and event priority

Existing location: `Timing/Midi/MidiEvent.cs`. Events carry absolute nonnegative tick after the global origin is applied, track/channel where relevant, and enough stable metadata for deterministic sorting. Semantic priority is explicit:

1. Note Off;
2. conductor state changes effective at the tick (Set Tempo, Time Signature);
3. bank/program/controller setup;
4. pitch bend;
5. Note On;
6. non-state text/markers.

A stable secondary key (track, channel, event kind, source sequence/index, or equivalent) is required for equal-priority events. No dictionary/enumeration/object identity/thread scheduling dependence is allowed.

### Global origin shift

After every source event has an absolute musical position/tick, compute one `originTickOffset >= 0` such that the minimum exported tick is nonnegative. Apply this same offset to conductor events, notes, pitch, rhythm, DAC, markers, and loops. Do not shift tracks or tempo segments independently.

### Note and pitch events

For each positive-duration note:

```text
startTick = map.SampleToTick(StartSample, ppq) + originTickOffset
endTick   = map.SampleToTick(EndSample, ppq) + originTickOffset
```

Map endpoints independently so a tempo transition inside a note is respected. If positive source duration rounds to equal ticks, force `endTick = startTick + 1`. Map every pitch sample independently. If bend range is not MIDI-default, emit the standard RPN setup once per affected channel/state and clamp bend values to legal 14-bit range without wrapping.

### Rhythm, DAC, markers, loops

All timed rhythm and DAC triggers, and all loop/section markers, use the same map and global offset. DAC identity mapping (`raw PCM -> canonical sample ID -> MIDI note`) is independent of trigger timing; changing identity assignment cannot move ticks. A DAC helper may remain only if it receives an already-established map/mapper and does not establish tempo or convert from sample rate/BPM itself.

### Conductor and musical tracks

The writer receives Format 1 data with track 0 as conductor and one logical-source-voice track per existing policy. Track 0 contains track name, Set Tempo events, Time Signature only when meter is known, applicable markers, and exactly one EOT. Musical tracks share PPQ, map, and origin offset and contain no conductor-only state.

Default PPQ is 960 and export remains unquantized. Pickup timing is preserved by the negative/fractional map plus global shift; it is never snapped to the first observed beat.

## 5. MIDI serialization model

### `MidiFileWriter`

Existing location: `Timing/Midi/MidiFileWriter.cs`. Input is a sorted absolute event stream plus configured PPQ; no source samples, BPM fit, phase inference, or samples-per-tick calculation appears here. It must produce:

- valid `MThd` Format 1 header and configured division;
- valid `MTrk` chunks with correct big-endian lengths;
- nonnegative delta times generated only at serialization;
- valid 24-bit Set Tempo values;
- VLQ encoding for `0`, `0x7F`, `0x80`, `0x3FFF`, `0x4000`, `0x1FFFFF`, `0x200000`, `0x0FFFFFFF`, with rejection beyond the MIDI range;
- one effective EOT event per track;
- byte-identical output for identical timeline/options/PPQ.

## 6. Timing report contract

`--timing-report` writes the compact JSON contract in `contracts/timing-report.schema.json`. The report is a projection of resolved map/diagnostics, not a second source of truth. Stable top-level data includes sample rate, PPQ, source, phase/tempo authority, origin offset, nullable meter/downbeat state, anchor counts/residuals, segment starts/quarter origins/BPM, and warnings. Existing property naming conventions may be retained where they are already part of the implementation, but placeholder PPQ values must be replaced with the configured PPQ.
