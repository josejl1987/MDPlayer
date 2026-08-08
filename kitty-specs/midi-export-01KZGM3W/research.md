# Phase 0 Research — MIDI Export Timing Hardening

## Scope and source baseline

This research is grounded in the authoritative `spec.md` (FR-001..FR-005) and the current `feature/linux-fmp-renderer` source layout. The existing implementation already has one timing subsystem under `MDPlayer/src/MDPlayer.Fmp.Core/Timing/`: `MusicalTimeMap`, `MusicalTimeMapBuilder`, `MusicalTimeMapOptions`, `BeatGridFitter`, `TimingDiagnostics`, `TempoSegment`, `TimingSource`, and `MusicalTimingException`. MIDI projection and serialization are under `Timing/Midi/`; timeline contracts and capture decoders are under `Visualization/`.

The planning assignment is not an implementation or validation run. No build, formatter, linter, or project-wide test command was run.

## Producer audit

The required literal audit of `TimelineBuilder.AddTiming(...)` and `TimelineBuilder.AddBeat(...)` found:

| Path | Actual role | Current evidence | Planning consequence |
|---|---|---|---|
| `Visualization/TimelineBuilder.cs` | Mutable ingestion boundary | `AddTiming(DriverTimingEvent)` and `AddBeat(BeatEvent)` null-check and append; `Build` orders by `SamplePosition`; `Merge` forwards existing timeline arrays | This is the single producer-boundary location at which a differing sample clock can be normalized, but normalization must be explicit and tied to a known producer. |
| `Visualization/Ym2608TimelineDecoder.cs` | Runtime `DriverTimingEvent` producer | `ApplyYm2608(..., long samplePosition)` appends `new DriverTimingEvent(samplePosition, value, ComputeTimerBpm(value))` for YM2608 port 0 register `0x26` (Timer B) writes | Audit actual timer semantics and activation timing before treating `ValidatedBpm` as an effective tempo transition. Do not reverse-engineer again in the MIDI writer. |
| `Visualization/ChipTimelineDecoderRegistry.cs` / `TimelineDecoderEventSink` | Capture routing | `OnChipWrite` validates and forwards `TimedChipWrite` to the selected decoder; `Complete` merges decoder timelines and builds the final `VisualizationTimeline` | The sink and decoder handoff are the runtime sample-clock boundary to verify. |
| `Visualization/VisualizationTimeline.cs` | Event data boundary | `DriverTimingEvent.SamplePosition`, `BeatEvent.SamplePosition`, and note/rhythm/pitch/DAC event records use source sample positions; `VisualizationTimeline.SampleRate` accompanies the arrays | Record names do not establish clock equivalence. The implementation must prove all event positions use the final playback/output sample clock or reject/normalize at the producer boundary. |
| `Visualization/VisualizationJsonWriter.cs` and `MDPlayer.Fmp.Cli/TimelineCaptureService.cs` | External timeline input | `--timeline` loads a serialized `VisualizationTimeline`; otherwise capture uses the event sink and selected backend | Beat arrays can enter from a serialized timeline. The audit must define the JSON producer's clock metadata/assumption or reject an ambiguous timeline clock before map construction. |
| `tests/MDPlayer.Fmp.Tests/...` | Synthetic producers | Existing tests construct `BeatEvent` and `DriverTimingEvent` directly, including `BeatIndex` values and sample positions | These tests currently encode assumptions and must be changed/extended only after the actual producer semantics are documented. |

A repository-wide search found no runtime `new BeatEvent(...)` constructor under `MDPlayer/src`; the only non-contract `AddBeat` calls are the builder's own `Merge` path and direct test setup. Therefore the source tree does not currently prove a driver decoder that emits beat anchors. The plan must not invent one. It must treat timeline-provided beats as an input contract and audit their producer metadata/clock at the timeline boundary.

The runtime timing producer's timer-B code currently documents its own assumptions: one Timer-B interrupt is treated as one quarter note, and `ComputeTimerBpm` derives a validated BPM from the YM2608 master clock. That is a producer-specific claim, not proof that every `BeatIndex` is a quarter or that the timer event's sample is the exact activation point. Those semantics require regression tests before fitting changes.

## Sample-clock findings and explicit decision

`TimelineBuilder` is constructed with a `sampleRate`, and `TimelineCaptureService` chooses the capture timeline rate from the backend's native rate when available, otherwise the configured output rate. Decoder callbacks receive `samplePosition` values and the final timeline carries `StartSample`, `EndSample`, and `SampleRate`. The source does not expose enough metadata to prove that every producer clock is identical in all paths; in particular, serialized timeline input and backend/native-rate conversion need an explicit contract.

Decision mandated by the user/spec: use one explicit normalization adapter at the producer boundary when a producer clock differs from the final playback/output sample clock, and reject ambiguous clocks. The adapter must be applied before `MusicalTimeMapBuilder` sees anchors/events; no `sampleRate`/BPM compensation may be added to `MidiFileWriter`, `MusicalMidiExporter`, DAC conversion, or rhythm conversion. The adapter must preserve source ordering and use a documented conversion policy; if source rate, destination rate, or relationship cannot be established, fail with an actionable diagnostic.

## Existing timing flow

```text
raw/backend or JSON timeline
        |
        v
TimelineBuilder / VisualizationTimeline
        |
        +--> Beats[] and Timing[] ----+
        |                              |
        |                              v
        |                     MusicalTimeMapBuilder
        |                              |
        |                              v
        |                     MusicalTimeMap + diagnostics
        |                              |
        +--> Notes/Rhythm/DAC/etc. --> MusicalMidiExporter
                                       |
                                       v
                              absolute MIDI events
                                       |
                                       v
                                MidiFileWriter
                                       |
                                       v
                                SMF Format 1
```

`MusicalTimeMap` already documents itself as the canonical sample-to-quarter/tick authority and validates segment continuity. `MusicalTimeMapBuilder` already has options for fixed BPM, quarter/sample phase, meter, downbeat, source selection, strict timing, and tempo-change detection. `BeatGridFitter` already performs robust slope/intercept fitting and exact duplicate deduplication, but the spec requires the remaining conflict, outlier, loop, diagnostic, and strict semantics to be verified against actual producer behavior.

`MusicalMidiExporter` currently maps note/rhythm/conductor samples through the map, but the implementation must be checked for a single global origin shift, absolute note endpoint mapping, no default quantization, pitch-change mapping, DAC/rhythm routing, and explicit same-tick priority. `Visualization/Dac/DacMidiExporter.cs` already accepts a `MusicalTimeMap` and calls `SampleToTick`, but it has its own export surface/default PPQ; it must not become a second timing authority or bypass the shared origin policy. `MidiFileWriter` currently receives absolute event ticks and serializes tracks, so source timing inference belongs upstream.

## Required semantic questions to lock with tests

1. What unit does `BeatEvent.BeatIndex` represent in each actual producer: quarter, driver beat, timer tick, or subdivision? Can it be fractional, jump, reset at loops, or start nonzero? The current builder has `QuartersPerBeat`, but its default is an assumption until producer semantics are proven.
2. Does `DriverTimingEvent.ValidatedBpm` include driver tempo, speed, timer/prescaler settings, playback multipliers, and song-specific rules? At what exact sample does a new value become effective? The YM2608 code currently records register-write sample positions, which may or may not equal the next timing interval's activation point.
3. Are note starts/ends, pitch changes, rhythm, DAC triggers, beat anchors, and timing events all on the final playback/output sample clock for each capture and JSON path? If not, which producer has the authoritative source rate and where is the one adapter applied?
4. Does the driver continue `BeatIndex` monotonically across loops or reset it? Only a documented reset may be normalized into a continuous beat index.
5. What meter/downbeat data is authoritative? If absent, preserve unknown and omit Time Signature rather than infer 4/4 or first-beat downbeat.

## Decisions and non-decisions

- **Keep** the existing timing/map/export/writer classes; no parallel timing abstraction.
- **Normalize once** at a producer boundary only when clocks differ and both clocks are explicit; reject ambiguous clocks.
- **Keep tempo, beat phase, and meter/downbeat separate.** BPM alone does not establish alignment.
- **Use absolute sample conversion** for all events and a single global tick-origin shift after musical positions are resolved.
- **Keep default export unquantized** and preserve pickup notes.
- **Do not expand symbolic inference** beyond the existing fallback path.
- **Do not add** audio beat detection, a new CLI framework, a new external dependency, or unrelated UI/rendering/chip-emulation refactors.

## Evidence-to-requirement mapping

- **FR-001**: Existing map/export architecture is present; hardening must cover all event classes and absolute/no-drift behavior.
- **FR-002**: The producer audit above identifies the runtime timer producer, the timeline ingestion/JSON boundary, and the absence of a runtime beat constructor; clock equivalence remains an explicit proof/rejection requirement.
- **FR-003**: Existing options/builder/exporter/writer surfaces match the required timing precedence, phase, segment, origin, ordering, DAC/rhythm, CLI, and diagnostic areas.
- **FR-004**: Existing `MidiFileWriter` is the serialization boundary for Format 1, PPQ, VLQ, chunks, event ordering, deltas, and EOT.
- **FR-005**: Existing options and CLI expose source/BPM/phase/meter/downbeat/strict/report fields; behavior must preserve unknowns and make fallback visible or fail.
