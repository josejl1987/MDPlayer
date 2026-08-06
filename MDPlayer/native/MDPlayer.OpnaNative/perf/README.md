# Corrected native FMP rendering baseline (measurement methodology v2)

This documents the reproducible performance measurement for the native-audio
(Furnace YM2608-LLE) backend and the accounting rules the LLE-core profile
review required. It supersedes the arithmetic in the earlier baseline write-up.

It is a **measurement** of the corrected workload only — **no compiler tuning,
LTO, unrolling, or `-march` flags were applied** to obtain these numbers.

## The four accounting rules (why the previous numbers were under review)

### 1. Per-pair denominators come from the actual final native-session master clock

The native session advances at a fixed **7,987,200 master-clock pairs per
emulated second** — one complete low/high `FMOPNA_Clock` pair per master-clock
increment (see `mdplayer_opna_*`). One native serial frame completes every 144
master clocks:

```text
7,987,200 / 144 = 55,466⅔ native frames/second
```

`SpeexDSP` then resamples those native frames to 44.1/48 kHz.

**A 3-second render therefore implies `3 × 7,987,200 = 23,961,600` master-clock
pairs** — *not* `3 × 44,100 × 144 = 19,051,200` pairs. Any per-pair metric
(cycles/pair, instructions/pair, branches/pair, L1I-misses/pair) must divide by
the **reported final native-session master clock** (`mdp_opna_get_master_clock`,
surfaced as `finalOpnaMasterClock` in the render JSON), never by
`host_rate × 144`.

The corrected normalization for the earlier raw counters (assuming the raw
counts are otherwise correct) is therefore ≈ **×1.25775 (7,987,200/6,350,400)
lower** per pair than the first report's denominator:

| Metric            | First report | Corrected (÷ 23,961,600) |
| ----------------- | -----------: | -----------------------: |
| Cycles/pair       |        544.6 |                    ~433  |
| Instructions/pair |      1,444.3 |                 ~1,148   |
| Branches/pair     |        217.4 |                    ~173  |
| L1I misses/pair   |         27.3 |                     ~22  |

### 2. Timing is reported PER RUN — never summed repetitions

`perf stat -r N` repeats a run N times and sums the counts; treating the summed
result as one run's numbers over one run's denominator inflated every figure
≈ ×N (the earlier `2716 cycles/pair` vs `544.6 cycles/pair` ≈ ×4.99 came from
exactly this). Every run's number is reported and normalized individually.

### 3. Hybrid PMUs must pin the core PMU explicitly

On Intel hybrid processors (this host: 13th-gen i9-13900HX, P-cores 0–15,
E-cores 16–31) generic `cycles`/cache events can resolve to separate
`cpu_core` and `cpu_atom` events, and a thread that barely ran on a PMU reports
scaled counts. This benchmark therefore:

* `cat /sys/bus/event_source/devices/cpu_core/cpus` and
  `cat /sys/.../cpu_atom/cpus` and
  `cat /sys/devices/system/cpu/cpu0/topology/thread_siblings_list` (recorded in
  `provenance.txt`),
* pins each render to a single P-core with `taskset -c <cpu_core cpu>`,
* selects PMU events explicitly: `cpu_core/cycles/`, `cpu_core/instructions/`,
  `cpu_core/L1-icache-load-misses/`, `cpu_core/cache-references/`,
  `cpu_core/cache-misses/`.

`perf stat` output (in `pmu_<secs>s.txt`) includes **enabled / running
percentages**, so a scaled count is visible rather than silently trusted.

### 4. Duration scaling is checked against the SAME exact artifact

Throughput must scale ~linearly with duration once startup is amortized. The
benchmark re-renders 3/12/60 s (or the requested durations) with **the same**
library file, CLI, affinity, environment and fixture, records each render's
`finalOpnaMasterClock`, and prints `master/expected`. A final master clock that
drifts off `duration × 7,987,200` (ratio ≠ 1.0) flags a non-equivalent
benchmark path, not a controllable overhead.

## Artifact provenance (recorded to `provenance.txt`)

For every run the benchmark records:

* library absolute path, **library SHA-256**, ELF **build ID** (`readelf -n`);
* **compiler flags** (CMake `CMAKE_BUILD_TYPE`, `CMAKE_C_FLAGS`,
  `CMAKE_C_COMPILER`);
* CLI path + mtime, CLI SHA-256, git revision, host topology;
* final native master clock, wall time (task-clock is reported by `perf`),
  `cycles`, and `instructions` per run.

## Host / tooling

| item       | value                                                   |
|------------|---------------------------------------------------------|
| CPU        | 13th Gen Intel(R) Core(TM) i9-13900HX (P 0-15 / E 16-31)|
| OS         | Arch Linux                                              |
| perf       | 7.1.4-1 (Arch package)                                  |
| compiler   | GCC/system default for the native Release build          |
| branch     | `feature/linux-fmp-renderer` (see `git log -1`)          |
| CLI        | `mdplayer-render` (Release) `batch` audio-direct path, JSON mode |

## Commands

Run `native_render_bench.sh` (it also records provenance + `cpu_core` PMU stat):

```bash
native_render_bench.sh <ovi-dir> <FMP.COM> <output-dir> 3 12 60
# optional: MDP_BENCH_RUNS=5 MDP_BENCH_CPU=8 MDP_BENCH_LIB=/abs/path/libmdplayer_opna.so
```

Each render is invoked in JSON mode
(`--opna-backend native-audio --quiet --json`); the `tracks[0]` record now
carries `renderedSamples` and `finalOpnaMasterClock` so per-pair normalization uses
the real denominator.

For the symbolized Self-vs-Children profile:

```bash
perf record -o perf.data --freq 400 -g -- \
    taskset -c <cpu_core cpu> mdplayer-render batch ... --max-duration 12 --quiet
perf report -i perf.data --stdio --no-children   # Self %
perf report -i perf.data --stdio --children      # Children %
```

## Baseline profile (Self vs Children — symbolized, 12 s render)

The core synthesis dominated the profile; numbers below are the self/children
share, well above the resampler/serial/ADPCM/glue:

| symbol                                             | Self % | Children % |
|----------------------------------------------------|--------|------------|
| `FMOPNA_Clock` (Furnace core, leaf)                 | 95.6%  | 95.8%      |
| `resampler_basic_interpolate_single` (SpeexDSP)     | 0.7%   | 0.7%       |
| `advance_frames`                                    | 0.5%   | 0.5%       |
| `opna_lle_serial_clock`                             | 0.5%   | 0.5%       |
| `opna_lle_adpcm_clock`                              | 0.4%   | 0.4%       |
| managed (JIT/coreclr) + glue + system               | < 1%   | < 1%       |

> Top-down / PMU percentages are ratios. A faster implementation can keep the
> Front-End-Bound *percentage* high while a different category improves
> absolutely. Judge optimization by **cycles per actual master-clock pair**,
> not by an arbitrary %-reduction in Front-End Bound.

## Status note on the current fixture cadence

The fixed-144 production profile requires every native frame interval to be
exactly 144 master clocks; any other interval latches
`MDP_OPNA_ERR_UNSUPPORTED_CADENCE` and stops resampling. The `XA2021.OVI`
fixture available in this checkout does **not** satisfy that cadence under the
current backend (the render reports `success:false` / `finalOpnaMasterClock:0`).
This is a **pre-existing backend condition** of the fixed-144 profile — it is
reproducible on the unmodified baseline and is outside the scope of the
measurement fix.

The earlier 12 s → 47.1 s (3.93×) and 60 s → 220.9 s (3.68×) real-time-factor
figures were measured on a fixture/code state that satisfied the fixed-144
cadence. Those raw numbers are superseded by the corrected accounting above and
must be regenerated on a valid fixed-144 fixture before they can be compared.
Until such a fixture is supplied, this README reports the measurement
**methodology and accounting rules**, not a fresh baseline table.

## Experiment A — real GCC PGO build (result: rejected)

A genuine two-phase GCC PGO variant was built (`perf/pgo_build.sh` →
`tests/pgo_train.c`) and trained on the shared library with the checked-in real
FM trace (a wire-format capture of FMP-driven OPNA bus writes; XA2047 prefix)
advanced through the session API over 30 M master-clock pairs (fixed-144
cadence preserved throughout). The trained `.so` was compared against the
baseline with an identical dlopen harness (`tests/core_speed.c`) driving the
same trace + 20 M-pair window, pinned to a P-core, counting `cpu_core/*` events.

| Metric                     | Baseline | PGO     | Δ (PGO) |
|----------------------------|---------:|--------:|--------:|
| `FMOPNA_Clock` section size| 57,901 B | 72,306 B| **+25%** |
| `.text` total              | 77,632 B | 117,314B| +51%    |
| cycles / master-clock pair |   1,976  |  2,452  | **+24%**|
| instructions / pair        |   5,464  |  5,927  | +8.5%   |
| L1-icache misses / pair    |   107    |   307   | **+188%**|
| throughput (pairs/s)       | 2.26 M   | 1.74 M  | −23%    |

Deltas are repeatable across re-runs (±1%) and are **consistent and negative**:
PGO bloated the already-monolithic `FMOPNA_Clock` function body and — because a
separate `.text.unlikely` cold section is never emitted for it — it did **not**
shrink the active fetch footprint. It added ~463 executed instructions/pair
(branch-tail duplication in the steady-state loop) and roughly tripled
L1-icache misses/pair, i.e. the exact front-end symptom the profile flagged got
**worse**.

**Conclusion (Experiment A): rejected as the architectural fix.** PGO hot/cold
layout cannot collapse `FMOPNA_Clock` because the function is a single
dominant, mostly-executed body with no separate cold path to partition out. Any
further PGO tuning would chase the wrong lever. The fetch-pressure problem must
be addressed structurally (compile-time low/high phase specialization, or
instruction-count reduction in the hot loop), measured on the correct raw
counts per the accounting rules above.

## Experiment B — compile-time low/high phase specialization (result: rejected)

Motivated by the strongest structural hypothesis the profile offered: if the
low (`mclk1`) and high (`mclk2`) halves of `FMOPNA_Clock` are mutually
exclusive, emitting them as **two functions from the same derived source** — the
`clk` parameter pinned to a compile-time constant — should let the compiler
eliminate the opposite phase's code and shrink each half's active fetch
footprint. Per the rule "do not hand-maintain two implementations", the two
functions were **generated**, not textual-edited by hand.

**Generation (single derived source).** `tools/gen_phase_specialize.py` starts
from the *unchanged* vendored `fmopna_impl.c` and emits
`mdp_fmopna_clock_low` / `mdp_fmopna_clock_high`, where the **only** edits are
the function name and the three seed lines that derive `mclk1`/`mclk2`
from the phase (now pinned to `{1,0}` / `{0,1}`). The `clk` parameter is dead
(`clk=0` ⇒ `mclk1=1,mclk2=0`; `clk=1` ⇒ `mclk1=0,mclk2=1`). Everything else is
byte-identical, so each emitted function is equivalent by construction to the
reference `FMOPNA_Clock(chip, {0,1})` at the same half-edge. The adapter's
half-edge calls are routed through them only under the opt-in
`MDPLAYER_OPNA_PHASE_SPEC` CMake flag; the shipping build is untouched.

**Correctness.** The phase-specialized `.so` passes the identical 26-test ctest
suite as baseline, and the `core_speed` harness drains the same session state
(drained counts byte-equal) over identical windows — half-edge lockstep holds.

**Footprint.** Because the mutually-exclusive phase blocks are only the small
latch/prescaler edges, and the dominant operator-synthesis body runs on *every*
clock regardless of phase, GCC had almost nothing to eliminate:

| Function                     | size  |
|------------------------------|------:|
| `FMOPNA_Clock` (monolith)    | 57,901 B |
| `mdp_fmopna_clock_low`       | 56,894 B |
| `mdp_fmopna_clock_high`      | 54,643 B |

i.e. ≤ 5.6% of the body was ever phase-dead; ~95% of the function (the shared
synthesis) must be resident on both phases.

**Throughput** (same harness + fixture, `taskset -c 4`, 20 M-pair window, 2 runs,
`cpu_core/*` events; `build-release` baseline vs `build-phase` specialized):

| Metric                  | Baseline | Phase-spec | Δ     |
|-------------------------|---------:|-----------:|------:|
| cycles / master-clock pair | 2,016 | 2,606 | **+29.3%** |
| instructions / pair     | 5,468 |    5,387 | −1.5%  |
| L1-icache misses / pair |   101 |      594 | **+487%** |
| throughput (pairs/s)    | ~2.1 M |   ~1.7 M | −21%   |

> The per-pair values are raw `cpu_core/*` counter sums divided by the 20 M-pair
> window (a `perf stat -r 3` run reports per-run sums, not a /run average, so
> dividing by the window is the correct per-pair basis).

**Conclusion (Experiment B): rejected.** Splitting the monolith into two
near-duplicate functions — both still ~the full body — **doubles the resident
fetch working set and explodes L1-icache misses +487%**, replicating PGO's
failure in a different way. The instruction count fell only ~1.5% (the removed
phase-gate branches), far too little to offset the front-end miss cost. This
**rules out compile-time phase specialization** for this core, since the phase
halves share the real hot body. The remaining structural lever is
instruction-count reduction inside the shared synthesis loop itself.

## Experiment C — fixed-prescaler specialization (result: small win, kept opt-in)

Before touching source, the profile was checked for a **hot loop to reduce**.
`perf annotate` on `FMOPNA_Clock` shows the cost is *not* concentrated: the top
~40 sampled instructions are spread across source lines 197–5639 (of 5,864) at
0.2%–0.8% each, and no single instruction exceeds 0.8%. The ~5,468
instructions/pair are the summation of ~5,470 cheap, flatly-distributed RTL
register/LUT updates. There is **no tight loop** whose per-iteration count can
be reduced; the only per-clock patterns that can be removed are a handful of
branchy state selectors (the prescaler switches, the phase gates).

The review's named C candidate — **fixed-prescaler specialization** — is the
only one of those that removes work **without growing the resident footprint**.
`tools/gen_fixed_prescaler.py` derives `mdp_fmopna_clock_fixedpresc` from the
same vendored body, replacing the two `switch (prescaler_sel[1])` blocks with
their `case 2:` arm body (select=2 is the IC-reset / Furnace-default value the
adapter's session path uses). Same half-edge derivation discipline as B;
bit-exact only while the select stays 2, opt-in (`MDPLAYER_OPNA_FIXED_PRESCALER`),
and it never ships.

The emitted function is **57,555 B — smaller than the 57,901 B monolith** (it
removes the other switch arms; nothing is duplicated). All 26 tests pass, and
`core_speed` drains the same session state at the same window.

Throughput (`taskset -c 4`, 20 M-pair window, `perf stat -r 3`, `cpu_core/*`):

| Metric                  | Baseline | Fixed-prescaler | Δ     |
|-------------------------|---------:|----------------:|------:|
| cycles / master-clock pair | 2,016 | 1,982 | **−1.7%** |
| instructions / pair     | 5,468 |    5,460 | −0.16% |
| L1-icache misses / pair |   101 |      100 | −0.9%   |
| function text size      | 57,901 B | 57,555 B | −0.6% |

**Conclusion (Experiment C): a real but small win (−1.7% cycles/pair).** It is
the only one of the three experiments that does not regress the front-end — it
removes branchy selector code while keeping the resident footprint flat, so
cycles follow (a bit of branch/fetch relief) rather than collapse. But the
magnitude confirms the flat-profile ceiling: there is no large per-clock target,
so source-level instruction reduction tops out at ~2% without abandoning the
core's bit-exact / cycle-accurate gate (which any algorithmic rewrite, or
dropping non-audio-affecting register toggles, would break). Of the three
experiments, **C is the only one usable in principle** — but its ~2% gain does
not justify the added bit-exactness constraint (select must stay 2), so it is
left opt-in and not wired into the shipping path.


## Quick wins — production-core batch (result: QW1+QW2 ship)

The follow-up batch shrinking the hot `FMOPNA_Clock` instruction stream:
fixed-prescaler production enforcement (QW1) and the ic-pinned reset split
(QW2) are default-ON shipping; the noinline copy helpers (QW4), debug-strip
(QW5) and hot/cold attributes (QW6) were measured and rejected. Full
derivation, per-flag benchmark table (630.9 cycles/pair vs 676.5 baseline,
L1I misses −43%) and verdicts: `perf/README.quickwin.md`.


## What this does NOT claim

* It is not yet a claim that render-ahead alone can sustain real-time playback.
* It is not a conclusion that any GCC flag / structural rewrite can materially
  reduce `FMOPNA_Clock` cost. Measured ceiling: source-level optimization tops
  out at ~2% (Experiment C) because the per-clock work is flatly distributed
  with no dominant loop; PGO layout (A) and phase specialization (B) both regress
  the front-end and are **ruled out**.
* The fixed-prescaler specialization (C) is a real −1.7% but is **not** wired
  into the shipping path: it adds a bit-exactness constraint (prescaler select
  must stay 2) and is only valuable if that constraint is acceptable.
* `-march=native` / LTO / unrolling are **not** recommended defaults from this
  measurement alone.

