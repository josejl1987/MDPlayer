# MDPlayer.OpnaNative

A native YM2608 (OPNA) low-level-emulation backend for MDPlayer, built by
**porting Furnace Tracker's working YM2608-LLE integration** rather than
independently reconstructing the chip protocol.

The on-chip core is the **YM2608-LLE** transistor-level emulator, vendored
byte-identical from the exact Furnace commit the integration was adapted
from. See `UPSTREAM.md` for the pin, file hashes and GPL attribution.

## Layout

```
upstream/furnace-ym2608-lle/   YM2608-LLE core snapshot (byte-identical, GPL)
src/                           adapted integration layer (MDPlayer-authored)
  mdplayer_opna.c              top-level driver (reset / write / render)
  mdplayer_opna_core.c         copied 576/576/576 reset sequence + pin state
  mdplayer_opna_bus.c          copied pin-level write state machine
  mdplayer_opna_serial.c       copied serial o_opo decoder (o_s/o_sh1/o_sh2)
  mdplayer_opna_adpcm.c        copied CAS/RAS multiplexed ADPCM-B memory bus
  mdplayer_opna_mix.c          Furnace SSG o_analog mix (scale 42) + frame out
  mdplayer_opna_internal.h     shared internal state / API
tests/                         CTest suite (Stage-1 probe, and more to come)
```

Only the chip-level machinery is ported: reset, clocking, bus transactions,
status reads/IRQ, serial PCM decoding, SSG analogue output and ADPCM external
memory. Furnace tracker code (channels, instruments, macros, note/frequency,
oscilloscope buffers, ymfm/Nuked combo paths, command dispatch, song/UI state)
is **not**.

## Build and test

```
cmake -S native/MDPlayer.OpnaNative -B native/MDPlayer.OpnaNative/build \
      -DCMAKE_BUILD_TYPE=Release
cmake --build native/MDPlayer.OpnaNative/build --config Release
ctest --test-dir native/MDPlayer.OpnaNative/build --output-on-failure
```

Produces `libmdplayer_opna.so` and copies it to
`runtimes/linux-x64/native/`. Optional:
`-DMDPLAYER_OPNA_SANITIZERS=ON` adds AddressSanitizer
(`..._UBSAN=ON` additionally enables UBSan — note the vendored core performs
intentional signed-int arithmetic that UBSan flags).

## Status (Stage 1 — native probe gate)

The first acceptance target **passes**: `mdplayer_opna_probe` replays a bounded
real FM trace prefix through the copied reset/bus/serial path, produces nonzero
stereo PCM, drains its write queue, and is byte-deterministic. Subsequent Stage-1
gates (left/right pan, SSG, rhythm, ADPCM-B, timer/IRQ) and the finite-state
fixture from `tests/` are the next steps per the backend directive, followed by
reconnecting the existing resampler and the managed ABI.
