# MDPlayer benchmark harness

Use `--midi-fixture [path]` to run a tracked `.vgz` or `.ovi` source through
`TimelineCaptureService -> MidiTranscriber -> MidiFileWriter`. With no path, the
harness selects the first existing repository fixture from its documented
deterministic list. `--ppq` preserves the requested export resolution; `--bpm`
is rejected because the raw transport is fixed at 120 BPM. The command emits
JSON and a human-readable summary containing input, phase, semantic, output,
configuration, and environment receipts.

The harness does not invent a baseline: until a reproducible frozen baseline is
provided, comparison is `baseline-unavailable` and no speedup claim is made.

`--corpus-receipts [fixture]` runs the eight-song raw-fidelity corpus through
the same fresh-capture -> transcriber pipeline and reports decode-only fidelity
maxima (source notes, native-rhythm hits, same-tick attack collisions, one-tick
notes) plus the raw transport invariants (single 500000us Set Tempo, no time
signatures/markers). `--corpus-receipts --midi-scale N` times the transcriber
over N and 2N generated source events.

Standalone Phase 2 modes:

* `--perf-render` renders reusable sequential frames into a caller-owned buffer;
  no FFmpeg process is started and pixels are discarded after each frame.
* `--perf-encode` sends reusable representative RGBA frames through one FFmpeg
  process with minimal visualization work.
* `--perf-video <input> [render options]` runs the complete capture, render,
  audio, and encode path.
* `--perf-scope <input> [render options]` runs the full pipeline twice on the
  same fixture — `--scope-fps 30` vs 1:1 (`--scope-fps 60`) — and emits a JSON
  comparison of `scopeFrameReadSeconds`, `corrscopeWaitSeconds`,
  `overlayCpuSeconds`, wall time, starvation and encoder/renderer idle deltas
  (waveform cadence plan §6).

`--perf-render` accepts `--frames`, `--width`, `--height`, `--scope-fps`
(scope-cadence reuse emulation; default 60 = 1:1) and `--scope-opacity`
(alpha-blend path vs raw-copy fast path); `--perf-encode`
accepts the same options. The full render JSON includes queue wait/backpressure,
scope-frame read time, renderer copy counts, dirty/full redraw counts, and
avoided-pixel estimates. Scope-frame reads are performed by the producer so
they can overlap overlay drawing and encoder writes. `renderer idle` is the
consumer wait for a ready frame, and `encoder blocked` is the measured raw-frame
write wait; both are pipeline proxies, not separate thread CPU samples.
