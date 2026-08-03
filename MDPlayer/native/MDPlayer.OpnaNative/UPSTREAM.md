# MDPlayer.OpnaNative — upstream vendoring

This directory vendors the **YM2608-LLE** YM2608B(OPNA) transistor-level core
unchanged from the Furnace Tracker pin used as the integration reference,
plus the MDPlayer-authored integration layer built on top of it (`src/`,
`tests/`). Everything under `upstream/furnace-ym2608-lle/` is imported
byte-identical from the pinned Furnace commit and **must never be hand-edited**.

A note on the core's origin: Furnace vendors YM2608-LLE under
`extern/YM2608-LLE`. libOPNMIDI ships the same project (by nukeykt) under a
slightly different source arrangement (`fmopna_impl_c.h`) and an older snapshot;
the directive for this backend pins Furnace's snapshot, because Furnace's
`fmopna_impl.h` blob differs from libOPNMIDI's and Furnace's integration relies on
fields (`o_analog`, `last_rss_sample`, etc.) that are absent from the searched
libOPNMIDI snapshot. Furnace is therefore the canonical reference.

## Repository and pin

- Repository URL: https://github.com/tildearrow/furnace
- Branch policy: pinned to one commit; never a floating branch.
- Pinned commit SHA: **`3bdfc824fb7d2e813852f6fcfa482d8ea999588a`**
  (Furnace `master`). This is the exact commit from which both the core files
  below and the integration logic in `src/engine/platform/ym2608.cpp` /
  `ym2608.h` were extracted, so the adapter and the core stay in lock-step.
- Licence: **GPL-2.0-or-later**. YM2608-LLE core: © 2023-2024 nukeykt (GPL v2
  or later header in `fmopna_impl.h`). Furnace integration: © 2021-2026
  tildearrow and contributors (GPL v2 or later). The full GPL v2 text is kept
  alongside the vendored files as `LICENSE`.

## Imported files (byte-identical)

All paths relative to `upstream/furnace-ym2608-lle/`. Soleced from the Furnace
repo at the pinned commit as `extern/YM2608-LLE/*`.

| File | Role | SHA-256 (pinned Furnace tree) |
| --- | --- | --- |
| `fmopna_2608.c` | Build shim: defines `FMOPNA_YM2608` and `#include`s `fmopna_impl.c`. **Compiled.** | `cd09b74f0e6d291f9c7e50add7795d7eb182a9492e7f8f51300d89ad540f8aa5` |
| `fmopna_2608.h` | Build shim: defines `FMOPNA_YM2608`, includes `fmopna_impl.h`, then undefines it so the public `fmopna_t`/`FMOPNA_Clock` types are exposed for the 2608 build. | `4aea9bd76af379e89bb2e959ae95ec7b283815e958b4bb38d3f05e3020f20a8e` |
| `fmopna_impl.c` | The full YM2608/2610/2612 transistor-level emulator implementation. **Compiled via `fmopna_2608.c`.** | `45b456264f4aceb545d5c9651bae261c010b12a1589f3d1091b27cfb173bef32` |
| `fmopna_impl.h` | Core structs (`fmopna_t`, `fmopna_input_t`) and `FMOPNA_Clock()` declaration. | `c20375ceea1c1d918edf38137cfa84f55239668fc051079c61523e87659635b1` |
| `fmopna_rom.h` | On-chip SSG (AY8910) constant tables included by `fmopna_impl.c`. | `3bf18decf58ad0e1b903de5bda870d1cfd5c73bece4d5899fd7015020878d863` |
| `LICENSE` | Full GPL v2 text (the core and Furnace integration are GPL-2.0-or-later; GPL v2 is the kernel of that grant). | — |

Furnace deliberately ships no `nopna.c` for this backend; the directive requires
that any `nopna.c` experiment **not** be used as the production foundation, and
none is vendored here.

## Provenance record (spec §6)

- Vendored files: copied **byte-identically** (verified by `sha256sum` against the
  checked-out pinned tree) — not adapted.
- Date copied: 2026 (see commit history for the exact date; the vendored blobs
  below are the authority).
- Repository URL: `https://github.com/tildearrow/furnace`
- Furnace commit: `3bdfc824fb7d2e813852f6fcfa482d8ea999588a`

| Vendored path (relative to `upstream/furnace-ym2608-lle/`) | Furnace source path | Git blob SHA (`git rev-parse <commit>:<path>`) | SHA-256 |
| --- | --- | --- | --- |
| `fmopna_2608.c` | `extern/YM2608-LLE/fmopna_2608.c` | `157859b7c3ffc7bef9ce68ce16030a475f60ea45` | `cd09b74f0e6d291f9c7e50add7795d7eb182a9492e7f8f51300d89ad540f8aa5` |
| `fmopna_2608.h` | `extern/YM2608-LLE/fmopna_2608.h` | `7021f14b60861ed3e8f56b75fe1b6c609fa69749` | `4aea9bd76af379e89bb2e959ae95ec7b283815e958b4bb38d3f05e3020f20a8e` |
| `fmopna_impl.c` | `extern/YM2608-LLE/fmopna_impl.c` | `23983069dd00d2ca5c83ee81720001634fc60c65` | `45b456264f4aceb545d5c9651bae261c010b12a1589f3d1091b27cfb173bef32` |
| `fmopna_impl.h` | `extern/YM2608-LLE/fmopna_impl.h` | `12cbde9dbcd9bc6abe609968f3e57c844b4dff35` | `c20375ceea1c1d918edf38137cfa84f55239668fc051079c61523e87659635b1` |
| `fmopna_rom.h` | `extern/YM2608-LLE/fmopna_rom.h` | `bfb9daaf02d3affc745f591d692e7f74ef8ca4e8` | `3bf18decf58ad0e1b903de5bda870d1cfd5c73bece4d5899fd7015020878d863` |

License: the vendored core is **GPL-2.0-or-later** (© 2023-2024 nukeykt; GPL v2
or later header in `fmopna_impl.h`). The full GPL v2 text is in
`LICENSES/GPL-2.0-or-later.txt`.

### Integration reference files (not vendored; adapted under `src/`)

The adapted integration code mirrors the corresponding ranges of these Furnace
sources, which are **not** copied into this directory (only the logic is adapted,
with the `Adapted from Furnace` header on each file):

| Furnace source path | Role in the adapter |
| --- | --- |
| `src/engine/platform/ym2608.cpp` | `DivPlatformYM2608::reset`, `acquire_lle` (bus scheduler, serial decoder, ADPCM bus, SSG mix) |
| `src/engine/platform/ym2608.h` | `QueuedWrite`-shaped write scheduling state used by `acquire_lle` |


## Build

```
cmake -S native/MDPlayer.OpnaNative -B native/MDPlayer.OpnaNative/build \
      -DCMAKE_BUILD_TYPE=Release
cmake --build native/MDPlayer.OpnaNative/build --config Release
ctest --test-dir native/MDPlayer.OpnaNative/build --output-on-failure
```

`fmopna_2608.c` is compiled directly from the vendored tree (the core needs no
build-time patching here). Produces `libmdplayer_opna.so` on Linux. The
post-build step copies it to `runtimes/linux-x64/native/libmdplayer_opna.so`
(the managed wrapper probes it there; on future Windows work this becomes
`runtimes/win-x64/native/mdplayer_opna.dll`).

## Adapted integration layer (MDPlayer-authored)

The C sources under `src/` are adapted from Furnace's YM2608 LLE integration in
`src/engine/platform/ym2608.cpp` at the same pinned commit. Per the backend
directive, only the chip-level machinery is adapted — reset sequencing, the
pin-level write state machine scheduled on valid prescaler phase, the serial
`o_opo` decoder driven by falling `o_s`/`o_sh1`/`o_sh2`, the CAS/RAS multiplexed
ADPCM-B external-memory adapter, and the `o_analog` SSG mix into both digital
outputs. Furnace tracker machinery (channels, instruments, macros, note
calculation, oscilloscope buffers, ymfm/Nuked combo paths, command dispatch,
song state, register-pool UI) is **not** ported.

Every adapted file carries a header of this shape, combining Furnace's GPL
copyright with an adaptation notice:

```
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
```

## Update procedure

1. `git clone https://github.com/tildearrow/furnace.git /tmp/furnace`
2. `cd /tmp/furnace && git checkout 3bdfc824fb7d2e813852f6fcfa482d8ea999588a`.
3. Copy `extern/YM2608-LLE/{fmopna_2608.c,fmopna_2608.h,fmopna_impl.c,fmopna_impl.h,fmopna_rom.h,LICENSE}` into `upstream/furnace-ym2608-lle/`.
4. Verify byte-identity against the pinned tree (`sha256sum` per file; do not hand-edit).
5. Re-check the adapted `src/*` against the corresponding ranges of the pinned
   `src/engine/platform/ym2608.cpp` (reset, `acquire_lle`, `immWrite`).
6. Re-run the native CTest suite (`ctest --test-dir ...`).
7. Commit the import as a separate commit:
   `chore: vendor YM2608-LLE core @ furnace 3bdfc824`.

Only bump the pin if the integration is deliberately retargeted; the adapter is
written against the exact pin above.
