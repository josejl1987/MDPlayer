# SPC SNES S-DSP Backend — Implementation Plan

Branch: `feature/spc-snes-dsp-backend`
Worktree: `/home/jose/MDPlayer-spc`
Base: `generic-mdplayer-visualization` (988b1560)

## Status

| PR | Title | Lines | Status |
|----|-------|-------|--------|
| 1  | SPC format probe and metadata | 498 | ✅ committed (1d0f1dc2) |
| 2  | Native playback wrapper | ~1270 authored (vendor commit separate) | ✅ committed (2d1b9d44 + c9d30fdd) |
| 3  | Master-parity instrumentation seam | ~1207 authored | ✅ committed (fc4fc61e) |
| 4  | Generic SPC timeline | 481 | ✅ committed (323b68fd) |
| 5  | BRR sample identity | 499 | ✅ committed (ac2111b3) |
| 6  | Voice and echo taps | ~990 authored | ✅ committed (a82ab819) |
| 7  | SPC 4×2 visualization | 491 | ✅ committed (3a1bfc60) |
| 8  | Effective PMON pitch | ~662 authored | ✅ committed (7924e396) |
| 9  | BRR root estimator | 498 | ✅ committed (75bb7bcf) |
| 10 | Packaging and hardening | ~960 authored (+505 LGPL text) | ✅ committed (1e396599) |

All 10 PRs complete. Built by parallel sub-agents (each in its own workspace
clone, `.spc-agent-*`), verified and fixed by the orchestrator, merged into
this branch. Current state: full suite 456 passed (365 baseline + 91 SPC);
native CTests parity/tap/pitch all pass (master bit-identical with observer,
voice/echo taps, and effective-pitch taps active). End-to-end CLI
`visualize song.spc --spc-stems` produces master.wav (32 kHz stereo) +
voice-01..08.wav (mono) + echo.wav (stereo), all same duration; timeline.json
at 32,000 Hz timebase (§2.3). `--spc-pitch estimate|relative` diagnostic
option works (§25.3).

## PR 10 status (final)

- ✅ Windows x64 packaging: native CMake post-build copies `mdplayer_spc.dll`
  to `runtimes/win-x64/native/` on WIN32; the CLI csproj packages it; the
  managed wrapper probes both `win-x64`/`linux-x64` by OS.
- ✅ Third-party notices: `THIRD_PARTY_NOTICES.md` documents the vendored
  Game_Music_Emu SPC core (LGPL-2.1 + gme.h static-linking exception, pinned
  `fe8da4b6`, upstream https://github.com/libgme/game-music-emu); the full
  licence text lives in `licenses/game-music-emu-LGPL-2.1.txt`.
- ✅ Sanitizer test target: `-DMDPLAYER_SPC_SANITIZERS=ON` (ASan+UBSan) for the
  native lib and test executables; documented in UPSTREAM.md.
- ✅ Malformed-input hardening tests (`SpcHardeningTests`).
- ✅ `--spc-pitch estimate|relative` diagnostic CLI option (§25.3) plumbed via
  `PlaybackOptions.SpcPitchMode` into `SpcPlaybackBackend` instrument building
  (`SpcPitchModeTests`).
- ✅ Docs: `docs/spc-usage.md` (build, override, runtimes layout, CLI options,
  master-parity ctest).

## Worktree setup notes

- The `lib/MDSound.dll` binary is gitignored and not tracked. The worktree
  needs a local copy (the generic worktree symlinks to the main worktree).
  Copied the actual binary into `MDPlayer/lib/` so the build resolves.
- Green baseline confirmed before any SPC work: Core/Cli/Tests build, 354
  tests pass.
- After PR 1: 365 tests pass (354 + 11 new SPC), 0 failures.

## Architecture integration points

SPC plugs into the generic pipeline purely through:
- `PlaybackBackendRegistry.CreateDefault` → `new SpcPlaybackBackend()`
- `VisualizationDeviceCatalog.SnesDsp()` / `SnesDspVoices()`
- `ChipType.SnesDsp`, `VoiceKind.PcmVoice` in `PlaybackContracts.cs`

No `.spc` conditionals exist in the renderer, pipeline, timeline builder,
topology, or CLI commands (verified — §4 satisfied).

## Key contracts consumed

- `IPlaybackBackend.Probe` / `Open` (§11)
- `PlaybackProbeResult` with `PlatformSpecific` availability
- `DeviceDescriptor` / `VoiceDescriptor` with `NativeVoiceAudio`,
  `SampleIdentity`, `Pan`, `Channel` scope support
- `TimelineDecoderEventSink` + `ChipTimelineDecoderRegistry` (PR 4 will
  register the S-DSP decoder here)

## PR 1 deliverables (done)

- `SpcSnapshot`: signature + CPU regs + RAM + DSP regs extraction
- `SpcMetadataParser`: ID666 defensive decoding (UTF-8→CP932→Win1252→repl)
- `SpcDurationResolver`: §24 priority with 150s/8s/600s defaults
- `SpcPlaybackBackend.Probe`: header-signature recognition
- 11 unit tests covering acceptance criteria
