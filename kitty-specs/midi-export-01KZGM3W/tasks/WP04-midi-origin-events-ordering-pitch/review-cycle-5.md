---
affected_files: []
cycle_number: 5
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T17:48:38Z'
reviewer_agent: user
wp_id: WP04
---

P2 (final, consolidated): The single global origin must derive ONLY from actually-EMITTED event families. ComputeOriginOffset currently scans all note pitch changes regardless of emission: it includes PitchChange.SamplePosition from notes Export skips (EndSample<=StartSample, VoiceExportOverride-excluded), and scans pitch even when EmitPitchBend=false. A non-emitted early pitch therefore shifts the origin and pushes every emitted event (including the first Set Tempo) off tick 0. This is the same root class as the (already-fixed) non-emitted-marker issue.

Fix: make ComputeOriginOffset derive the origin minimum from the SAME event set that will actually be emitted — respect EmitMarkers (done), skip pitch changes from emitted-but-EmitPitchBend=false, skip notes excluded by EndSample<=StartSample or VoiceExportOverride, and only include a family when it will be serialized. Add regressions: (a) EmitPitchBend=false with an early pitch -> origin not shifted; (b) a skipped (EndSample<=StartSample) note's early pitch -> origin not shifted; (c) EmitMarkers=false already covered.

This is the definitive origin fix; do not introduce further changes beyond emitted-family derivation.