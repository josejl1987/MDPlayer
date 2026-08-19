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
  Each physical voice receives its required bend range; the corpus oracle checks
  every emitted pitch-state tick, not only attack/release endpoints.
- `SamplePlaybackEvent` is a metadata/view family, not automatically an
  independent musical attack. The raw transcriber currently exports only the
  sample-only YM2612 DAC voice (`ym2612.0.pcm.dac`) as identity-trigger
  NoteOn/NoteOff events. `SampleId`s are sorted and mapped to MIDI bank/note
  identities; `StartSample` and `EndSample` use the same absolute tick
  conversion and one-tick clamp. Null `MidiPitch` never invents tonal pitch.
- OKI/PPZ8/ADPCM sample views remain available for downstream projection but are
  not emitted alongside an authoritative `RhythmEvent` in raw MIDI. A source
  attack must have one owner.
- `timeline.Rhythm` is serialized on MIDI channel 10 (zero-based channel 9), with
  semantic GM mapping where a rhythm role exists and note 60 otherwise. Every
  rhythm hit remains one attack.
- A `NoteEvent` from an FM voice is **not** inferred as percussion. It remains a
  melodic physical note. GM percussion conversion requires an explicit upstream
  rhythm classification; guessing from FM voice/register shape is outside this
  transcriber.
The writer emits Format 1:

```text
Conductor: Set Tempo(0, 500000)
Voice 0:   PortPrefix + physical NoteEvent events
Sample 0:  PortPrefix + bank/note identity triggers
...
Rhythm:    PortPrefix + channel 9 events, when timeline.Rhythm is non-empty
```


Each physical track has a unique `(port, channel)` endpoint. Channel 9 is reserved
for native rhythm; melodic endpoints skip it. Track event ordering is explicit and
stable; the writer must not regroup equal-tick events after the transcriber has
established the retrigger barrier.

## Validation oracle

`MDPlayer/benchmarks/MDPlayer.Fmp.Benchmarks/RawMidiCorpusReporter.cs` is the raw
corpus gate. It independently decodes the generated SMF and compares:

- every `NoteEvent` attack/release, base note, physical endpoint and effective
  pitch;
- every interior `PitchChange` bend tick and effective pitch;
- every `RhythmEvent` start tick, one-tick release and mapped drum identity;
- every `SamplePlaybackEvent` start/end tick, physical endpoint, bank/note
  identity and optional attack pitch;
- the complete source-event universe (`Notes + Rhythm + SamplePlayback`);
- fixed transport and deterministic bytes.

Counts alone are insufficient evidence. A receipt that does not pass the
per-event comparisons is not a demolition gate.

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
