---
affected_files: []
cycle_number: 2
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T14:34:50Z'
reviewer_agent: user
wp_id: WP01
---

P1: Serialized-timeline (--timeline) path bypasses ProducerClockNormalization. TimelineCaptureService.Capture:29 returns VisualizationJsonWriter.Read directly, so a serialized timeline with a differing/missing sample rate never reaches the normalization boundary (TimelineBuilder.Merge is the only caller). MidiCommand/MidiExportService consume that timeline directly. Requires routing the JSON path through the same normalization boundary with actionable rejection for ambiguous/missing rates.
P2: ProducerClockNormalizationTests BeatIndex test admits no runtime producer and reimplements BuildAnchors; does not lock actual producer BeatIndex unit/loop/jump semantics.
