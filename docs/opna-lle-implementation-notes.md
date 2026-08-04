# native-audio FMP backend — Implementation Notes

This document tracks the MDPlayer FMP **YM2608 audio backend** as of Prompt 8R,
which replaces the live CPU/native-IRQ scheduling path (`native-lle`) with a
simpler two-pass **trace-driven** native renderer: `native-audio`.

> **Control-plan compromise (deliberate).** `native-audio` is **not** a full
> hardware-driven LLE execution mode. It keeps the proven MDSound/FMP driver
> control path authoritative and uses the native YM2608 device purely as an
> offline audio renderer. The native device never drives Nise98, never polls
> IRQ, never reads status, and is never invoked during replay to decide
> execution.

## 1. Architecture

```
Pass 1 — control capture
Nise98 + existing FMP runtime + existing OpnaTimer
    ├── exact YM2608 register events   (authoritative Nise286 CPU cycles)
    ├── exact PPZ8 command events      (authoritative Nise286 CPU cycles)
    ├── global call order  (one monotonic Sequence across OPNA + PPZ8)
    ├── loop / termination result
    └── final output timeline   (fade/tail/loop decisions from legacy path)

Pass 2 — native audio replay
captured YM2608 events
    → exact CPU-cycle→OPNA-master-clock mapping   (floor(cpu×7,987,200/cpuHz))
    → native YM2608 core                          → native OPNA PCM
captured PPZ8 events
    → exact CPU-cycle→output-frame mapping       (floor(cpu×sr/cpuHz))
    → shared PPZ8 renderer                        → PPZ8 PCM
native OPNA PCM + PPZ8 PCM
    → integer mixer (signed 32-bit accumulate, single 16-bit clamp)
    → captured fade/tail envelope
    → final stereo PCM
```

- Backend enum: `internal enum FmpOpnaBackend { Mdsound = 0, NativeAudio = 1 }`.
- **Default: `Mdsound`** (unchanged, still byte-identical to the pre-backend branch).
- The native device controls only YM2608 register application, FM/SSG/rhythm/
  ADPCM-B synthesis, chip-clock progression, resampling and OPNA PCM output.

## 2. The control path is authoritative

Pass 1 reuses the existing FMP execution path unchanged (that is the point).
During capture:

- MDSound/FMP driver execution is unchanged.
- Timer polling and IRQ-driven driver behavior go through the proven path.
- The `NativeAudioFmpPcmSession` run in `Boot()` executes the ordinary legacy
  MDSound session to completion, discarding its PCM, and captures every YM2608
  write and PPZ8 command at the authoritative Nise286 cycle count.

The capture is the contract for final duration: `FinalOutputFrame`,
`FadeStartOutputFrame`, `FadeEndOutputFrame`, `TailEndOutputFrame`, loop count
and termination reason all come from the legacy session. Replay never
independently recalculates termination, loop count or duration.

## 3. Pass 1 — deterministic control capture

Capture types (all `Fmp.Core.Rendering`):

- `FmpExecutionCapture` — the finalized capture (duration, loop, termination,
  banks, + a single globally-ordered `Events` list).
- `FmpCapturedEvent` — value-type interface (`CpuCycle`, `Sequence`).
- `CapturedOpnaWrite(cycle, seq, port, address, data)` — `port` is the logical
  YM2608 port (0/1), normalized at the boundary.
- `CapturedPpz8Command(cycle, seq, port, address, data, bankId)` — `BankId`
  references a capture-local `Ppz8BankSnapshot` for a bank load.
- `Ppz8BankSnapshot(BankId, LogicalName, Data, ChannelLengths, Sha256)` —
  immutable bank content, deduplicated by SHA-256 (not filename).

Integration: an optional `IFmpExecutionCaptureSink` tap on `FmpRuntime` is
called at the exact operation boundaries (when Nise98 performs the write). The
authoritative timestamp is `Nise98.GetCPU().TotalCycles`, read at the I/O call
boundary. The ordinary MDSound path is byte-identical whether or not a capture
sink is installed.

Not captured: native status reads, IRQ levels, MDSound PCM, wall clock,
Stopwatch, floats, debug text. Register writes are never coalesced, deduplicated
or reordered.

## 4. Pass 2 — native replay

- `NativeOpnaTraceRenderer` owns `IClockedOpnaDevice`, the `NiseOpnaClockMapper`
  and the OPNA event cursor. Every YM2608 write is applied at its exact mapped
  master clock (`floor(cpuCycle * 7,987,200 / cpuHz)`); equal-cycle writes keep
  call order and equal clocks. The device is advanced to each chunk-boundary
  clock. It never reads status or IRQ.
- `Ppz8TraceRenderer` owns the shared MDSound PPZ8 renderer, the immutable bank
  snapshots and the CPU→output-frame mapper (`floor(cpuCycle * sr / cpuHz)`).
  Commands are applied before their mapped output frame; output is delayed by
  the native `OutputLatencyFrames` to align with OPNA.
- `NativeAudioFmpPcmSession` (`IFmpPcmSession`) coordinates: `Boot()` runs the
  capture pass, then creates the native and PPZ8 replay state; `Render()` mixes
  bounded `4096`-frame chunks through the integer mixer and the captured
  envelope.

Replay invariants (architecturally enforced and tested):

- no Nise98 execution during replay;
- no native status reads;
- no native IRQ reads;
- no deriving timing from output samples;
- output chunk size and native drain size do not change the PCM.

## 5. CLI and serialization

```
--opna-backend <mdsound|native-audio>
    Select the YM2608 audio backend for FMP rendering.
    Default: mdsound.
```

- Accepted: `mdsound`, `native-audio`.
- `native-lle` is **rejected**, never aliased:
  `The native-lle backend is not available. Use native-audio for native YM2608 audio rendering.`
- `auto`, `bogus` and unknown values are rejected.
- Old serialized requests without a backend field default to `Mdsound`.

There is no GUI setting and no UI-specific backend field. The native library is
not loaded during ordinary GUI startup. Trace persistence is out of scope: the
capture is an internal, in-memory implementation detail — no production trace
files, no import/export commands.

## 6. Removal of native-lle

The former live scheduler (`NativeLleFmpPcmSession`, `ClockedFmpExecutionSession`,
`ClockedNise98OpnaBridge`) and its `native-lle` surface are removed from
production. The fixed low-level primitives are preserved:

- `IClockedOpnaDevice`, `NativeOpnaDevice`, the native ABI wrapper,
  `OpnaNativeSession`, `NiseOpnaClockMapper`, `NativeFmpNoProgressException`,
  `Ppz8OutputDelayBuffer`, the native output-latency query, and the native
  cadence/drain/latency tests.

## 7. Limitations

- The native ABI supports only the fixed **144-master-clock** cadence. Captured
  traces writing `0x2E`/`0x2F` throw `NotSupportedException` (no fallback).
- Replay never falls back to MDSound and never aliases a failed native render.
- PPZ8 and OPNA PCM are deterministic per platform but not compared byte-for-byte
  against MDSound PCM (that is not an equality requirement).

## 8. Future direction

Trace-driven native replay is intentionally the current production architecture.
A future event-driven LLE execution mode could reuse the same exact clock
mappers and native device while restoring a live IRQ loop — that is a separate
architecture and is out of scope here.

## 9. Prompt 9R — release status for the `native-audio` backend

### Selection and defaults
- **MDSound remains the default.** `native-audio` is an explicit, opt-in backend
  (CLI `--opna-backend native-audio`). There is **no automatic fallback**: a
  failed native render is a clear, typed error, never a silent MDSound replay.
- The retired `native-lle` value is rejected, not aliased.

### Architecture (unchanged)
- **Pass 1** runs Nise98 + the legacy timer/control session purely for
  deterministic capture (CPU-cycle event stream, OPNA register writes, PPZ8
  commands, immutable PPZ8 banks).
- **Pass 2** replays the captured events through the native YM2608 plus the shared
  PPZ8 renderer, producing aligned mixed PCM. Replay performs **zero** native
  status reads, **zero** native IRQ reads, and **no** CPU/Nise98 execution.

### Supported rates and platforms
- Output rates **44.1 kHz, 48 kHz, 96 kHz** only.
- Native library paths:
  - Linux x64: `runtimes/linux-x64/native/libmdplayer_opna.so`
  - Windows x64: `runtimes/win-x64/native/mdplayer_opna.dll`
- The runtime library has **no system SpeexDSP dependency** (SpeexDSP is
  statically linked and renamed), no Furnace runtime, and no RPATH/CWD
  dependency on Linux.

### Determinism
- Capture is deterministic across fresh sessions and independent of the discarded
  PCM buffer size, and capture instrumentation does not change MDSound PCM.
- Replay is deterministically identical **within a platform** for the same
  immutable capture (byte-identical PCM across sessions and block/drain sizes).
- Cross-platform byte equality is **not guaranteed** and is not required; the
  cross-platform hash comparison is reported, not asserted equal.

### Performance expectations
- Legacy MDSound and the capture pass run far faster than real time.
- **Native replay is dominated by the vendored Furnace YM2608-LLE synthesis**
  (profilers attribute ~96% of replay wall time to `FMOPNA_Clock`). On a typical
  x64 development machine the `native-audio` two-pass total renders at roughly
  **0.2× real time** for chip-dense FMP tracks. The LL synthetic cost is intrinsic
  to the faithful LLE core; a faster result would require skipping/coarsening
  synthesis, changing chip clock or timestamps, lowering SpeexDSP quality, or
  replacing the resampler — all of which are rejected. The backend is therefore
  most practical for **offline rendering**, not real-time playback.
- The full 30-minute extended stability tier is scheduled (nightly); at ~0.2×
  real time it takes ~2.5 h wall.

### Symbol surface
- The shipped libraries export **only the documented `mdp_opna_*` API** (12
  functions). Vendored-core, FIFO, resampler-wrapper and SpeexDSP internals are
  hidden.

### Troubleshooting
- **Missing native library** — the managed wrapper raises a clear typed error
  telling you the library path; MDSound is unaffected. Rebuild the native library
  per `native/MDPlayer.OpnaNative/README.md`.
- **ABI mismatch** — the wrapper checks the exported ABI version against its
  target and reports; rebuild the native library from the same tree.
- **Unsupported cadence** — a captured trace with `0x2E`/`0x2F` prescaler writes
  throws `NotSupportedException` (no fallback).
- **Capture memory usage** — capture events/banks are small value types held in
  memory for the two-pass render; the capture store is reported in benchmarks.
- **Slow replay pass** — expected (see Performance expectations); use a shorter
  render window or the legacy MDSound backend for interactive preview.
- **Slow capture pass** — capture uses the legacy MDSound session and is normally
  fast; a very large track raises event-count memory proportionally.
- **Windows DLL load failure** — confirm `runtimes/win-x64/native/mdplayer_opna.dll`
  is present and depends only on KERNEL32 + the UCRT (no `speexdsp.dll`).
- **Linux dependency failure** — `ldd libmdplayer_opna.so` should show only
  `libm`/`libc`; no system SpeexDSP or Furnace runtime.

