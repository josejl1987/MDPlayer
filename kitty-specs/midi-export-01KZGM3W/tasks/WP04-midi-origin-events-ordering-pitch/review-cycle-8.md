---
affected_files: []
cycle_number: 8
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T18:04:44Z'
reviewer_agent: user
wp_id: WP04
---

P2 (definitive): Gating the rhythm origin scan on voice emission. ComputeOriginOffset's emitted-family filtering covers notes but NOT rhythm: it unconditionally min()s every RhythmEvent.SamplePosition, even for a rhythm channel whose VoiceExportOverride.Include=false (BuildTracks drops such channels, so they emit no track/event). An early excluded RhythmEvent therefore shifts the global origin and delays conductor/remaining notes, violating the 'origin derives only from emitted families' rule.

Fix: gate the rhythm origin scan on _options.OverrideFor(rhythm.ChannelId).Include — skip rhythm events from excluded channels, matching BuildTracks emission. This is the final remaining emission-predicate gap; after this, ComputeOriginOffset's emission predicates (notes: positive-duration + voice-include + EmitPitchBend gate for pitch; markers: EmitMarkers; rhythm: voice-include) must exactly match what BuildTracks/Export actually emit. Add a regression: a rhythm channel with VoiceExportOverride.Include=false whose event is earlier than all emitted events -> the origin is not shifted (first emitted event/tempo at tick 0).