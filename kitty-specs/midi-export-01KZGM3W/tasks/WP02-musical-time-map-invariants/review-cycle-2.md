---
affected_files: []
cycle_number: 2
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T15:58:03Z'
reviewer_agent: user
wp_id: WP02
---

P2: Inverted BPM helper in MusicalTimeMapInvariantsTests.cs Segment helper. The expression 60_000_000.0*spq/(Sr*60.0) yields 400000 BPM for spq=19200 instead of 150 BPM. Multi-segment tests therefore construct mislabeled TempoSegments and do not exercise the claimed tempo change. Replace with Sr*60.0/spq (i.e. BPM = SampleRate*60/samplesPerQuarter).