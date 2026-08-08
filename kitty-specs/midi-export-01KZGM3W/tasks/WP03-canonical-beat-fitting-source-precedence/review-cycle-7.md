---
affected_files: []
cycle_number: 7
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T16:48:30Z'
reviewer_agent: user
wp_id: WP03
---

P1: Use anchor evidence when collapsing an early lone tempo observation (BeatGridFitter.cs:358-359). With valid constant-120 anchors at samples 0, 22050, 44100, ... and exactly ONE validated BPM=150 event at sample 22050 (a first-beat boundary), the head region has only one anchor so RegionRate falls back to 150 while the post-event anchors fit 120. Because the code unconditionally selects segments[0] for any uncorroborated lone observation with multiple segments, it returns a single 150 BPM map, overriding the anchor-derived 120 (violates Section 10).

ROOT CAUSE: the lone-observation resolution depends on segment geometry (segments.Count, segments[0]) instead of on the anchor evidence and the validated distinct-value count. A lone, uncorroborated validated-BPM observation must NEVER override a consistent anchor-derived rate, regardless of its sample position (sample 0, mid-source, or first-beat boundary).

FIX: Make the lone/uncorroborated determination structural, independent of segment geometry: when a consistent, clean set of beat anchors establishes a rate (RegionRate over ALL clean anchors), and the single distinct validated-BPM value conflicts with that anchor rate, the map MUST use the anchor-derived rate for the whole domain. Do not derive the representative rate from segments[0] or from a segment whose head has too few anchors to establish the rate; derive it from all anchor evidence. Cover all placements (sample 0, first-beat boundary, mid-source) with tests.