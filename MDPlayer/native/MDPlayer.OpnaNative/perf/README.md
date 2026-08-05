# Corrected native FMP rendering baseline

After the master-clock timeline fix, this is the reproducible performance
baseline for the native-audio (Furnace YM2608-LLE) backend. It is a measurement
of the corrected workload only — **no compiler tuning, LTO, unrolling, or
`-march` flags were applied** to obtain these numbers.

## Host / tooling

| item      | value                                             |
|-----------|---------------------------------------------------|
| CPU       | 13th Gen Intel(R) Core(TM) i9-13900HX (32 logical)|
| OS        | Arch Linux                                        |
| perf      | 7.1.4-1 (Arch package)                            |
| compiler  | GCC/system default for the native Release build    |
| branch    | `feature/linux-fmp-renderer` (see `git log -1`)    |
| fixture   | XA2020.OVI (Sugimo, 3219 bytes)                   |
| CLI       | `mdplayer-render` (Release) `batch` audio-direct path |

## Correctness precondition

These numbers were measured **only after** the corrected render passed the
timeline regressions (startup region, duration oracle, key-on/off separation,
capture block-size independence, replay-cursor progression) and produced a
12.0s / 60.0s deterministic WAV (see the test suite).

## Commands

Run `native_render_bench.sh` (the exact commands it runs):

```bash
# 12s and 60s real-time-factor
mdplayer-render batch <ovi-dir> --output-dir <out>/native \
    --opna-backend native-audio --fmp-com <FMP.COM> \
    --overwrite --max-duration 12 --quiet
mdplayer-render batch <ovi-dir> --output-dir <out>/native \
    --opna-backend native-audio --fmp-com <FMP.COM> \
    --overwrite --max-duration 60 --quiet

# symbolized corrected 12s profile
perf record -o perf.data --freq 400 -g -- \
    mdplayer-render batch <ovi-dir> --output-dir <out>/native \
    --opna-backend native-audio --fmp-com <FMP.COM> \
    --overwrite --max-duration 12 --quiet

perf report -i perf.data --stdio --no-children   # Self %
perf report -i perf.data --stdio --children      # Children %
```

## Results (XA2020.OVI)

| audio duration | wall time | real-time factor |
|----------------|-----------|------------------|
| 12 s           | 47.1 s    | **3.93×** slower  |
| 60 s           | 220.9 s   | **3.68×** slower  |

Real-time factor = wall time / audio duration (not ×10 or ×100 — the earlier
40–50× figure was an arithmetic error and is incorrect for this workload).

## Profile (corrected 12 s render) — Self vs Children, reported separately

| symbol                                             | Self % | Children % |
|----------------------------------------------------|--------|------------|
| `FMOPNA_Clock` (Furnace core, leaf)                 | 95.6%  | 95.8%      |
| `resampler_basic_interpolate_single` (SpeexDSP)     | 0.7%   | 0.7%       |
| `advance_frames`                                    | 0.5%   | 0.5%       |
| `opna_lle_serial_clock`                             | 0.5%   | 0.5%       |
| `opna_lle_adpcm_clock`                              | 0.4%   | 0.4%       |
| managed (JIT/coreclr) + glue + system               | < 1%   | < 1%       |

* `Self %` is the function's own (exclusive) cost, from `perf --no-children`
  (44k samples on the P-cores). `Children`/Self are reported separately; a
  leaf like `FMOPNA_Clock` has Children ≈ Self because its callers are thin.
* The core synthesis (`FMOPNA_Clock`) is the dominant exclusive hotspot; the
  resampler, serial decoder, ADPCM handler, bus and managed glue are all
  individually ≤ 1%.

## What this does NOT claim

* It is not a claim that render-ahead alone can sustain real-time playback —
  this backend renders at ~3.7–3.9× slower than real time, so indefinite
  sustained real-time playback is not possible on this core without either a
  faster synthesis path or substantially pre-rendering the track.
* `-march=native` / LTO / unrolling are **not** recommended defaults from this
  profile alone; no projection is made. A separate measured experiment would be
  needed before any such change.
