---
affected_files: []
cycle_number: 3
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T16:02:43Z'
reviewer_agent: user
wp_id: WP02
---

P2: MusicalTimeMapInvariantsTests.cs lines ~161-173 continuity/150-BPM assertions are tautological. Both assert map.SampleToQuarterPosition(48000); at that boundary LocateSegment selects segment B, so they only read B's QuarterPositionAtStart (2.0) and never compute segment A's value at the shared boundary, and never assert the segment's BeatsPerMinute. A regression to the old inverted BPM helper (or an incorrect B slope) would still pass. Fix: assert segment A.QuarterPositionAt(48000) equals B's origin (genuine continuity check), and assert an interior B sample (e.g. 5000+ samples into B) maps per 150 BPM (samplesPerQuarter = Sr*60/150), or assert BeatsPerMinute == 150.0.