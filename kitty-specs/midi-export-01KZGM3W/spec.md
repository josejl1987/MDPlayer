# MDPlayer MIDI Export Timing Hardening — Coding-Agent Specification

## 1. Mission

Fix and complete the existing MIDI export timing implementation so exported Standard MIDI Files:

* preserve the source timeline timing accurately;
* derive musical position from the existing driver beat/timing information whenever possible;
* have correct BPM;
* have correct beat phase;
* preserve pickup notes;
* handle tempo changes without drift;
* map melodic notes, pitch bends, rhythm events, and YM2612 DAC events through the same musical-time model;
* import into a DAW with beats aligned to the intended musical grid;
* remain deterministic.

This is **not** a greenfield implementation.

The branch already contains the timing architecture. Extend and correct it.

Do not create a second timing system.

Current branch checked:

```text
feature/linux-fmp-renderer
HEAD 9dbd8d1de0603bc758f76796b575537bb928309d
```

---

# 2. Existing architecture

The current tree already contains:

```text
MDPlayer/src/MDPlayer.Fmp.Core/Timing/
    MusicalTimeMap.cs
    MusicalTimeMapBuilder.cs
    MusicalTimeMapOptions.cs
    BeatGridFitter.cs
    TimingDiagnostics.cs
    TempoSegment.cs
    TimingSource.cs

MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/
    MusicalMidiExporter.cs
    MidiFileWriter.cs
    MidiEvent.cs
```

The visualization timeline already exposes timing evidence including:

```csharp
DriverTimingEvent
{
    SamplePosition
    TimerBValue
    ValidatedBpm
}
```

and:

```csharp
BeatEvent
{
    SamplePosition
    BeatIndex
}
```

Notes and pitch changes are also timestamped in source samples.

The current `MusicalMidiExporter` already consumes a `MusicalTimeMap`.

## KEEP

Keep:

* `MusicalTimeMap`
* `MusicalTimeMapBuilder`
* `BeatGridFitter`
* `TimingDiagnostics`
* `TempoSegment`
* `TimingSource`
* `MusicalMidiExporter`
* the current timeline sample-position representation

## CHANGE

Harden:

* semantics of the beat anchors;
* timing-source precedence;
* phase calculation;
* tempo segment construction;
* segment continuity;
* MIDI origin shifting;
* conductor-track generation;
* event ordering;
* edge cases around retriggers and zero-tick notes;
* all DAC/rhythm timing paths;
* CLI failure/fallback behaviour;
* diagnostics;
* tests.

## DO NOT

Do not:

* introduce another `MusicalTimeMap`;
* build a parallel MIDI timing implementation;
* rewrite `BeatGridFitter` wholesale;
* implement audio BPM detection;
* add default quantization;
* assume 4/4;
* assume sample zero is beat zero;
* treat BPM as sufficient to establish alignment.

---

# 3. Core timing model

There are three separate concepts.

They MUST remain separate.

## 3.1 Tempo

Tempo specifies the rate of musical time.

For MIDI this means:

```text
microseconds per quarter note
```

not beats of an arbitrary driver unit.

## 3.2 Beat phase

Beat phase specifies where the musical beat grid lies relative to playback sample zero.

For example:

```text
sample 0       = quarter position -0.37
sample 12348   = quarter position 0
```

is perfectly valid.

A correct BPM with the wrong phase is still incorrectly aligned MIDI.

## 3.3 Meter/downbeat

Meter specifies bar grouping.

For example:

```text
4/4
3/4
6/8
```

Downbeat specifies which beat begins a measure.

Knowing beat locations does not automatically reveal the downbeat.

### Required invariant

Never collapse these concepts into:

```text
BPM => beat 0 => bar 1
```

That assumption is invalid.

---

# 4. Mandatory producer audit

Before changing the fitting algorithm, locate **every producer** of:

```csharp
TimelineBuilder.AddTiming(...)
```

and:

```csharp
TimelineBuilder.AddBeat(...)
```

Do this first.

Do not infer semantics from the record names.

Document and test the actual semantics.

The audit MUST answer all of these questions.

## 4.1 Sample clock

Confirm that:

```text
DriverTimingEvent.SamplePosition
BeatEvent.SamplePosition
NoteEvent.StartSample
NoteEvent.EndSample
PitchChange.SamplePosition
rhythm event sample positions
DAC event sample positions
```

are all expressed on the same final playback/output sample clock.

If one producer uses a different clock, correct that producer or normalize it at one clearly defined boundary.

Do not compensate for different clocks inside the MIDI writer.

---

# 5. BeatIndex semantics

Determine exactly what:

```csharp
BeatEvent.BeatIndex
```

represents.

Answer explicitly:

* Is one increment one quarter note?
* Is one increment a driver beat?
* Is it a timer tick?
* Is it some subdivision?
* Can values be fractional?
* Does it reset at loop boundaries?
* Can the first emitted value be something other than zero?
* Can values jump?
* Is the event emitted before or after a timing change at the same sample?

### Critical rule

Do **not** assume:

```text
driver beat == MIDI quarter note
```

If the driver's beat unit differs from a quarter note, normalize it once during construction of `MusicalTimeMap`.

For example:

```text
driver beat
    ↓ explicit scale
quarter-note position
    ↓
MusicalTimeMap
```

Do not hide this scale factor inside `MusicalMidiExporter`.

Add a regression test that locks the discovered semantics.

---

# 6. ValidatedBpm semantics

Audit:

```csharp
DriverTimingEvent.ValidatedBpm
```

Determine whether it represents actual effective playback BPM after:

* driver tempo;
* speed;
* timer settings;
* playback multipliers;
* song-specific timing rules.

Also determine exactly when the new value becomes effective relative to:

```csharp
DriverTimingEvent.SamplePosition
```

The event sample must have an unambiguous meaning such as:

```text
new tempo begins at this sample
```

Lock that convention in a test.

Do not derive BPM directly from `TimerBValue` unless the driver timing semantics have been explicitly validated.

Prefer `ValidatedBpm` over reverse engineering the timer in the MIDI layer.

---

# 7. MusicalTimeMap is the sole timing authority

All MIDI timing must follow:

```text
source sample position
        ↓
MusicalTimeMap
        ↓
absolute quarter-note position
        ↓
absolute MIDI tick
```

No MIDI component may independently implement:

```text
sample → seconds → BPM → tick
```

or:

```text
samplesPerTick = ...
```

outside the shared map.

This includes:

* melodic notes;
* note ends;
* retriggers;
* pitch changes;
* rhythm events;
* YM2612 DAC triggers;
* loop markers;
* tempo transitions;
* section markers.

Search the entire branch for any independent sample-to-MIDI-tick implementation.

Remove or route it through `MusicalTimeMap`.

In particular, if a DAC-specific exporter still accepts something conceptually equivalent to:

```csharp
sampleRate
bpm
```

and performs its own timing conversion, remove that timing responsibility.

The DAC exporter may decide:

```text
DAC sample identity → MIDI note
```

but MUST NOT decide:

```text
source sample → MIDI tick
```

---

# 8. Absolute timing only

Never calculate source event timing by repeatedly accumulating rounded MIDI delta ticks.

Wrong:

```csharp
currentTick += Round(sampleDelta * ticksPerSample);
```

This accumulates error.

Correct conceptual conversion:

```text
tick =
    segmentStartTick
    + round(
        (sourceSample - segmentStartSample)
        × PPQ
        / samplesPerQuarter
      )
```

Every event starts from its original absolute source sample.

Delta times exist only when serializing the final sorted MIDI event stream.

### Required long-duration test

A constant-tempo ten-minute timeline must show no accumulating beat drift.

The final beat should be as accurate as the first beat apart from normal one-tick integer quantization.

---

# 9. TempoSegment continuity

Tempo segments must be continuous in musical time.

For adjacent segments:

```text
A
[startA, startB)

B
[startB, ...)
```

require:

```text
B.QuarterPositionAtStart
    ==
A.SampleToQuarterPosition(startB)
```

within floating-point tolerance.

Construct the next segment musical origin from the previous segment.

Do not independently recalculate a rounded MIDI tick origin for every tempo segment.

Continuity must exist first in double-precision musical time.

MIDI tick rounding happens afterward.

Add an invariant check in debug/tests.

---

# 10. Timing source precedence

`MusicalTimeMapBuilder` must have explicit deterministic source selection.

Use this priority unless the existing driver semantics prove a stronger source:

```text
1. authoritative driver beat anchors
2. explicit sequencer/driver clock or bar information
3. validated driver tempo information
4. explicit user override
5. existing symbolic timeline inference
```

Audio inference is outside this task.

## Why beat anchors rank highly

A BPM value gives:

```text
rate
```

Beat anchors can provide:

```text
rate + phase
```

Therefore good driver beat anchors are better evidence for DAW alignment than BPM alone.

## Important combination rule

Driver BPM data may establish or validate segment rate without destroying phase established from authoritative beat anchors.

Do not do this:

```text
fit beat phase
↓
see ValidatedBpm
↓
rebuild map from BPM starting at quarter zero
```

That loses the alignment.

---

# 11. Explicit timing source modes

Use or extend the existing options rather than creating a parallel configuration object.

Support the equivalent of:

```text
auto
driver
symbolic
fixed
```

## `auto`

Use the highest-confidence available source.

It MUST prefer verified driver beat/timing data over heuristics.

It MUST NOT silently claim beat alignment by falling back to:

```text
120 BPM, sample 0 = beat 0
```

## `driver`

Require usable authoritative driver timing.

If it cannot construct a trustworthy map, fail.

Do not silently switch to symbolic inference.

## `symbolic`

Explicitly use the existing symbolic inference path.

Do not use symbolic inference merely because authoritative data has a small amount of harmless jitter.

## `fixed`

Require:

```text
BPM
```

For actual beat alignment also require an explicit phase convention such as:

```text
beat offset in samples
```

A fixed BPM with no phase can produce correctly spaced MIDI, but it is not necessarily beat aligned.

Diagnostics must say so.

---

# 12. BeatGridFitter

Keep the current fitter.

Do not replace it with a new framework.

Harden its behaviour where needed.

Its conceptual job is to fit:

```text
quarterPosition =
    intercept +
    sample / samplesPerQuarter
```

from beat anchors.

The fit must establish:

```text
tempo/rate
phase/intercept
```

---

# 13. Input validation for beat fitting

Before fitting:

* sort anchors by sample;
* detect duplicate sample positions;
* detect conflicting beat positions;
* verify musical positions are sensible;
* detect impossible backwards motion;
* account for documented loop-reset semantics if they exist.

## Duplicate identical anchor

For example:

```text
sample 10000, beat 4
sample 10000, beat 4
```

Deduplicate harmlessly.

## Conflicting anchor

For example:

```text
sample 10000, beat 4
sample 10000, beat 5
```

Do not silently average it.

Report the conflict.

Strict mode must fail.

## Backwards BeatIndex

Do not immediately assume corruption.

First apply the producer semantics discovered in section 5.

If a driver intentionally resets BeatIndex on looping, normalize the index into a continuous musical position before fitting.

Do this once.

Do not put loop-reset exceptions throughout the MIDI code.

---

# 14. Robust fit

Small timestamp jitter must not create unstable BPM.

Use the existing robust-fit approach.

Required behaviour:

1. estimate a robust interval/rate;
2. establish likely inliers;
3. fit rate and intercept using the inliers;
4. compute residuals;
5. reject sufficiently severe isolated outliers;
6. report the rejection.

Do not overfit.

Do not create a new tempo segment because one beat arrives a few samples early.

---

# 15. Timing diagnostics

`TimingDiagnostics` must expose enough information to understand why MIDI aligned—or did not.

At minimum expose:

```text
selected timing source
sample rate
number of raw beat anchors
number of accepted anchors
number of rejected anchors
estimated BPM
tempo segment count
RMS anchor residual
maximum anchor residual
beat phase/intercept
whether phase is authoritative
whether tempo is authoritative
meter status
downbeat status
warnings
```

For each rejected anchor include enough data to identify:

```text
sample
beat position
residual
reason
```

Do not dump huge diagnostic structures by default.

---

# 16. Strict timing mode

Support a strict mode through the existing configuration/CLI plumbing.

Strict mode must fail export if any fundamental timing ambiguity remains.

Examples:

* incompatible beat anchors;
* unknown beat-unit conversion;
* unresolved beat phase when alignment is required;
* pathological fit residual;
* invalid tempo;
* impossible tempo segment ordering;
* conflicting duplicate anchors.

Strict mode must not silently fall back to 120 BPM.

---

# 17. Tempo changes

Support real tempo changes as piecewise `TempoSegment`s.

Do not emit one MIDI tempo event for every small variation in measured beat intervals.

Differentiate:

```text
real sustained tempo change
```

from:

```text
sampling / timer / observation jitter
```

Prefer explicit validated driver tempo transitions when available.

Use the beat anchors to verify phase/continuity.

---

# 18. Tempo-change requirements

For each accepted tempo transition:

```text
sample S
old tempo → new tempo
```

construct a new segment beginning at `S`.

The musical position at `S` must remain identical across both segments.

Add tests for:

```text
note starts before transition
note ends after transition
pitch bend occurs before transition
pitch bend exactly at transition
note starts exactly at transition
beat anchor exactly at transition
```

All must remain correctly ordered.

---

# 19. MIDI tempo representation

MIDI Set Tempo uses:

```text
microseconds per quarter note
```

Use:

```text
µsPerQuarter = round(60_000_000 / BPM)
```

Validate result is representable by the MIDI Set Tempo 24-bit value.

Do not unnecessarily emit duplicate tempo events.

After conversion to integer `µs/qn`, if adjacent segments have the same value, emit one Set Tempo event unless another semantic requirement demands otherwise.

Do not compare raw floating-point BPM for this deduplication.

Compare the MIDI value actually being emitted.

---

# 20. Beat phase

Never assume:

```text
source sample 0 == quarter position 0
```

`MusicalTimeMap` must be capable of representing:

```text
sample 0 -> fractional quarter position
```

including negative positions.

Example:

```text
sample 0 = quarter -0.25
```

This represents a pickup before the first chosen musical beat.

Preserve it.

---

# 21. Global MIDI tick origin

MIDI events cannot use negative absolute ticks.

After all source events have been mapped to musical positions, calculate a **single global tick shift**.

Conceptually:

```text
rawTick = musicalTimeMap.SampleToTick(sample, ppq)

exportedTick =
    rawTick + originTickOffset
```

where:

```text
originTickOffset >= 0
```

and all exported ticks become nonnegative.

Use the same offset for:

* conductor events;
* note events;
* pitch bend;
* rhythm;
* DAC;
* markers;
* loops.

Do not shift each track separately.

Do not shift each tempo segment separately.

That would destroy alignment.

---

# 22. Pickup notes

Pickup notes must remain pickups.

Example:

```text
note                  downbeat
 |----------------------|
quarter -0.5           quarter 0
```

After the MIDI origin shift this might become:

```text
tick 0                 tick 480
 |-----------------------|
pickup                 downbeat
```

This is correct.

Do not snap the pickup to the first beat.

---

# 23. Meter and downbeat

Do not infer a MIDI Time Signature just because BPM is known.

Priority:

```text
1. explicit driver/format bar information
2. explicit user override
3. already-existing high-confidence meter inference, if present
4. unknown
```

If meter is unknown:

```text
omit Time Signature meta event
```

Do not invent:

```text
4/4
```

Likewise, do not call the first observed beat the first downbeat.

Beat phase and bar phase are not the same thing.

---

# 24. MIDI file format

Export Standard MIDI File:

```text
Format 1
```

Use one conductor track plus musical tracks.

Default:

```text
PPQ = 960
```

Keep PPQ configurable.

Do not change default PPQ casually once tests depend on it.

---

# 25. Track 0 — conductor track

Track 0 contains only timeline-wide information.

At minimum:

```text
Track Name
Set Tempo
Time Signature, only when known
loop/section markers where applicable
End of Track
```

Optionally include compact textual metadata indicating timing source/confidence if that is consistent with the existing exporter.

Do not place per-channel note/controller data on Track 0.

---

# 26. Musical tracks

Initially retain the current policy of one track per logical source voice unless the existing implementation deliberately groups them differently.

Timing changes are shared globally through Track 0.

Each voice track must use the same:

```text
PPQ
MusicalTimeMap
originTickOffset
```

---

# 27. Quantization

Default export is:

```text
UNQUANTIZED
```

This task does not add quantization.

The point is to preserve the actual source scheduling relative to the recovered beat grid.

Do not silently snap:

```text
note-ons
note-offs
rhythm triggers
DAC triggers
```

to musical subdivisions.

Any future quantizer must be a separate destructive transformation after timing reconstruction.

---

# 28. Notes

For every `NoteEvent`:

```text
startTick =
    map(StartSample) + origin

endTick =
    map(EndSample) + origin
```

Map start and end independently from their absolute source samples.

Do not derive:

```text
endTick = startTick + convertedDuration
```

because tempo changes can occur inside the note.

A note that spans a tempo transition must therefore automatically produce the right musical duration.

---

# 29. Minimum MIDI note duration

A source note can have positive sample duration while both endpoints round to the same MIDI tick.

Example:

```text
StartSample < EndSample
```

but:

```text
startTick == endTick
```

Do not emit a zero-duration note.

If the source duration is positive:

```text
endTick = max(endTick, startTick + 1)
```

This one-tick correction is acceptable and must be documented in tests.

Do not globally quantize surrounding events to solve this.

---

# 30. Retriggers

For a same-pitch retrigger on the same MIDI channel/tick:

```text
Note Off
Note On
```

must be emitted in that order.

Never depend on list insertion order to obtain this.

Ordering must be explicit.

---

# 31. Pitch changes

Map every `PitchChange.SamplePosition` independently through `MusicalTimeMap`.

Do not interpolate MIDI ticks from the note start.

For each note:

```text
source pitch sample
    ↓
MusicalTimeMap
    ↓
absolute MIDI tick
```

Pitch events crossing a tempo change therefore remain correct automatically.

---

# 32. Pitch bend setup

If the exporter uses a pitch-bend range other than MIDI's assumed/default behaviour, explicitly configure it using RPN.

Do not make DAW playback depend on the destination software coincidentally assuming the same bend range.

Required controller sequence follows standard Pitch Bend Sensitivity RPN semantics.

Avoid resending it before every note if channel state has not changed.

---

# 33. Pitch bend range

The exporter must have one explicit, documented bend-range policy.

For each pitch:

1. choose the MIDI base note according to the current policy;
2. calculate bend offset;
3. clamp to the legal 14-bit bend range;
4. never wrap arithmetic.

If the pitch cannot be represented using the selected bend range, use the nearest representable base note where possible.

Do not silently produce overflow.

---

# 34. Same-tick MIDI ordering

Create an explicit deterministic event-priority system.

Do not depend on insertion order from LINQ or dictionary enumeration.

For events sharing a tick, use this semantic ordering:

```text
1. Note Off
2. conductor state changes effective at this tick
   - Set Tempo
   - Time Signature
3. bank/program/controller state setup
4. pitch bend
5. Note On
6. non-state textual/marker metadata
```

Where two events share tick and priority, use a stable deterministic secondary ordering.

For example:

```text
track/channel
event type
source sequence/index
```

The exact secondary key is implementation-specific, but output MUST be deterministic.

---

# 35. Tempo boundary ordering

A tempo event beginning at tick `T` must become active at tick `T`.

Notes that also start at `T` therefore remain associated with the intended new musical timeline position.

Keep the tempo event before musical Note On events at the same tick for deterministic representation.

---

# 36. Rhythm events

Rhythm events must use:

```text
MusicalTimeMap.SampleToTick(...)
```

plus the same global origin shift.

There must be no rhythm-specific samples-per-tick calculation.

Existing rhythm note/channel mapping is not part of the timing algorithm.

Do not refactor unrelated percussion allocation unless needed to remove duplicate timing conversion.

---

# 37. YM2612 DAC samples

DAC sample identity and DAC timing are separate concerns.

The existing/sample-deduplication design can continue assigning a unique MIDI note to each unique DAC sample.

Timing is simply:

```text
DAC trigger sample
    ↓
MusicalTimeMap
    ↓
MIDI tick
```

Never derive DAC tick timing from:

```text
DAC sample duration
sample rate + fixed BPM
local 120 BPM default
```

If any current DAC-specific MIDI exporter performs its own conversion, refactor it.

Preferred design:

```text
DAC event
    ↓
shared MusicalMidiExporter/event builder
    ↓
shared MusicalTimeMap
```

If keeping a separate DAC helper is simpler, it must receive an already-established timing mapper rather than establishing tempo itself.

---

# 38. DAC deduplication must not affect timing

These two responsibilities must remain independent:

```text
raw DAC PCM → canonical sample ID → MIDI note
```

and:

```text
trigger source sample → MIDI tick
```

Changing sample deduplication rules must never move MIDI events.

Likewise, changing BPM/phase recovery must never change DAC sample identity.

Add a regression test for this separation.

---

# 39. Loop handling

Audit how BeatIndex behaves when playback loops.

Two possibilities are valid:

```text
BeatIndex continues monotonically
```

or the driver may report:

```text
BeatIndex resets
```

The producer audit must determine which occurs.

If reset occurs, convert the repeated local BeatIndex into a continuous musical index before fitting.

Example:

```text
0 1 2 3
0 1 2 3
```

may normalize to:

```text
0 1 2 3
4 5 6 7
```

only if that is actually the driver's defined loop behaviour.

Do not infer a reset solely because an arbitrary malformed anchor went backwards.

---

# 40. Loop markers

Map loop marker samples through exactly the same timing map.

A loop marker can legitimately occur:

```text
on beat
off beat
```

Do not quantize it.

If exported as MIDI markers, preserve its exact recovered musical position.

---

# 41. Symbolic timing inference

The project already has symbolic timing facilities.

Do not expand them significantly in this pass.

The purpose of this task is to make authoritative timing solid first.

Symbolic inference is fallback only.

It must not override valid driver timing in automatic mode.

---

# 42. Half/double tempo ambiguity

When symbolic inference is used, values such as:

```text
70 BPM
140 BPM
```

may both describe the same onset pattern.

Do not hide this ambiguity behind arbitrary confidence.

Where the existing system can expose candidate ambiguity, report it.

In strict mode, a materially ambiguous symbolic result should fail unless a user override resolves it.

Do not implement a complicated new inference engine for this task.

---

# 43. MIDI writer correctness

Audit `MidiFileWriter.cs`.

The writer is responsible only for encoding the already-resolved MIDI event stream.

It must not know anything about source samples or BPM fitting.

Required invariants:

```text
valid MThd header
SMF Format 1
configured PPQ division
valid MTrk chunks
correct big-endian lengths
correct VLQ delta times
events sorted by absolute tick + explicit priority
nonnegative delta times
End of Track on every track
```

---

# 44. Variable Length Quantity

Add boundary tests for MIDI VLQ encoding.

At minimum:

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

Round-trip or compare against expected byte sequences.

Reject values outside the representable MIDI VLQ range rather than truncating.

---

# 45. MIDI deterministic output

The exact same:

```text
timeline
options
PPQ
```

must generate byte-identical `.mid` output on repeated exports.

No ordering may depend on:

* dictionary enumeration;
* hash codes;
* object identity;
* thread scheduling;
* non-stable sorting of equal elements.

Add a byte-for-byte deterministic regression test.

---

# 46. End of Track

Every MIDI track MUST finish with exactly one effective End of Track meta event.

Its delta must be computed from the previous event normally.

Do not rely on a MIDI parser repairing malformed tracks.

---

# 47. CLI integration

Do not create a second MIDI command if one already exists.

Extend the existing MIDI export command/options.

Expose the equivalent of:

```text
--ppq 960

--tempo-source auto|driver|symbolic|fixed

--bpm <number>

--beat-offset-samples <number>

--meter <numerator/denominator>

--first-downbeat-sample <number>

--timing-report <path>

--strict-timing
```

Use names matching existing CLI conventions if they differ.

Do not redesign the CLI framework.

---

# 48. CLI option semantics

## `--ppq`

Default:

```text
960
```

Require positive MIDI-valid division.

## `--tempo-source auto`

Choose the best trustworthy source.

Report which source was selected.

## `--tempo-source driver`

Require driver timing.

Fail if unavailable or invalid.

## `--tempo-source symbolic`

Force existing symbolic analysis.

## `--tempo-source fixed`

Require:

```text
--bpm
```

## `--bpm`

Must be finite and positive.

Reject:

```text
0
negative
NaN
Infinity
```

## `--beat-offset-samples`

Define explicit beat phase for fixed/manual timing.

Document its sign convention.

For example:

```text
sample position at which quarter position zero occurs
```

Pick one convention and use it everywhere.

Do not introduce multiple synonymous phase options internally.

## `--meter`

Optional.

Do not default to 4/4.

## `--first-downbeat-sample`

Optional explicit bar phase.

Require compatible meter information.

## `--strict-timing`

Enable the failures specified above.

---

# 49. Non-strict fallback

If compatibility requires retaining a fixed-tempo fallback, it must be explicit in diagnostics.

For example:

```text
WARNING:
No authoritative beat phase was available.
Export used fallback tempo 120 BPM with sample 0 as arbitrary quarter origin.
The MIDI is not guaranteed to align with the song's musical beat grid.
```

Do not call this result:

```text
beat aligned
```

No silent 120 BPM fallback is allowed in strict mode.

---

# 50. Timing report

`--timing-report file.json` should serialize useful timing diagnostics.

Keep the schema compact and stable.

Suggested structure:

```json
{
  "sampleRate": 44100,
  "ppq": 960,
  "source": "DriverBeatAnchors",
  "phaseAuthoritative": true,
  "tempoAuthoritative": true,
  "originTickOffset": 960,
  "meter": null,
  "downbeatKnown": false,
  "anchors": {
    "input": 124,
    "accepted": 123,
    "rejected": 1,
    "rmsResidualSamples": 1.7,
    "maxResidualSamples": 4.0
  },
  "segments": [
    {
      "startSample": 0,
      "quarterAtStart": -0.5,
      "bpm": 120.0
    }
  ],
  "warnings": []
}
```

Use actual existing property naming conventions where practical.

Do not introduce an elaborate diagnostics framework.

---

# 51. Unit tests — constant tempo

Add a synthetic 120 BPM case.

For sample rate:

```text
48,000 Hz
```

one quarter at 120 BPM is:

```text
24,000 samples
```

With:

```text
PPQ = 960
```

require:

```text
sample 0       -> tick 0
sample 24000   -> tick 960
sample 48000   -> tick 1920
sample 72000   -> tick 2880
```

subject to any explicitly configured global origin shift.

---

# 52. Unit test — nonzero phase

Example anchors:

```text
sample 12000 -> quarter 0
sample 36000 -> quarter 1
sample 60000 -> quarter 2
```

at:

```text
48 kHz
120 BPM
```

Then:

```text
sample 0 -> quarter -0.5
```

The exported grid must preserve that half-beat pickup.

Do not force sample zero to quarter zero.

---

# 53. Unit test — nonzero starting BeatIndex

Input:

```text
sample 0     -> beat 128
sample 24000 -> beat 129
sample 48000 -> beat 130
```

must preserve the relative musical timeline.

Do not treat the first BeatIndex as zero just because it is the first event.

A global MIDI origin translation may move all output ticks, but intervals and phase relationships must be unchanged.

---

# 54. Unit test — pickup

Create:

```text
first note starts quarter -0.5
first downbeat quarter 0
```

Verify:

* all MIDI ticks are nonnegative;
* note still occurs 0.5 quarter before downbeat;
* origin shift is applied once globally.

---

# 55. Unit test — harmless duplicate anchor

Input:

```text
sample 24000 / beat 1
sample 24000 / beat 1
```

Expected:

```text
deduplicated or otherwise harmless
no timing distortion
```

---

# 56. Unit test — conflicting anchor

Input:

```text
sample 24000 / beat 1
sample 24000 / beat 2
```

Expected:

```text
diagnostic conflict
strict mode failure
```

Do not average.

---

# 57. Unit test — beat jitter

Generate a long set of beat anchors around a constant tempo with small sample noise.

Example:

```text
±2 samples
```

Expected:

* one stable tempo segment;
* BPM remains near truth;
* no tempo event per beat;
* low residual reported.

---

# 58. Unit test — outlier

Introduce one severe bad beat timestamp.

Expected:

* robust fitter rejects it;
* resulting tempo remains close to correct;
* rejected anchor appears in diagnostics;
* strict behaviour follows configured residual policy.

---

# 59. Unit test — real tempo change

Example:

```text
120 BPM for 16 quarters
150 BPM afterward
```

Require:

* exactly the required tempo transition;
* quarter position continuous at boundary;
* Set Tempo emitted at correct tick;
* no extra jitter segments.

---

# 60. Unit test — note crossing tempo change

Create a note:

```text
start = quarter 15.5
end   = quarter 16.5
```

with tempo change at quarter 16.

Verify start and end ticks derive correctly from their source samples.

This test specifically protects against:

```text
endTick = startTick + fixed-tempo duration
```

bugs.

---

# 61. Unit test — pitch at tempo boundary

Create a pitch change whose sample exactly equals a tempo segment boundary.

Verify:

* correct tick;
* stable same-tick ordering;
* no one-tick discontinuity.

---

# 62. Unit test — positive source duration rounds to zero ticks

Construct:

```text
EndSample > StartSample
```

but close enough that both map to one integer MIDI tick.

Expected:

```text
endTick == startTick + 1
```

---

# 63. Unit test — retrigger ordering

Same voice, same MIDI pitch:

```text
old note ends tick 960
new note begins tick 960
```

Encoded order must be:

```text
Note Off
Note On
```

---

# 64. Unit test — unknown meter

Provide excellent beat/tempo anchors but no bar information.

Expected:

```text
correct beat alignment
NO fabricated 4/4 Time Signature
```

---

# 65. Unit test — known meter

Provide authoritative:

```text
4/4
first downbeat sample S
```

Expected:

* valid Time Signature event;
* correct bar phase after global origin shift.

Do not change note timing to force a convenient bar 1.

---

# 66. Unit test — loops

Cover:

```text
loop marker exactly on beat
loop marker off beat
```

and the actual driver BeatIndex loop behaviour discovered during producer audit.

Ensure beat fitting does not interpret a loop reset as a huge negative tempo.

---

# 67. Unit test — DAC timing

Create a timeline containing:

```text
melodic NoteEvent at sample S
DAC trigger at sample S
rhythm trigger at sample S
```

All three must map to the same musical tick before event-priority differences.

This is the regression that prevents separate DAC timing math from returning.

---

# 68. Unit test — DAC identity independence

Export the same timed DAC sequence with two different valid sample-ID/note mappings.

Expected:

```text
MIDI note numbers may differ
event ticks must be identical
tempo track must be identical
```

---

# 69. Unit test — long-file drift

Synthetic constant-tempo timeline lasting at least ten minutes.

Create anchors throughout.

For every anchor:

```text
expectedTick =
    round(
        anchorQuarter * PPQ
        + originTickOffset
    )
```

Require:

```text
abs(actualTick - expectedTick) <= 1
```

including the final anchor.

There must be no error proportional to file duration.

---

# 70. Integration test — MIDI parser round trip

Do not validate only raw bytes produced by our writer.

Load the generated MIDI using an independent existing MIDI parser if the project already has one available.

Do not add a major dependency solely for this test unless unavoidable.

Verify:

```text
format == 1
division == configured PPQ
track count correct
tempo values correct
event ticks correct
EOT present
```

---

# 71. Anchor accuracy acceptance criterion

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

One tick is the maximum normal MIDI discretization tolerance.

A wider tolerance requires an explicit diagnostic reason.

---

# 72. Temporal reconstruction criterion

For source events not subject to the deliberate minimum-one-tick duration correction, reconstruction error must be no greater than approximately half one MIDI tick after rounding.

Do not use arbitrary millisecond tolerances when musical ticks provide the natural unit.

---

# 73. File-by-file work

## `Timing/MusicalTimeMap.cs`

Audit and harden:

* absolute sample → quarter conversion;
* segment lookup;
* segment-boundary behaviour;
* MIDI tick conversion;
* negative quarter positions;
* no cumulative rounding.

Do not add MIDI serialization responsibilities here.

---

## `Timing/MusicalTimeMapBuilder.cs`

This is the main coordination point.

Implement/harden:

* timing-source precedence;
* beat-unit normalization;
* beat-anchor validation;
* phase establishment;
* explicit tempo integration;
* piecewise tempo construction;
* strict-mode validation;
* fixed/manual overrides;
* diagnostic creation.

Do not put MIDI channel/instrument logic here.

---

## `Timing/MusicalTimeMapOptions.cs`

Extend only with options necessary for:

```text
source selection
fixed BPM
explicit beat phase
meter
downbeat
strict mode
```

Avoid redundant aliases and generic option bags.

---

## `Timing/BeatGridFitter.cs`

Keep current implementation.

Add or correct only:

* validation;
* stable robust fitting;
* outlier handling;
* diagnostics;
* explicitly tested residual behaviour.

Do not rewrite it merely because a different mathematical approach exists.

---

## `Timing/TimingDiagnostics.cs`

Expose the compact diagnostics required by this spec.

Keep this as data.

Do not make it responsible for CLI formatting.

---

## `Timing/TempoSegment.cs`

Ensure representation contains enough information to guarantee:

```text
sample interval
quarter position at segment start
tempo / samples-per-quarter
source/confidence as currently appropriate
```

Keep segment continuity invariant testable.

---

## `Timing/Midi/MusicalMidiExporter.cs`

Harden:

* one shared `MusicalTimeMap`;
* one global origin shift;
* conductor-track creation;
* tempo events;
* optional meter;
* absolute note start/end mapping;
* pitch mapping;
* rhythm/DAC mapping where owned here;
* explicit same-tick priorities;
* minimum note duration;
* deterministic output.

Do not perform BPM inference here.

---

## `Timing/Midi/MidiEvent.cs`

If necessary, give events enough stable metadata to support explicit deterministic sorting.

Prefer a small priority/source-order field over several event subclasses solely for sorting.

KISS.

---

## `Timing/Midi/MidiFileWriter.cs`

Restrict to:

```text
sorted absolute MIDI event stream
→
valid Standard MIDI File bytes
```

Harden:

* deterministic ordering contract;
* delta generation;
* VLQ;
* chunk sizes;
* EOT.

It must have no sample-domain timing calculations.

---

## Existing CLI MIDI files

Do not create another command framework.

Wire the existing MIDI command/options into:

```text
MusicalTimeMapOptions
MusicalMidiExporter
TimingDiagnostics
```

Add `--timing-report` through the current CLI conventions.

---

# 74. Implementation sequence

Follow this order.

Do not jump ahead.

## Batch A — lock source semantics

1. Find every `AddBeat`.
2. Find every `AddTiming`.
3. Document BeatIndex unit.
4. Document loop behaviour.
5. Document sample clock.
6. Document timing-event activation point.
7. Add regression tests.

Stop and correct source semantics first if any are wrong.

Do not compensate for bad source timestamps downstream.

---

## Batch B — MusicalTimeMap invariants

1. Add absolute-conversion tests.
2. Add negative-quarter tests.
3. Add continuity tests.
4. Add ten-minute no-drift test.
5. Fix only failures discovered.

---

## Batch C — driver beat fitting

1. Normalize BeatIndex into quarter units.
2. Handle duplicate anchors.
3. Reject conflicts.
4. Harden robust fit.
5. Establish phase.
6. Add diagnostics.
7. Add strict failure cases.

---

## Batch D — tempo segments

1. Integrate validated tempo transitions.
2. Preserve fitted phase.
3. Guarantee continuity.
4. Suppress jitter-driven segment spam.
5. Add crossing-boundary tests.

---

## Batch E — MIDI origin/conductor

1. Calculate one global tick offset.
2. Support pickups.
3. Generate Set Tempo events.
4. Emit Time Signature only when known.
5. Map markers/loops.
6. Add conductor tests.

---

## Batch F — musical events

1. Map note start/end absolutely.
2. Enforce minimum one-tick positive note.
3. Map every pitch change absolutely.
4. Set explicit pitch-bend range.
5. Enforce same-tick ordering.
6. Route rhythm through same map.
7. Route DAC through same map.

---

## Batch G — MIDI writer

1. Validate track sorting.
2. Add explicit priority.
3. Add VLQ boundaries.
4. Validate EOT.
5. Add deterministic byte test.

---

## Batch H — CLI and diagnostics

1. Wire timing-source selection.
2. Wire BPM override.
3. Wire phase override.
4. Wire meter/downbeat override.
5. Wire strict mode.
6. Wire timing report.
7. Remove silent misleading fallbacks.

---

# 75. Guardrails for the coding agent

These are mandatory.

### DO NOT create another timing abstraction.

Use the existing `MusicalTimeMap`.

### DO NOT replace the existing timing subsystem with a new library.

Fix the existing code.

### DO NOT rewrite `BeatGridFitter` unless a test proves its design fundamentally incapable of satisfying an invariant.

Small targeted corrections only.

### DO NOT add audio beat detection.

Not part of this pass.

### DO NOT use FFT/onset detection to fix data that the driver already provides.

### DO NOT assume 4/4.

### DO NOT assume the first observed beat is a downbeat.

### DO NOT assume sample zero is beat zero.

### DO NOT assume BPM establishes beat phase.

### DO NOT assume an FMP beat equals a MIDI quarter.

Verify it.

### DO NOT calculate MIDI ticks incrementally.

Always start from the absolute source sample.

### DO NOT calculate DAC timing independently.

### DO NOT calculate rhythm timing independently.

### DO NOT tie timing to FM instrument mapping.

### DO NOT tie timing to DAC sample deduplication.

### DO NOT quantize by default.

### DO NOT add new external dependencies unless the existing project cannot reasonably perform the required tests without one.

### DO NOT refactor visualization rendering, video rendering, UI, chip emulation, or unrelated playback code.

### DO NOT make broad style cleanups during this task.

### DO NOT silently fall back to fixed 120 BPM in strict mode.

### DO NOT describe BPM-only output with unknown phase as "beat aligned."

### DO NOT produce hundreds of MIDI tempo events because source timer observations jitter slightly.

### DO NOT optimize prematurely.

Normal sorting:

```text
O(n log n)
```

is fine.

---

# 76. Failure messages

Errors need to say what is actually wrong.

Bad:

```text
Unable to export MIDI.
```

Good:

```text
Cannot establish MIDI beat phase: driver timing contains BPM information but no usable beat anchors. Provide an explicit beat offset or disable strict timing.
```

Bad:

```text
Invalid timing.
```

Good:

```text
Conflicting beat anchors at sample 184320: beat positions 31 and 32 were reported for the same source sample.
```

Warnings should likewise differentiate:

```text
tempo known
phase unknown
meter unknown
```

---

# 77. Definition of Done

The work is complete only when all of the following are true.

## Architecture

* Every MIDI event uses the existing `MusicalTimeMap`.
* No independent DAC/rhythm sample-to-tick conversion remains.
* BPM inference is outside `MusicalMidiExporter`.
* MIDI serialization contains no source timing inference.

## Timing

* Constant tempo maps correctly.
* Beat phase maps correctly.
* Pickup notes survive.
* Tempo changes remain continuous.
* Long files have no cumulative drift.
* Beat anchors are within one MIDI tick of expected musical positions.

## Musical correctness

* Unknown meter is not fabricated.
* Known meter/downbeat is preserved.
* Notes crossing tempo changes remain correct.
* Pitch changes cross tempo changes correctly.
* Retriggers have deterministic Off→On ordering.

## MIDI correctness

* SMF Format 1.
* Configured PPQ.
* Correct Set Tempo values.
* Valid VLQs.
* No negative delta times.
* EOT on every track.
* Same input produces byte-identical MIDI.

## Driver correctness

* BeatIndex semantics are documented and protected by tests.
* Loop behaviour is handled intentionally.
* ValidatedBpm activation semantics are documented and protected by tests.

## DAC/rhythm

* DAC and rhythm triggers share the same timing grid as melodic voices.
* Changing DAC sample-to-note assignment does not alter timing.

## User-facing behaviour

* `auto` selects the strongest available evidence.
* `driver` fails when authoritative driver timing is unavailable.
* `fixed` requires explicit BPM.
* strict mode rejects unresolved alignment.
* fallback timing is visibly reported.
* timing diagnostics explain source, phase, fit quality, and warnings.

---

# 78. Expected end-state architecture

The implementation should end up conceptually this simple:

```text
             VisualizationTimeline
                     |
          +----------+----------+
          |                     |
       Beats[]              Timing[]
          |                     |
          +----------+----------+
                     |
            MusicalTimeMapBuilder
                     |
              MusicalTimeMap
          sample <-> quarter time
                     |
          +----------+-----------+
          |          |           |
        notes      rhythm        DAC
          |          |           |
          +----------+-----------+
                     |
            MusicalMidiExporter
                     |
       absolute MIDI tick events
                     |
              MidiFileWriter
                     |
                  .mid
```

There is exactly one source-sample → musical-time authority:

```text
MusicalTimeMap
```

There is exactly one global MIDI origin translation.

Tempo, phase, and meter remain separate concepts.

That is the architectural constraint that should prevent this implementation from drifting into several subtly incompatible MIDI timing paths.
 
---

# Functional Requirements

| ID | Requirement |
|---|---|
| FR-001 | Exported Standard MIDI Files MUST preserve source timeline timing through the existing `MusicalTimeMap`, including beat phase, pickup notes, tempo changes, melodic notes, pitch bends, rhythm events, and YM2612 DAC events without cumulative drift. |
| FR-002 | The implementation MUST audit every timing and beat producer, document the semantics and sample-clock relationship of its events, and use one explicit producer-boundary normalization adapter when a producer clock differs from the final playback/output sample clock; ambiguous clocks MUST be rejected. |
| FR-003 | Timing-source precedence, BPM, phase, meter/downbeat, tempo-segment continuity, MIDI origin shifting, event ordering, retriggers, zero-tick notes, DAC/rhythm paths, CLI fallback behavior, and diagnostics MUST follow the explicit rules and failure behavior defined in this specification. |
| FR-004 | MIDI output MUST be deterministic and valid Standard MIDI Format 1 with configured PPQ, correct tempo events, valid VLQs, non-negative delta times, and an end-of-track event on every track. |
| FR-005 | The implementation MUST preserve unknown meter/downbeat and unresolved alignment as unknown, MUST not quantize by default, and MUST report or reject fallback timing according to the selected `auto`, `driver`, `fixed`, and strict-mode behavior. |
