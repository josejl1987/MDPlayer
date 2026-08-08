---
affected_files: []
cycle_number: 4
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T16:37:45Z'
reviewer_agent: user
wp_id: WP03
---

P1: Lone conflicting BPM at sample 0 still overrides anchor rate. In BeatGridFitter.FitValidatedTempoSegments, when anchors cover a constant 120 BPM and ONE validated event at Sample=0 reports 150 BPM, the fit produces only ONE segment (the post-validation value 150 for the whole domain), so the `if (segments.Count > 1)` guard at lines ~343-356 prevents `loneObservation`/`AnchorsCorroborateRate` from running. The returned segment uses 150 BPM for the entire map, overriding the authoritative anchor-derived 120 rate (violates Section 10 anchors-first precedence). Fix: run the lone-conflicting-BPM / AnchorsCorroborateRate check regardless of segment count (including the single-segment case where the lone event's BPM conflicts with the anchor-derived rate), so sample-0 conflicting observations also retain the anchor grid. Add a test: anchors prove 120 BPM, lone validated event at Sample=0 says 150 -> map uses constant 120, not 150.