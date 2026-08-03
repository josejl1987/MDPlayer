# MDPlayer.OpnaNative — SpeexDSP vendoring

This directory vendors the **standalone resampler** of **SpeexDSP** under the
MIT-style Xiph BSD licence (see `LICENSE`), pinned to the `SpeexDSP-1.2.1`
release. Only the files required by the fixed-point, `OUTSIDE_SPEEX`,
symbol-prefixed resampler are imported — not the full SpeexDSP repository.

The resampler is used as the fixed-cadence converter for the OPNA native frame
rate (7,987,200/144 Hz) to 44100/48000/96000 Hz, compiled in fixed-point
processing mode at `SPEEX_RESAMPLER_QUALITY_MAX`. It is compiled into the
private static target `mdplayer_opna_speex_resampler` and linked privately into
`libmdplayer_opna`, so no SpeexDSP symbol is exported from the public library.

## Repository and pin

- Repository URL: https://github.com/xiph/speexdsp
- Branch policy: pinned to an annotated release tag; never a floating branch.
- Pinned release: **`SpeexDSP-1.2.1`**
- Pinned commit SHA: **`1b28a0f61bc31162979e1f26f3981fc3637095c8`**
  (tag `SpeexDSP-1.2.1^{}`, commit object).
- Licence: **BSD-3-Clause (Xiph.org)**. Upstream keeps the licence text as
  `COPYING`; it is vendored here as `LICENSE` (SHA-256 recorded below).

## Imported files (byte-identical)

All files are copied verbatim from the pinned commit and **must never be
hand-edited**. `resample.c` is the sole compiled translation unit; the headers
are included as-is with include path resolution set in CMake.

| File | Role | SHA-256 (pinned tree) |
| --- | --- | --- |
| `resample.c` | The SpeexDSP resampler implementation (compiled; fixed-point via `FIXED_POINT`). | `c28fabfc082d0e7634eb678e2e3a2bc091148bbb8324e3d06669c9a9faf6793a` |
| `speex_resampler.h` | Public resampler API (from `include/speex/speex_resampler.h`); under `OUTSIDE_SPEEX` it renames every exported symbol via `RANDOM_PREFIX`. | `7e439ec0dd30c32216b3ced17135f8992e5aaf53389d3f5996a7d900c453e65f` |
| `arch.h` | Architecture dispatch; selects `fixed_generic.h` in fixed-point mode. | `102f6a14a95f8ae0bcfc69270f3a9e3fbba08b63bba688ca524f71e3faa48c23` |
| `fixed_generic.h` | Generic fixed-point helpers used by `arch.h`. | `18e7c5e6bc4b137fc3aefc05d75d18cd4d50a2841fd8224cefed1e1340170b05` |
| `LICENSE` | Upstream licence text (upstream file: `COPYING`). | `2654a4264b2bfe298dedc508748d140111840c315cc8eb646a3a68c13fa75b01` |

The `#include "config.h"` guarded by `HAVE_CONFIG_H` is not emitted (no
`config.h` is generated), so no configuration header is needed. With
`OUTSIDE_SPEEX` defined, `resample.c` supplies its own allocation wrappers and
`speex_resampler.h` maps the `spx_*` types and every `speex_resampler_*`
symbol through `RANDOM_PREFIX=mdp_opna_speex`, so no separate `speexdsp_types.h` /
`os_support.h` is required for this standalone build.

## Build definitions

The vendored resampler is compiled with exactly:

```text
FIXED_POINT
OUTSIDE_SPEEX
RANDOM_PREFIX=mdp_opna_speex
```

`USE_SSE`, `USE_NEON`, `FLOATING_POINT` and `VAR_ARRAYS` are deliberately **not**
enabled. `libm` is linked on Unix where SpeexDSP's filter-table creation needs it.