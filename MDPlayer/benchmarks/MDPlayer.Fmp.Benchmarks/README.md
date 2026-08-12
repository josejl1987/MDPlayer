# MDPlayer benchmark harness

Use `--midi-fixture [path]` to run a tracked `.vgz` or `.ovi` source through
`TimelineCaptureService -> SourceTimeline -> MusicalMidiExporter -> MidiFileWriter`.
With no path, the harness selects the first existing repository fixture from its
documented deterministic list. `--ppq` and `--bpm` preserve requested export
settings. The command emits JSON and a human-readable summary containing input,
phase, semantic, output, configuration, and environment receipts.

The harness does not invent a baseline: until a reproducible frozen baseline is
provided, comparison is `baseline-unavailable` and no speedup claim is made.

Standalone Phase 2 modes:

* `--perf-midi [path]` runs the normal MIDI conversion and emits nested MIDI
  stage timings plus source-visit, suppression, sort, allocation, RSS, tempo
  score, and pruned-onset counts.
* `--perf-render` renders reusable sequential frames into a caller-owned buffer;
  no FFmpeg process is started and pixels are discarded after each frame.
* `--perf-encode` sends reusable representative RGBA frames through one FFmpeg
  process with minimal visualization work.
* `--perf-video <input> [render options]` runs the complete capture, render,
  audio, and encode path.

`--perf-render` accepts `--frames`, `--width`, and `--height`; `--perf-encode`
accepts the same options. The full render JSON includes queue wait/backpressure,
scope-frame read time, renderer copy counts, dirty/full redraw counts, and
avoided-pixel estimates. Scope-frame reads are performed by the producer so
they can overlap overlay drawing and encoder writes. `renderer idle` is the
consumer wait for a ready frame, and `encoder blocked` is the measured raw-frame
write wait; both are pipeline proxies, not separate thread CPU samples.
