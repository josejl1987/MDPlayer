---
affected_files: []
cycle_number: 5
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T19:14:13Z'
reviewer_agent: user
wp_id: WP05
---

P1: Emit the rate-metadata delta exactly once in the DAC track. BuildDacTrack writes an event delta before a rate metadata event, while the changed WriteMetaText (via WriteMeta) writes ANOTHER delta, so any InitialRateHz export is encoded as `delta, 0x00, FF 01 ...` — the second delta byte is consumed where a MIDI status byte is required and the DAC track is malformed. Fix: ensure exactly one delta precedes the meta-text event (remove the redundant one).

P2: Apply MIDI VLQ upper-bound rejection to the DAC writer's newly-serialized tempo deltas. The per-segment tempo loop passes `tempo.Tick - lastTick` into the DAC WriteVlv, which still accepts values >0x0FFFFFFF and emits 5+ byte VLQs. A valid long MusicalTimeMap with a tempo boundary beyond that tick produces invalid SMF. Fix: the DAC WriteVlv must reject values above 0x0FFFFFFF (and negatives) like the core writer, rather than emitting over-long VLQ.