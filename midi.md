# MDPlayer MIDI transcription

**Status:** Current runtime contract
**Source of truth:** `MDPlayer/src/MDPlayer.Fmp.Core/Timing/Midi/MidiTranscriber.cs` and its round-trip tests.

## Purpose

`MidiTranscriber` serializes a persisted `VisualizationTimeline` into a deterministic
SMF without reconstructing musical timing. It preserves the source wall clock and
uses a fixed transport of 120 BPM.

This document describes the shipped cutover. The runtime does not reconstruct
musical timing; raw transcription is the sole MIDI export path.

## Contract

- PPQ is configurable; the default is 960.
- The only conductor tempo event is `Set Tempo = 500000` microseconds per quarter
  at tick 0.
- Sample-to-tick conversion is direct and absolute:

  ```text
  tick = round((sample - timeline.StartSample) * 2 * PPQ / SampleRate)
  ```

  The factor 2 is the fixed 120 BPM transport.
- Every source `NoteEvent` produces one NoteOn and one NoteOff on its physical voice
  track. No `(track, tick)` deduplication is allowed.
- A non-positive serialized duration is clamped to one tick and is reported by
  `MidiTranscriptionDiagnostics.OneTickNotes`.
- Retriggers at the same tick are ordered `NoteOff → pitch bend → NoteOn`.
  Multiple pitch writes at one tick collapse to the final source state.
- Initial pitch and `PitchChange` values are encoded as a MIDI note plus pitch bend.
  Each physical voice receives its required bend range.
- `timeline.Rhythm` is serialized on MIDI channel 10 (zero-based channel 9), with
  semantic GM mapping where a rhythm role exists and note 60 otherwise. Every
  rhythm hit remains one attack.
- A `NoteEvent` from an FM voice is **not** inferred as percussion. It remains a
  melodic physical note. GM percussion conversion requires an explicit upstream
  rhythm classification; guessing from FM voice/register shape is outside this
  transcriber.
- No meter, beat phase, marker, instrument, quantization, tempo inference, or
  musical layout is emitted by this path.
- Serialization is deterministic for identical timeline bytes and options.

## SMF layout

The writer emits Format 1:

```text
Conductor: Set Tempo(0, 500000)
Voice 0:   PortPrefix + physical events
Voice 1:   PortPrefix + physical events
...
Rhythm:    PortPrefix + channel 9 events, when timeline.Rhythm is non-empty
```

Each physical track has a unique `(port, channel)` endpoint. Channel 9 is reserved
for native rhythm; melodic endpoints skip it. Track event ordering is explicit and
stable; the writer must not regroup equal-tick events after the transcriber has
established the retrigger barrier.

## Validation oracle

`MDPlayer/benchmarks/MDPlayer.Fmp.Benchmarks/RawMidiCorpusReporter.cs` is the raw
corpus gate. It must decode the generated SMF independently and compare every
`VisualizationTimeline.NoteEvent` with its decoded attack/release:

- exact start tick;
- exact end tick, including one-tick clamping;
- emitted base note;
- effective pitch at attack and release, including pitch bend and RPN range;
- physical track, port, and channel;
- source/output note counts and native rhythm attacks;
- fixed transport and deterministic bytes.

Counts alone are insufficient evidence. A receipt that does not pass the
per-note comparison is not a demolition gate.

## Reference tooling

`ReferenceMidiAnalyzer` is an independent SMF analyzer. Its state key is the actual
`(PortPrefixEvent port, channel)` endpoint. Tracks without a port prefix use MIDI
port 0; a track index is never treated as a port. Event flattening is stable by
absolute tick, track index, and original intra-track order. Tempo integration uses
the tempo active before the segment being integrated. The MIDI default pitch-bend
range is ±2 semitones until an RPN range is observed.

`ReferenceMidiComparator` maps tracks by explicit map, then same name/index rules;
without a map it does not use `*ALL*`. Aggregated `*ALL*` matching is opt-in. Attack
errors are converted to milliseconds exactly once.

## Migration status

The cutover is complete. Production callers, tests, and corpus tooling use
`MidiTranscriber`, `MidiExportService`, and the independent raw MIDI oracle.
