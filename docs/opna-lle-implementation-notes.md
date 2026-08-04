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
