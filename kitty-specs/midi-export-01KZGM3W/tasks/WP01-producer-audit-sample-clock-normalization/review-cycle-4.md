---
affected_files: []
cycle_number: 4
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T15:05:24Z'
reviewer_agent: user
wp_id: WP01
---

P1: Application/GUI serialized-JSON path bypasses normalization. MidiExportService.ExportFromTimelinePath calls VisualizationJsonWriter.Read directly then builds MusicalTimeMap — never reaches TimelineBuilder.Merge/ProducerClockNormalization. Route it through the boundary (or raise actionable MusicalTimingException for ambiguous source rate). Anchor VisualizationJsonWriter.cs:72.
P2: CaptureSerializedTimeline rebuilds via TimelineBuilder.Build which hardcodes StartSample=0; a valid --timeline with startSample>0 silently resets map origin/range while event samples stay absolute. Preserve the serialized StartSample range. Anchor TimelineCaptureService.cs:62.
