---
affected_files: []
cycle_number: 2
mission_slug: midi-export-01KZGM3W
reproduction_command:
reviewed_at: '2026-08-08T18:47:31Z'
reviewer_agent: user
wp_id: WP05
---

P1: DAC conductor track must serialize EVERY tempo segment. DacMidiExporter.Write passes only _map.Segments[0].MicrosecondsPerQuarter to the DAC writer, whose conductor emits a single FF51 Set Tempo event. For a valid multi-segment MusicalTimeMap, BuildEvents maps triggers using each segment's changing quarter position but the MIDI never changes tempo at later segment boundaries, so DAC playback tempo diverges from the map. Fix: serialize a Set Tempo event for EVERY tempo segment at its correct sample-derived tick (like the core MusicalMidiExporter conductor does).

P1: DAC origin must match the melodic exporter's origin. DacMidiExporter.ComputeOriginOffset only uses _map.FirstSample + meter, but MusicalMidiExporter includes _map.FirstDownbeatQuarter when EmitMarkers (default true). For a map with first downbeat before map start, the same sample maps to DAC tick 0 vs melodic tick 1920+ - a direct §67 shared-origin failure (contradicts MusicalMidiExporterTests Export_MarkersEnabled_DownbeatBeforeMapStart_OriginStillCoversIt). Fix: DacMidiExporter's origin must include the same FirstDownbeatQuarter/bar-ceiling the melodic exporter applies when markers are emitted, so DAC and melodic triggers share the identical global origin. Add a §67 regression asserting melodic + DAC + rhythm at the same sample map to the SAME tick with a first-downbeat-before-map-start and a multi-segment tempo map (asserting the DAC conductor also emits the 2nd Set Tempo).