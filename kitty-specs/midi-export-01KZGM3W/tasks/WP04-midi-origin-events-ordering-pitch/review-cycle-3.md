---
affected_files: []
cycle_number: 3
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T17:32:30Z'
reviewer_agent: user
wp_id: WP04
---

P2: Exclude non-emitted markers (and FirstDownbeatQuarter) from the origin calculation when EmitMarkers is false. ComputeOriginOffset unconditionally includes LoopMarker.SamplePosition (and FirstDownbeatQuarter) even when EmitMarkers=false, while BuildConductor emits those events only under that option. A marker before the map start therefore adds an extra shift to every emitted event; the first Set Tempo can land after tick 0, leaving leading ticks at the DAW's default tempo. Fix: only include LoopMarker/FirstDownbeatQuarter minimum elements in the origin computation when the corresponding events will actually be emitted (i.e. when EmitMarkers is true). Add a regression: EmitMarkers=false with a marker before the first note -> first Set Tempo / first emitted event maps to tick 0 (no phantom marker shift).