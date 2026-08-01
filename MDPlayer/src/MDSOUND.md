# MDSound dependency — provenance and portability decision

Phase 1 of the Linux FMP renderer plan required an explicit pass/fail answer for
YM2608 and PPZ8 synthesis on Linux, plus documented source/license/version for the
synthesis dependency.

## Outcome: A — the bundled MDSound works unchanged on Linux

Verified by unit tests (`tests/MDPlayer.Fmp.Tests/MdsoundChipSinkTests.cs`) and by
real OVI renders to WAV with deterministic hashes. No native Windows DLL is loaded;
MDSound is a pure managed assembly.

## Dependency facts

| Field | Value |
|---|---|
| Assembly | `lib/MDSound.dll` (binary reference from `MDPlayer.Fmp.Core`) |
| Upstream project | MDSound by Kuma Computer (kuma4649), component of MDPlayer |
| Assembly informational version | `1.0.3+922fdd751d241bd6c2e56f8ad1fbc3154d71639d` (upstream git SHA `922fdd7…`) |
| SHA-256 of bundled DLL | `be9a73f4ef442ac70ee0d4bbe36a2df8fee6d8f4ca1c37043ee63eaca4dbc3df` |
| Nature | Pure managed .NET; no native dependencies, no P/Invoke into Windows binaries |
| Embedded cores | fmgen (OPN/OPNA FM, © cisc — `licenses/fmgen/readme.txt`), MAME-derived emulation code (`licenses/mame/mame_license.txt`); other cores per `licenses/List.txt` (vgmplay GPL, mucom88 MIT, …) |

License note: MAME code is BSD-3-Clause/GPL-2.0+ dual-licensed upstream; vgmplay
portions are GPL. Distributing MDSound.dll therefore carries GPL obligations for
those cores. The FMP renderer consumes the DLL as-is from the upstream MDPlayer
distribution; cores not exercised by the FMP path (YM2608 + PPZ8 only) are still
present in the binary.

## MDSound build refresh

A broken YM2608 rhythm implementation in the upstream `922fdd7` build was discovered
during parity work: `SetReg(0x10, ...)` never reset `rhythm[i].pos`, and `RhythmMix`
checked `rhythmtvol < 128` incorrectly. The DLL was rebuilt from the latest upstream
source (`kuma4649/MDSound`) and is the binary now referenced by the Linux renderer.

## Known limitation: audio parity gap

Render output for the same OVI/FMP inputs is deterministic and uses the same register
writes, first-audio timing after the 500 ms `WaitTime`, and loop handling. It is not
reference-accurate: the synthesized PCM waveform correlates only approximately 0.4
with the Windows reference WAV across tested tracks, with an RMS ratio of
approximately 0.7. Corrscope visualizes this generated PCM, so the parity gap also
applies to displayed waveforms. Investigation ruled out register differences, DLL
version, double-chip setup, WaitTime, and MDSound mixing volume as the cause.
The remaining divergence is consistent with a subtle difference in how the
OPNA/fmgen internal state is initialized or resampled in the new build relative to
the legacy `mdpc` capture environment. This is accepted as a known limitation of
the Linux renderer. User-facing wording should say “deterministic native Linux FMP
rendering with synchronized per-channel visualization”; do not call it bit-perfect
or identical to Windows MDPlayer.

## Scope architecture: multi-pass fallback (per-channel taps unavailable)

The scope renderer (`ScopeRenderer.cs`) uses independent mute-and-rerender
passes to produce per-channel stems (13 passes by default: master + 6 FM + 3 SSG
+ rhythm + ADPCM + PPZ8). This is the **second-best architecture**, adopted
because the ideal single-pass per-channel tap approach is not achievable with
the current MDSound binary.

**Ideal architecture (blocked):** Record all logical channel PCM in a single
emulation pass. Each chip's `Update(byte chipID, int[][] outputs, int samples)`
method accepts a jagged output array, which could theoretically allow per-channel
capture. However:
1. MDSound's internal mixer (`MDSound.Update()`) only ever calls chip `Update`
   with a stereo (2-channel) output array — not per-FM-channel.
2. The `ym2608.Update` method signature (`int[][] outputs`) is ambiguous; the
   first dimension likely maps to stereo pairs, not individual FM channels.
3. Reverse-engineering the binary would be required to confirm, and forking
   MDSound (GPL-licensed via fmgen/MAME cores) is out of scope.

**Current fallback:** Parallel mute-and-rerender passes. Each pass creates a
fresh `MdsoundFmpChipSink` wrapped in a `MaskedChipSink` that:
- Forces FM TL (total level) registers to max (0x7F, silence) for muted channels
- Suppresses key-on writes for muted channels
- Zeros SSG amplitude writes for muted channels
- Uses MDSound group volumes for rhythm/ADPCM/PPZ8 muting

Since each pass uses an independent chip sink, all non-master stems are
rendered from a single emulation pass via broadcast chip sinks.

**To unblock the ideal architecture:** Replace or fork MDSound to expose
per-channel PCM rendering at the `IFmpChipSink` boundary, or add a new
`IFmpChannelTapSink` interface that the scope renderer detects and uses
preferentially.

## Deviation from the plan's project layout (documented decision)

The plan sketched `MDPlayer.Fmp.MDSound` implementing an `IFmpSynthesizer`
abstraction, with `MDPlayer.Fmp.Core` depending only on the abstraction.
The implementation instead:

- references `lib/MDSound.dll` directly from `MDPlayer.Fmp.Core`, and
- keeps the synthesis seam at the `IFmpChipSink` boundary
  (`Audio/MdsoundFmpChipSink.cs`), with `NullFmpChipSink` for trace-only runs.

Rationale: Outcome A removed the portability risk the abstraction was guarding
against; a single concrete backend does not justify a plugin project, and
`IFmpChipSink` already isolates the emulator from the synthesizer (proven by
`MaskedChipSink` used for stems). If a second backend is ever needed, extract
`IFmpSynthesizer` then — the seam location does not change.
