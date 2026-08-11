# MDPlayer benchmark harness

Use `--midi-fixture [path]` to run a tracked `.vgz` or `.ovi` source through
`TimelineCaptureService -> SourceTimeline -> MusicalMidiExporter -> MidiFileWriter`.
With no path, the harness selects the first existing repository fixture from its
documented deterministic list. `--ppq` and `--bpm` preserve requested export
settings. The command emits JSON and a human-readable summary containing input,
phase, semantic, output, configuration, and environment receipts.

The harness does not invent a baseline: until a reproducible frozen baseline is
provided, comparison is `baseline-unavailable` and no speedup claim is made.
