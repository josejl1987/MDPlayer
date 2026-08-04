# YM2608-LLE Gold Backend — Implementation Notes

Tracking file for the MDPlayer FMP YM2608-LLE (`nuked-lle`) backend.

## Pre-furnace WIP preservation (spec §3)

Location: `/home/jose/opna-wip-archive/`

- `ym2608-lle-pre-furnace-wip.patch` — `git diff --binary` of the tracked working
  tree at preservation time (the repo-root `.gitignore` OPNA/ runtime ignore rules).
- `ym2608-lle-pre-furnace-opnanative-src.tar.gz` — tarball of the untracked
  `MDPlayer/native/MDPlayer.OpnaNative/` implementation files (excluding the
  generated `build*/` trees).

Also, a separate worktree `/home/jose/MDPlayer-worktrees/opna-lle` (branch
`feature/opna-lle-backend`) holds a **superseded** `nopna.c`-based attempt plus
`STATUS-Stage1.md` documenting the earlier silent-output diagnostic — that path is
rejected by the task (no `nopna.c` in production) and is preserved for reference only.

The current uncommitted native work ("WIP") is recoverable from the archive above.

## Prompt 4 — SSG, Rhythm, ADPCM-B, Timers, IRQ

Split into 4A (independent, committed) and 4B (bus-scheduler conformance, blocked).

### 4A — committed (d16218f1..7b815976)

Commits (in order):

- `native: extract Furnace-compatible SSG mix` — d16218f1
  Canonical `MDP_OPNA_FURNACE_SSG_ANALOG_SCALE` (= 42), kept with the short
  `OPNA_FURNACE_SSG_ANALOG_SCALE` alias; the mix itself landed in Prompt 3.
- `native: extract Furnace ADPCM memory protocol` — b21d0f5c
  Canonical `MDP_OPNA_ADPCM_RAM_BYTES` (256 KiB) / `MDP_OPNA_ADPCM_ADDRESS_MASK`
  (0x3ffff); ADPCM reset now clears the 256 KiB backing RAM; test-only
  `opna_lle_adpcm_load()`.
- `test: add pan SSG rhythm and ADPCM native gates` — 0e4762d0
  `tests/pan_test.c`, `tests/ssg_test.c`, `tests/adpcm_test.c`.
- `native: add timer IRQ and determinism tests` — 7b815976
  `opna_lle_obs_status_timer_a/b`, `opna_lle_obs_irq_pull`; `tests/timer_irq_test.c`,
  `tests/determinism_test.c`.

New native gates (all pass; `ctest` 10/10 in release):

- pan: left/right never swapped; mono SSG folds into both channels.
- SSG: both channels, exact ×42 scale, disable removes contribution, digital PCM
  preserved.
- ADPCM-B: CAS/RAS row+column formation, first/last address, `0x3ffff` wrapping,
  read (`mem -> input.dm/dt0`), test-only loader (write), reset clears RAM.
- Timer: deterministic Timer-A overflow pinned at master clock 151344, Timer-B at
  590976; independent status clears; stable IRQ-pull observation.
- determinism: FM+SSG stream rendered in three separate processes yields an
  identical native PCM SHA-256.

Sanitizer status: release build fully green. ASan build compiles with no errors and
the new tests run clean (incl. leak detection). The `-fsanitize=undefined` build
reports signed left-shift noise *inside the pinned upstream `fmopna_impl.c`* only —
the project's CMake already documents UBSan as "noisy inside upstream" and optional;
none of the reports come from our glue/test/native code.

### 4B — blocked: bus-scheduler pin conformance

The following Prompt-4 gates cannot be advanced until a differential test against a
faithful copy of Furnace's `DivPlatformYM2608::acquire_lle` scheduler establishes the
missing pin alignment:

- RSS / rhythm: the core has the OPNA RSS unit (fmopna_rom.h `rss_rom[8192]`,
  `reg_rss`, `fsm_rss`, `rss_18`, `ac_rss_sum_l/r`, `last_rss_sample`). Pan/params
  and the play-state machine engage, but `rss_sample_shift` stays 0 and the serial
  fold never asserts, so no nonzero rhythm PCM.
- IRQ assertion / deassertion from a timer source: `o_irq_pull` reflects
  `reg_irq[1]` from register 0x29; the `data_bus2` value observed at write-latch
  time (`0x17f`) does not enable the timer bits.
- per-channel FM pan (0xB4 / `ac_fm_pan`) and ADPCM-B nonzero playback exhibit the
  same scheduler-timing dependency.
  `tests/rhythm_test.c` is intentionally not added yet.

Next step for 4B: add `tests/furnace_bus_reference.{c,h}` (a literal test-only
scheduler copy) and `tests/rss_bus_conformance_test.c` that differential-compare pin
traces against the MDPlayer scheduler on a synthetic RSS register stream, then fix
the first divergence before attempting the RSS state-progression ladder.

MDSound remains the default backend; `nopna.c` is not used; no managed code changed;
no resampler added; no external rhythm WAV is loaded.

# Prompt 5 — Native ABI, Timed Audio FIFO, Prescaler/Cadence Audit

## Outcome: fixed-rate resampler HARD STOP (variable cadence)

Per spec §21/§22, the fixed-rate 144-clock resampler (Workstream D) and the
shared-ABI library + integration (Workstream E) were NOT built because the
cadence audit proves native cadence is NOT constant when OPNA prescaler-select
registers are honored through the production bus scheduler.

Production scheduler change: the resident `mdplayer_opna_bus.c` absorbed
prescaler-select writes 0x2E/0x2F into the register pool (Furnace's silent-ignore
behavior). Prompt 5 removes that special case so 0x2E/0x2F flow through the normal
Furnace-compatible bus, as the directive requires ("Do not copy Furnace's behavior
of ignoring 0x2E or 0x2F").

Measured cadence (real completed serial-frame timestamps, 40000-clock windows):
  - reset / default: fixed 144 clocks/frame
  - 0x2D:            fixed 144 clocks/frame
  - 0x2E:            VARIABLE — 556 frames, 555 non-144 intervals (interval
                     alternates 72/144)
  - 0x2F:            no frames produced (cadence breaks / silent)
  - FM fixture:      fixed 144 clocks/frame

Because any supported trace changes cadence when prescaler writes are honored,
spec §22 applies:
  1. keep the timed native-frame FIFO (done),
  2. do NOT implement the fixed-rate resampler (done),
  3. add a failing test named
     fixed_rate_resampler_requires_constant_native_cadence (done —
     tests/native_cadence_test.c),
  4. stop after Workstreams A, B and C (done — no resampler, no Workstream E,
     no managed integration).

## Workstream A — versioned session ABI

- `include/mdplayer_opna.h`: `MDP_OPNA_ABI_VERSION 1`, opaque `mdp_opna_session`,
  `mdp_opna_result` codes, fixed hardware constants (7,987,200 Hz,
  262144-byte ADPCM RAM), `mdp_opna_open_options` (44100/48000/96000 only).
- `src/mdplayer_opna_session.{c,h}`: session lifecycle + all 11 public ABI
  functions. Power-on runs the existing validated 576/576/576 reset; chip reset
  calls the validated `opna_lle_reset_core` while preserving external ADPCM RAM
  (transient stack copy, no heap allocation) and the configured rate; master
  clock resets to zero; monotonic-clock and equal-clock-order semantics enforced.
- `mdp_opna_read_status` returns live LLE core status via the validated
  `opna_lle_obs_status_timer_a/b` helpers (never shadow/cached/MDSound/synthesized).
- `mdp_opna_drain_audio` consumes only already-queued timed frames and never
  advances emulated time.

## Workstream B — timed native-frame FIFO

- `src/mdplayer_opna_fifo.{c,h}`: fixed 16384-frame (256 KiB) power-of-two ring
  of `mdp_opna_timed_frame{master_clock,left,right}`; bounded, no per-frame
  allocation, exact insertion order, deterministic wraparound, overflow is an
  explicit error that never overwrites old data, reset empties and resets the
  observed-watermark.

## Workstream C — prescaler / cadence audit

- `tests/prescaler_audit_test.c`: diagnostic observation of frame intervals and
  prescaler mode across reset + 0x2D/0x2E/0x2F + FM fixture.
- `tests/native_cadence_test.c`: the strict
  fixed_rate_resampler_requires_constant_native_cadence gate which FAILS because
  cadence is variable.

## Tests / sanitizer status

Release + ASan: 14/15 pass; the single failure is the intentional
fixed_rate_resampler_requires_constant_native_cadence hard-stop gate. The
combined ASan+UBSan run passes 14/15 after the FIFO watermark fix. The UBSan-only
run reports only the documented intentional signed-shift noise inside the pinned
vendored `fmopna_impl.c` (left shift of large constants) — none originates in the
new ABI/FIFO/audit code; this matches the project's long-standing CMake note that
`-fsanitize=undefined` is noisy inside upstream.

MDSound remains the default backend; no managed/Furnace/CLI code changed; `nopna.c`
is not used; no resampler and no coefficient generation added.

## Prompt 6 — managed clocked OPNA session (Linux renderer integration)

- `src/MDPlayer.Fmp.Core/Playback/Opna/` — managed wrapper over the native
  versioned session ABI (`IClockedOpnaDevice`, `NativeOpnaDevice`,
  `OpnaNativeSession`, `MdpOpnaResult`, `OpnaException`).
- `OpnaNativeSession` owns the native session handle through a private
  `SafeHandleZeroOrMinusOneIsInvalid` subclass (`OpnaSessionHandle`); the native
  `mdp_opna_close` runs exactly once from `ReleaseHandle`, and every ABI call
  goes through `DangerousGetHandle()` after an open/closed guard.
- `src/MDPlayer.Fmp.Core/Playback/NativeLibraryResolver.cs` — one assembly-wide
  `DllImportResolver` shared by the SPC and OPNA wrappers; each wrapper registers
  only its own library name.
- `src/MDPlayer.Fmp.Core/Playback/Opna/ILegacyFmpAudioEngine.cs` +
  `LegacyMdsoundFmpAudioEngine.cs` — existing MDSound adapter (production default
  unchanged).
- Shared renderer: `src/MDPlayer.Fmp.Core/Nise98/NisePPZ8.cs` is a verbatim
  extraction of the legacy `MDPlayerx64/Driver/FMP/Nise98/NisePPZ8.cs` (fidelity
  proven by `Ppz8ExtractionFidelityTests`).

## Prompt 8 — backend selection, native FMP session, CLI surface

### Backend selection (Commit 2)

- `FmpOpnaBackend` enum (`Mdsound = 0`, `NativeLle = 1`) — the default value is
  0 so existing configs/CLI invocations keep the byte-identical MDSound path.
- `IFmpPcmSession` + `FmpPlaybackContext` + `FmpPcmSessionFactory` — the factory
  constructs exactly the requested backend and never catches a native
  construction failure to fall back (no-fallback enforced by tests).
- `LegacyMdsoundFmpPcmSession` — the existing sample-position sink session,
  byte-identical to the pre-backend renderer (pinned by
  `FmpLegacyBaselineTests`).
- `FmpRenderer.Options.OpnaBackend` routes the choice; the default render loop
  is untouched.

### Native session (Commit 3)

- `NativeLleFmpPcmSession` boots the real FMP driver on Nise98 through
  `ClockedFmpExecutionSession` (same LoadRun/FMPRegistPPZ8/load/play sequence as
  the legacy path). Every YM2608 access crosses the clocked bridge at the
  authoritative cycle count; the device is advanced only by cycles actually
  executed.
- The bridge's data-port reads (0x8a/0x8e) now return the real YM2608 register
  read-back (last written value, 86-board pseudo-registers 0x0e/0xff) — the
  legacy FMPortInport semantics the FMP driver's boot code depends on; status
  reads (0x88/0x8c) still return the native device status directly.
- PPZ8: driver commands are captured with their CPU cycle, mapped to output
  samples by an exact UInt128 rational mapper (`NisePpz8CommandMapper`), applied
  to the shared MDSound PPZ8 renderer, delayed by the device's fixed output
  latency (`Ppz8OutputDelayBuffer`) and mixed with the drained OPNA frames by
  pure-integer arithmetic (`OpnaPpz8IntegerMixer`).
- Deterministic tests: mapper-vs-clock-mapper oracle, delay-line alignment,
  mixer exact arithmetic, latency query, backend selection, no-fallback.

### Fixed-cadence fail-closed (verified against a real FMP.COM + .OVI)

The native session is fail-closed: the fixed 144-master-clock cadence profile
(ABI v1) cannot honor the FMP driver's boot sequence. The driver polls the OPNA
status registers (48+ port-0x088 reads in a tight loop); each status read runs
the LLE core clock with read pins asserted, which perturbs the serial decoder
frame phase (the observed frame interval becomes 288 master clocks instead of
144). The cadence guard then latches `MDP_OPNA_ERR_UNSUPPORTED_CADENCE` and the
session reports a deterministic error — it never falls back, never produces
partial output, and never silently degrades. This is covered by
`NativeFmpIntegrationTests` (fail-closed contract, determinism across slices,
no WAV left behind) and `OpnaBackendCliTests` (option passes through to the
native session).

A future Prompt would need to relax the cadence guard / make the serial decoder
robust to read-transaction clocking before the native session can boot real
FMP.COM.

### CLI surface (Commit 4)

- `--opna-backend mdsound|native-lle` on the standalone audio-render path
  (`batch`/`analyze` option parser), strictly validated (unknown values rejected
  with exit 2 semantics), default absent = MDSound.

### Prompt 8.2 — status-read cadence fix, idle-time advancement, construction order

- **Native status-read correction.** `do_status_read()` no longer advances the
  master clock and no longer runs the serial decoder. Reading the YM2608 status
  is asynchronous to the FM clock on the real chip, so the read is evaluated on
  a transient copy of the core (pins applied, one clock pair, `o_data` read
  back) while the real core, the serial decoder, the FIFO and the session clock
  stay untouched. The 288-clock interval that a status read used to introduce
  is gone; every raw frame interval stays exactly 144 regardless of status-poll
  density or phase (`tests/status_cadence_test.c`). Commit
  `4591d876` "native: keep OPNA cadence stable during status reads".
  `ReadStatus(requestedClock)` now advances to `requestedClock` when necessary,
  evaluates status there, returns it, and never advances beyond it.
- **Result:** the real FMP driver's boot sequence (which polls status heavily)
  now boots without a cadence error, so the fail-closed gate tests were updated
  to assert the new deterministic failure point (the emulator's next
  unimplemented opcode) instead of the cadence error. The no-fallback and
  no-partial-WAV contract is unchanged.
- **Construction order.** `ClockedFmpExecutionSession` now takes the
  authoritative `Nise286` CPU in its constructor and rejects a null CPU
  immediately (`ArgumentNullException`). `NativeLleFmpPcmSession.Boot()` follows
  the explicit order: construct Nise98 → (coordinator installs the clocked
  bridge) → `Init()` → obtain the CPU → construct the coordinator with it →
  boot FMP. The session can no longer fail later from `ExecuteSlice()` with a
  `NullReferenceException`.
- **Explicit idle-time advancement.** `Nise286.AdvanceIdleToCpuCycle(absolute)`
  advances the authoritative cycle counter without executing an instruction (no
  registers, memory or port I/O touched); `ClockedFmpExecutionSession.AdvanceIdleToCpuCycle`
  maps the resulting cycle through the existing `NiseOpnaClockMapper`, advances
  the native OPNA device to the mapped clock and samples IRQ at the boundary.
  The renderer's fake CPU spin loop (`SpinSegment`) was removed.
- **Exact output-frame mapping.** `MapOutputFrameToCpuCycle` computes
  `ceil(frame × cpuClockHz / sampleRate)` with UInt128 integer arithmetic (no
  float, no TimeSpan, no Stopwatch). The coordinator rejects regression; equal
  input is a no-op; if the CPU has already executed past the requested boundary
  the renderer does nothing (never moves either timeline backward).
- **No-progress guard.** Each render iteration records CPU cycles / OPNA clock /
  output frame / driver state before and after; if none change it throws
  `NativeFmpNoProgressException` (message carries exactly those values, no
  retry, no sleep, no arbitrary iteration limits).

## Prompt 8.3 — Nise286 short conditional jumps & native FMP CPU compatibility

### Implemented: the complete short `Jcc rel8` family (0x70–0x7F)

All sixteen short conditional jumps now decode and execute through the real
Nise286 decoder. They share one condition evaluator and one rel8 executor
(`Nise286.ConditionCode`, `EvaluateCondition`, `ExecuteShortConditionalJump`),
so no opcode handler re-implements a flag predicate.

| opcode | mnemonic | condition | taken when |
|--------|----------|-----------|-----------|
| 70 | JO | OF = 1 | OF |
| 71 | JNO | OF = 0 | !OF |
| 72 | JB | CF = 1 | CF |
| 73 | JAE | CF = 0 | !CF |
| 74 | JE | ZF = 1 | ZF |
| 75 | JNE | ZF = 0 | !ZF |
| 76 | JBE | CF or ZF | CF∨ZF |
| 77 | JA | CF = 0 and ZF = 0 | ¬CF∧¬ZF |
| 78 | JS | SF = 1 | SF |
| 79 | JNS | SF = 0 | !SF |
| 7A | JP | PF = 1 | PF |
| 7B | JNP | PF = 0 | !PF |
| 7C | JL | SF != OF | SF≠OF |
| 7D | JGE | SF = OF | SF=OF |
| 7E | JLE | ZF or SF != OF | ZF∨(SF≠OF) |
| 7F | JG | ZF = 0 and SF = OF | ¬ZF∧(SF=OF) |

Semantics (all sixteen):
- The displacement is an **signed 8-bit** rel8 taken relative to the IP
  **after the displacement byte** (the branch base), with 16-bit wrapping IP
  arithmetic; CS is never modified and flags are never cleared or altered.
- Conditions are evaluated exclusively from the emulated flag bits
  (`CF/PF/ZF/SF/OF`) — never from host-language signed comparisons.
- Instruction timing follows the authoritative Nise286 convention
  (`Nise286CycleTimingTests`): exactly one CPU clock tick per executed
  instruction, counted once at `StepExecute`. No taken/not-taken split is
  invented and no per-instruction allocation occurs.

### Result

The real FMP driver boots **past opcode 0x7C** and renders bounded frames with
no `NotImplementedException`, cadence error, `NullReferenceException`, clock
regression or MDSound fallback. Bounded renders (1 / 7 / 64 / 257 frames) are
monotonic in CPU cycles, OPNA master clock and output frame position and pass
in a normal test timeout. Same-platform boot + bounded render is byte-identical
across runs.

### Remaining CPU limitation (real FMP multi-second render)

Boot and bounded-frame rendering succeed. The **multi-second** render,
however, does not complete within a practical test timeout: once the native
session crosses the 500 ms startup gate (`NativeLleFmpPcmSession`), the
driver's one-frame routine drives a large burst of CPU cycles per output frame,
so the CPU/OPNA idle timeline races far ahead of the output frame position and
the renderer must emit a disproportionate number of idle frames to catch up.
This is native-session OPNA/IRQ-clock behaviour and is **outside** the
short-conditional-jump scope; per the prompt's hard restrictions it is
reported rather than patched (no native OPNA timing change, no MDSound
fallback, no default-backend change). Consequently **no successful full-length
native PCM render is claimed** for Prompt 8.3; the fail-closed CLI gate was
updated to assert the native session returns deterministically (never hanging,
never falling back).

### Prompt 8.4 — native FMP timer IRQ scheduling (verified fix)

The post-gate runaway is fixed via two clocked-coordinator invariants:

- **Final CPU-cycle synchronization** (`ClockedFmpExecutionSession`):
  `ExecuteDriverFunction` centralizes all real Nise98 driver execution. After
  each driver call it maps the *actual* final CPU cycle through the
  authoritative mapper, advances the native OPNA device to that clock
  (`SynchronizeDeviceToCpu`), samples the final native IRQ, propagates it to the
  Nise286 input, and re-syncs in a `finally` on exception. The renderer plays no
  direct `CallRunfunctionCall`/`StepExecute`; the boot path is routed through
  the coordinator with post-step sync. Regression: `ClockedDriverSyncTests` +
  `DriverExecutionBoundaryTests`.
- **One driver invocation per continuous IRQ assertion**
  (`NativeLleFmpPcmSession.ServiceIrqAssertions`): a level-high assertion is
  served exactly once; a second invocation requires a deassertion followed by a
  fresh assertion. The IRQ is never cleared in managed code — only the driver's
  own timer acknowledgement (through the native chip) clears it. No recursive
  invocation while the level stays high, no loop until IRQ clears.

**Runaway classification (Prompt 8.4):** A + B together.
`DiagnosticInvocationCount = 4096` for 4096 output frames with IRQ
`True→True` on every call, and `devClk` stayed frozen at `3993600` while the CPU
advanced `+32,901` cycles per frame and `expOpnaFinal` climbed — the device was
never advanced to the final mapped CPU cycle, so the timer's `0x27` clear
write queued in the bus scheduler never flushed and the IRQ could not
deassert. With the fix a 2 s / 48 kHz native render now completes in normal
wall time (no freeze); all managed Release tests pass.

**Remaining blocker — silent native PCM (not claimed):** the FMP driver
schedules its musical frames with the MNDRV *software* FMTimer
(`Nise98.Runtimer()`/`IntTimer()`, advanced once per output sample; the legacy
path fires it ~225× over 2 s to produce sound). The clocked bridge routes the
hardware `0x24–0x27` writes to the real native chip and bypasses that software
timer, so on the native path the driver never advances its frame scheduler and
emits no OPNA writes — `nonzero=0`. Driving it requires exactly the "sample-
based timer" the prompt forbids on this path. The two requirements (nonzero
multi-second native PCM **and** no sample-based timer) are therefore
mutually incompatible for this driver, so **no successful full-length native
PCM render is claimed** and Prompt 8.4 is reported, not over-claimed.

### Sanitizers

- Native release (without sanitizers): full `ctest` green.
- `build-asan` (`ASan`): 20/20 tests pass.
- `build-ubsan` (`MDPLAYER_OPNA_ADAPTER_UBSAN=ON`, adapter+test UBSan): 24/24
  tests pass after fixing a pre-existing signed `int` left-shift UB in the
  native determinism SHA test (`tests/determinism_test.c`: cast each byte to
  `uint32_t` before shifting).
- `build-combined` (`ASan` + adapter `UBSan`): configured and built; see
  combined run result.
