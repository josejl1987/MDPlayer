# MDPlayer.OpnaNative tests

This directory holds the native test suite for the copied Furnace-compatible
YM2608-LLE path. They are built with CTest via the project `CMakeLists.txt`:

```
ctest --test-dir native/MDPlayer.OpnaNative/build --output-on-failure
```

## `furnace_reference_probe.c` — Stage-1 acceptance gate

The **first** audio gate required by the backend directive. It asserts that the
copied *native probe* — Furnace core snapshot + copied reset + copied bus
scheduler + copied serial decoder — replays a bounded **real** FM trace prefix
and produces nonzero stereo PCM.

Provenance of the trace: a bounded prefix (writes 0..136, 137 writes) of
`XA2047.events.jsonl` at the repository root. That file is a real capture of
YM2608 `opna` bus writes (`{"ev":"opna","port":…,"address":…,"data":…}`)
recorded from actual FMP playback. The prefix covers chip-setup registers
(6-channel mode, LFO, PCM/RSS volumes, prescaler, timer/stepping writes) and a
complete FM channel-0 key-on (operator envelope/TL, algorithm/FB `0xB0`,
frequency `0xA4`/`0xA0`, and the `0x28` key-on). Port-1 register writes are
stored with the 0x100 bank bit, i.e. internal address = `0x100 | reg`.

Assertions:

1. the embedded trace table is intact (`TRACE_LEN == 137` and its FNV-1a
   checksum `192ddf6d`, sha256 `b45cad85...e1443e`, matches the derivation),
2. left channel produces audio (nonzero samples),
3. right channel produces audio (nonzero samples),
4. stereo separation exists (L != R in some frames),
5. the write queue fully drains (the bus scheduler consumed the whole trace),
6. determinism: a fresh render of the same input is byte-identical.

`tests/trace.inc` holds the embedded table as `{{addr,val},...}` and
`tests/trace.py` regenerates it from `XA2047.events.jsonl` and prints the
checksums the probe pins. Re-run `python3 tests/trace.py` after any source-trace
change; if it changes `trace.inc`, update the `kTraceFnv` constant in
`furnace_reference_probe.c` accordingly.

### Stereo-pan note

The real trace stores FM channel 0 panned **left-only** (`0xB4 <- 0x80`) for
this note, so the right channel would only carry the SSG mono mix (silent
here). To exercise both serial-channel capture paths (`o_sh1` and `o_sh2`) the
probe additionally writes `0xB4 <- 0xC0` — the same center/both value the trace
itself emits a few writes earlier (index 94) — before rendering. Every byte used
in the probe is still taken verbatim from the real trace.
