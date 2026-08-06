# Quick wins — production-core batch (QW1/QW2/QW4/QW5/QW6)

Follow-up review: the front-end-bound core needs its 57 KiB `FMOPNA_Clock`
instruction stream shrunk *without duplicating the hot body*. This batch
implements the "Recommended first patch" from the quick-win plan, with every
change independently switchable via CMake options, benchmarked separately and
cumulatively, and retained only when it reduces median cycles/pair with
bit-identical PCM.

Derivation discipline: every variant comes from the SAME vendored body via
`tools/gen_quickwin_core.py` (the vendored file is never edited), and the IC
reset sequence always runs on the generic monolith (`FMOPNA_Clock_RESET0/1`
in `src/mdplayer_opna_internal.h`) so every combination stays bit-exact by
construction. Cross-variant PCM equality is gated by
`mdplayer_opna_pcm_compare` (`tests/pcm_compare.c`).

| CMake option | Transform |
|---|---|
| `MDPLAYER_OPNA_QW1_FIXED_PRESCALER` (ON, ships) | enforce the fixed-144 contract (prescaler select must stay 2) at the session write path (`MDP_OPNA_ERR_UNSUPPORTED_CADENCE` on 0x2E/0x2F), run production clocks through the select=2 specialization. The managed renderer already refuses 0x2E/0x2F; this moves the contract into the native ABI. |
| `MDPLAYER_OPNA_QW2_RESET_SPLIT` (ON, ships) | split `chip->ic` handling: `mdp_fmopna_clock_running` (ic pinned 0 — dead reset branches compiled out) for production; `mdp_fmopna_clock_reset` (ic pinned 1) for IC-active clocks. Pinning is textual read replacement (`chip->ic` is written only at the seed line, so GCC's alias analysis cannot defeat it). |
| `MDPLAYER_OPNA_QW4_COPY_HELPERS` (OFF, rejected) | the most code-expensive repeated inline pipeline memcpys (pg_phase i32×22/23, eg_state u8×22/23, eg_level u16×21/22, op_* u8×22/24) become calls to static noinline helpers. |
| `MDPLAYER_OPNA_QW5_STRIP_DEBUG` (OFF, rejected) | delete state that only feeds diagnostics: pg_dbg / pg_dbgsync, eg_debug / eg_debug_inc / eg_dbg / eg_dbg_sync, o_gpio_a/b and the test-register read_bus construction. Timer / busy / key / ADPCM / IRQ state untouched. |
| `MDPLAYER_OPNA_QW6_HOT_COLD` (OFF, rejected) | `__attribute__((hot))` on the running function, `__attribute__((cold, noinline))` on the reset function (GCC/Clang). |

## Hot-function text size (shipping `-O2`, `nm -S`)

| Build | hot text | cold text |
|---:|---:|---:|
| baseline | 57,901 B | — |
| QW1 | 57,491 B | — |
| QW2 | 54,057 B | 40,045 B (reset) |
| QW1+QW2 | 53,462 B | 39,450 B |
| QW1+QW2+QW6 | 53,080 B | 27,934 B |

## Throughput

`perf/quickwin_bench.sh`: 20 M-pair window, `perf stat -r 3`, `taskset -c 4`,
`cpu_core/*` events; counts summed over 60 M pairs (rule 1: divide by the
actual final master clock; rule 3: pinned core PMU). Wall-rate runs in the
same table; medians agree within ±0.3%.

| Build | cycles/pair | Δ | instr/pair | L1I miss/pair | branch miss/pair |
|---:|---:|---:|---:|---:|---:|
| baseline | 676.5 | — | 1,822.7 | 34.2 | 5.52 |
| QW1 | 672.9 | −0.5% | 1,820.0 | 32.3 | 5.50 |
| QW2 | 640.7 | −5.3% | 1,786.3 | 28.5 | 5.29 |
| QW1+QW2 | **630.9** | **−6.7%** | 1,786.2 | 19.6 | 5.31 |
| QW1+QW4 | 691.4 | +2.2% | 1,838.9 | 39.0 | 5.51 |
| QW1+QW5 | 655.8 | −3.1% | 1,799.7 | 34.5 | 5.51 |
| QW1+QW2+QW4+QW5 | 651.5 | −3.7% | 1,792.9 | 22.1 | 5.21 |
| QW1+QW2+QW5 | 633.5 | −6.4% | 1,773.1 | 20.7 | 5.23 |
| QW1+QW2+QW6 | 633.9 | −6.3% | 1,785.6 | 25.0 | 5.27 |
| QW1+QW2+cold-only | 652.2 | −3.6% | 1,786.2 | 25.5 | 5.32 |
| all (1+2+4+5+6) | 646.8 | −4.4% | 1,791.8 | 23.0 | 5.13 |

## Verdict (acceptance rule: retain only what reduces median cycles/pair with identical PCM)

* **QW1 ships (default ON).** −0.5% cycles, −5% L1I misses, free: the
  fixed-144 contract was already enforced downstream (cadence guard +
  managed renderer), so the specialization adds no restriction a successful
  session ever hit.
* **QW2 ships (default ON).** −5.3% cycles/pair alone, −6.7% combined with
  QW1 (L1I misses −43%): removing the reset code from the hot function is
  exactly the front-end relief the profile asked for, and the cold reset
  function is never fetched during playback.
* **QW4 rejected:** the noinline copy helpers add call/ret traffic and pull
  helper bodies into the fetch footprint; cycles and L1I misses both regress
  relative to baseline and to QW1+QW2. GCC's inline copies were already the
  right size for this core.
* **QW5 rejected:** removes ~20–27 instr/pair on paper but cycles/pair do not
  improve on top of QW1+QW2 (633.5 vs 630.9) and L1I misses rise; the
  stripped debug pipeline reads were not on the critical path. It also costs
  the test-register status diagnostics for no measurable win.
* **QW6 rejected:** 633.9 vs 630.9 for QW1+QW2 (hot+cold), and the cold
  attribute alone is worse still (652.2); the compiler already splits the
  reset branches into `.text.unlikely` on its own, and the reset function is
  never fetched during playback regardless of section placement.

All 26 ctest gates pass on every build in the table, and the fixture window
drains byte-identical stereo PCM across all builds (accepted: identical
native stereo frames, identical frame clocks, identical ADPCM transactions,
identical final PCM; full `fmopna_t` equality is not required for the
deliberately omitted diagnostic-only state).

## Commands

```bash
# one build per combination (example: the shipping QW1+QW2 set)
cmake -S . -B build-qw12 -DCMAKE_BUILD_TYPE=Release \
      -DMDPLAYER_OPNA_QW1_FIXED_PRESCALER=ON \
      -DMDPLAYER_OPNA_QW2_RESET_SPLIT=ON
cmake --build build-qw12 -j
(cd build-qw12 && ctest)
perf/quickwin_bench.sh 20000000 4
mdplayer_opna_pcm_compare build-qw0/libmdplayer_opna.so \
                           build-qw12/libmdplayer_opna.so 2000000
```
