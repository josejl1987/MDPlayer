---
affected_files: []
cycle_number: 2
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T17:18:46Z'
reviewer_agent: user
wp_id: WP04
---

P2 (fix in WP04): ComputeOriginOffset omits LoopMarker.SamplePosition and PitchChange.SamplePosition, so such an event occurring before the note/rhythm/first-sample minima can map to negative ticks; the writer then clamps negative deltas, violating the all-families nonnegative global origin requirement (spec Section 21). Fix: include LoopMarker.SamplePosition and PitchChange.SamplePosition in the origin-offset minimum computation so the single global offset covers every event family. Add a regression with a marker/pitch earlier than notes verifying all ticks are nonnegative.

P1 (coordinate to WP05, not edit the writer here): MidiEventBase.SourceOrder secondary key was added and populated, but MidiFileWriter (WP05-owned) still sorts only by Tick + MidiEventOrder.Rank, so equal-tick/equal-rank output remains insertion-order dependent (violates determinism Section 34/45). WP05's writer hardening (T033/T034) MUST consume the SourceOrder secondary key for deterministic sorting. Do not edit MidiFileWriter in this WP; note this for the WP05 reviewer.