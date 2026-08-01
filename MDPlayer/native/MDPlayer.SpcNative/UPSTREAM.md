# MDPlayer.SpcNative — upstream vendoring

This directory vendors the SPC core of **Game_Music_Emu** unchanged, plus the
small C ABI wrapper built on top of it. The wrapper sources (`include/`,
`src/`, `patches/`, `tests/`) are authored by MDPlayer; everything under
`upstream/` is imported byte-identical from the pinned upstream commit and
**must never be hand-edited**. PR 3 and PR 6 add build-time instrumentation
patches (see "PR 3 local modifications" and "PR 6 local modifications" below)
applied to a *copy* of the upstream tree in the build directory — the vendored
files on disk stay byte-identical.

## Repository and pin

- Repository URL: https://github.com/libgme/game-music-emu
- Branch policy: pinned to one commit; never a floating branch.
- Pinned commit SHA: **`fe8da4b6d3876d7542c2fb69d94487e19836d678`** (game-music-emu master, 2026-07-31 vendor).
  (The PR 2 authoring environment had no network access; the pin was resolved
  and the files vendored byte-identical at integration time. The vendor
  commit records this SHA.)
- Licence: **LGPL-2.1** (Game_Music_Emu is distributed under the GNU LGPL v2.1
  with the exception in `gme/gme.h` permitting static linking; see COPYING at
  the upstream repository root).

## Imported files

All paths are relative to the upstream repository root (`gme/` subdirectory).
Every `.cpp` listed below is compiled into `libmdplayer_spc.so`; headers are
included via `upstream/` on the include path.

| File | Role |
| --- | --- |
| `gme/Spc_Emu.h`, `gme/Spc_Emu.cpp` | **Compiled.** High-level SPC emulator (`Music_Emu` subclass); our wrapper drives it via the stable `Music_Emu` API (`load_mem`, `start_track`, `play`). |
| `gme/Snes_Spc.h`, `gme/Snes_Spc.cpp` | **Compiled.** SPC-700 + S-DSP system emulator. |
| `gme/Spc_Cpu.h`, `gme/Spc_Cpu.cpp` | **Compiled.** SPC-700 CPU core. |
| `gme/Spc_Dsp.h`, `gme/Spc_Dsp.cpp` | **Compiled.** SNES S-DSP core. |
| `gme/SPC_Filter.h`, `gme/SPC_Filter.cpp` | **Compiled.** Output-stage equalizer/surround used by Snes_Spc. |
| `gme/Music_Emu.h`, `gme/Music_Emu.cpp` | **Compiled.** Base emulator class (tempo, mute, sample rate, play loop). |
| `gme/Gme_File.h`, `gme/Gme_File.cpp` | **Compiled.** Base file/load_mem handling. |
| `gme/Data_Reader.h`, `gme/Data_Reader.cpp` | **Compiled.** In-memory reader used by `load_mem`. |
| `gme/Blip_Buffer.h`, `gme/Blip_Buffer.cpp` | **Compiled.** Blip buffer (Music_Emu dependency). |
| `gme/Multi_Buffer.h`, `gme/Multi_Buffer.cpp` | **Compiled.** Multi-channel buffer (Music_Emu dependency). |
| `gme/Effects_Buffer.h`, `gme/Effects_Buffer.cpp` | **Compiled.** Effects buffer (Music_Emu dependency). |
| `gme/blargg_common.h` | Shared blargg types/macros. |
| `gme/blargg_config.h` | Feature configuration. |
| `gme/blargg_source.h` | Debug/source helpers. |
| `gme/blargg_endian.h` | Endian helpers (needs BLARGG_LITTLE_ENDIAN/BIG define from CMake). |
| `gme/Fir_Resampler.h` | Resampler used by Spc_Emu output. |
| `gme/M3u_Playlist.h` | Playlist handling referenced by Music_Emu/Gme_File. |
| `gme/gme.h` | Public GME header (licence exception note). |

The current upstream tree consolidated `blargg_errors.h`/`blargg_vector.h`
into the remaining `blargg_*.h` headers; no separate files are needed for
those. `gme.h`/`gme.cpp` (the full-library C API and format registry) and
every non-SPC emulator are intentionally **not** imported: `gme.cpp`'s
`gme_new_emu` dispatcher references every format, which would force the whole
library in. The SPC core closure above is the minimal self-consistent set.

## Build

```
cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build
cmake --build native/MDPlayer.SpcNative/build --config Release
ctest --test-dir native/MDPlayer.SpcNative/build --output-on-failure
```

Produces `native/MDPlayer.SpcNative/build/libmdplayer_spc.so` on Linux and
`.../mdplayer_spc.dll` on Windows. The CMake post-build step (PR 10) copies it
to `runtimes/linux-x64/native/libmdplayer_spc.so` (non-Windows) or
`runtimes/win-x64/native/mdplayer_spc.dll` (WIN32), the locations the managed
wrapper probes via `AppContext.BaseDirectory`, with an `MDPLAYER_SPC_NATIVE`
environment-variable override that works on both platforms.

PR 3/PR 6/PR 8 build step: before compiling, CMake copies `upstream/` into
`build/upstream_patched/` and applies `patches/0001-voice-capture.patch`
there (`patch -p1 -d build/upstream_patched -i patches/0001-voice-capture.patch`),
then `patches/0002-voice-echo-capture.patch` and
`patches/0003-effective-pitch.patch` the same way, then compiles the upstream
`.cpp` files and the wrapper against that copy. The vendored `upstream/` tree
on disk is never modified. Requires the standard `patch` utility (checked at
configure time).

Note on upstream TU layout: if the pinned tree bundles `Spc_Cpu.cpp` /
`Spc_Dsp.cpp` / `SPC_Filter.cpp` into another `.cpp` via `#include`, remove the
bundled files from `SPCNATIVE_UPSTREAM_SOURCES` in `CMakeLists.txt` to avoid
multiple-definition errors. Check after vendoring:
`git show <pin>:gme/Snes_Spc.cpp | grep -n '#include ".*\.cpp"'`.

## PR 10 packaging and sanitizer test builds (spec §33)

- **Packaging**: the post-build copy step is platform-aware. On `WIN32` the
  built `mdplayer_spc.dll` is copied to
  `runtimes/win-x64/native/mdplayer_spc.dll`; on all other platforms
  `libmdplayer_spc.so` is copied to `runtimes/linux-x64/native/`. The managed
  `SpcNativeSession` picks the current OS's runtime identifier
  (`OperatingSystem.IsWindows()`), so the same wrapper binary serves both
  layouts.
- **Sanitizers**: configure with `-DMDPLAYER_SPC_SANITIZERS=ON` to build
  `mdplayer_spc` and every native test executable
  (`mdplayer_spc_parity_test`, and any future tap/pitch test executables added
  by PR 6/8) with `-fsanitize=address,undefined -fno-omit-frame-pointer -g`
  (GCC/Clang only). `CMAKE_CXX_FLAGS` and the shared/exe linker flags are set
  accordingly. Recommended CI variants:
  1. **ASan** — `-DMDPLAYER_SPC_SANITIZERS=ON -DCMAKE_BUILD_TYPE=RelWithDebInfo`
  2. **UBSan-only** — set the flags to `-fsanitize=undefined` (edit
     `CMakeLists.txt` `MDPLAYER_SPC_SANITIZE_FLAGS`).
  3. **Release** — default `OFF`, plain `-DCMAKE_BUILD_TYPE=Release`.
  Run the suite with `ctest --test-dir native/MDPlayer.SpcNative/build
  --output-on-failure` (note: ASan builds need `ASAN_OPTIONS=detect_leaks=0`
  on some CI images because the test processes exit via `return`).


## Update procedure

1. `git clone https://github.com/libgme/game-music-emu.git /tmp/gme`
2. `cd /tmp/gme && git checkout <commit>` — pin a specific commit; never
   `master`.
3. Record the pin: `git rev-parse HEAD` → fill in `__PINNED_SHA__` above.
4. Copy the files listed above, unchanged, into `upstream/`.
5. Verify byte-identity against the pinned tree, e.g. `diff -r /tmp/gme/gme
   upstream` (or `sha256sum` per file). Do **not** hand-edit any vendored file.
6. Run the master-output parity test (below).
7. Commit the import as a separate commit:
   `chore: vendor upstream SPC core @ <sha>`.

## Master-output parity test

`mdp_spc_render` (with `enable_voice_pcm = 0`, `enable_echo_pcm = 0`) must
produce a 32,000 Hz stereo int16 master stream identical to the reference GME
SPC path on the same `.spc` file:

```
Spc_Emu e; e.set_sample_rate( 32000 ); e.load_mem( data, size );
e.start_track( 0 ); e.play( frames, out );   // frames = stereo frames
```

Byte-for-byte PCM equality is required. The managed layer (MDPlayer) applies
only the linear fade to the master after the native core; the native core must
never resample or post-process. Determinism is asserted by
`SpcNativeSessionTests.Render_SameInputTwice_IsByteIdentical` — two renders of
the same input must produce identical WAV files.

PR 3 adds a second, native parity test (`tests/parity_test.cpp`, registered
with CTest as `mdplayer_spc_parity`): it renders the same synthetic SPC once
through a plain `Spc_Emu` (no observer) and once through the C ABI with the PR
3 voice observer and per-block event capture active, and asserts the masters
are byte-identical. This is the CRITICAL requirement of spec §5.3.

## PR 3 local modifications

PR 3 needs to observe the S-DSP voice state for `mdp_spc_get_voice_state` and
per-block capture events. Upstream exposes only `Spc_Dsp::read(addr)` (the raw
register byte); the per-voice *emulated* state (`voice_t`: envelope mode/level,
BRR cursor, KON delay) is private. Instead of hand-editing the vendored files,
PR 3 applies a build-time patch (`patches/0001-voice-capture.patch`) to a copy
of `upstream/` in the build tree (`build/upstream_patched/`).

The patch adds only **read-only inline accessors**; it contains no logic that
writes or reorders emulation:

- `Spc_Dsp.h` — `const voice_t* voices() const` and `const uint8_t* regs() const`
  (the S-DSP's per-voice emulated state and its 128 register bytes).
- `Snes_Spc.h` — `Spc_Dsp& dsp_ref() { return dsp; }` (reach the private S-DSP).
- `Spc_Emu.h` — `Spc_Dsp& dsp_ref()` (via `apu.dsp_ref()`) and
  `Snes_Spc& apu_ref() { return apu; }` (to reach the shared 64K RAM through the
  existing public `Snes_Spc::smp_ram()`, used to resolve DIR sample pointers).

The observer (`src/spc_capture.cpp`) only reads through these accessors after a
render block. Because it never writes to the emulator and never changes
execution order, the instrumented master output is bit-identical to an
uninstrumented render; the `mdplayer_spc_parity` CTest enforces this.

Update procedure: if the upstream pin changes, re-apply the patch by hand to
the new tree (or regenerate it with `diff -u` against the new headers) and
re-run the parity test.

## PR 6 local modifications

PR 6 captures per-voice post-envelope PCM (spec §8.2) and the audible
echo-return signal (spec §8.4) for the SPC voice/echo stems. These signals are
computed inside `Spc_Dsp::run()` from private locals that upstream does not
expose, so PR 6 applies a second build-time patch
(`patches/0002-voice-echo-capture.patch`) to the build-tree copy, after 0001.

The patch adds **caller-owned, off-by-default capture hooks** that are pure
writes to caller memory; when disabled (NULL destinations) the DSP executes
exactly the same instruction stream as the unpatched core:

- `Spc_Dsp.h` — `set_voice_taps(sample_t*[voice_count], int stride)`,
  `set_echo_tap(sample_t*)` and `clear_taps()`; new `state_t` fields
  `voice_taps[8]`, `voice_taps_on`, `echo_tap`, `tap_stride`, `tap_sample`
  (placed after `extra` so `Spc_Dsp::load()`'s memset never clears them).
- `Spc_Dsp.cpp` —
  - `set_output()` resets the per-output-run `tap_sample` counter to 0.
  - In the voice loop at the `// Output` section (where `output` is the
    post-envelope sample, after `(output * env) >> 11`), when `voice_taps_on`
    the §8.2 scope sample `output * max(abs(VOLL),abs(VOLR)) / 127` (VOLL/VOLR
    = the SIGNED voice volume registers, `v_voll`/`v_volr`) is written into
    `voice_taps[voice][tap_sample * tap_stride]` as a clamped int16.
  - In the `Sound out` section, when `echo_tap` is set the §8.4 echo return
    `(echo_in_l * evoll) >> 14` / `(echo_in_r * evolr) >> 14` (echo_in is the
    FIR-filtered echo input, evoll/evolr are the echo volumes) is written into
    `echo_tap[tap_sample*2]` / `echo_tap[tap_sample*2+1]` as clamped int16.
  - The loop body advances `tap_sample` once per DSP output pair.

The hooks never modify `l`/`r`/`main_out`/`echo_out`/`v` state, never change
execution order, and never clamp differently. Tap capture is therefore a pure
side-effect on caller memory: the instrumented master output is bit-identical
to an uninstrumented render. This is enforced by `tests/tap_test.cpp`
(CTest name `mdplayer_spc_tap`), which also asserts voices 0/1 are non-silent,
voices 2..7 stay zero, and the echo return stays zero when EON is disabled.

The wrapper (`src/spc_taps.cpp`) arms these destinations from a `Spc_Emu*`
before each render block and clears them afterwards; the ABI wires them through
`mdp_spc_audio_buffers.voice[i]` / `.echo` when the open options'
`enable_voice_pcm` / `enable_echo_pcm` are set.

Update procedure: if the upstream pin changes, re-apply both patches by hand to
the new tree (or regenerate with `diff -u`) and re-run `mdplayer_spc_parity`
and `mdplayer_spc_tap`.

## PR 8 local modifications

PR 8 needs the S-DSP's *effective* pitch — the register pitch after the
pitch-modulation adjustment of spec §10.2 — per rendered sample, so the
observer can report it and PITCH_CHANGED can be emitted on effective-pitch
transitions (§9.2/§13.6). This is computed inside `Spc_Dsp::run()` and
normally discarded, so PR 8 adds a second build-time patch
(`patches/0003-effective-pitch.patch`, applied after 0001 to the same
build-tree copy) that instruments it:

- `Spc_Dsp.h` — a per-voice, OPTIONAL, off-by-default tap:
  `int* effective_pitch_out[voice_count]` in `state_t` (NULL = disabled) and a
  public `void set_effective_pitch_out(int voice, int* out)` accessor.
- `Spc_Dsp.cpp` — in the voice loop, right where `pitch` is fully computed
  (after the PMON adjustment, before the "// Apply pitch" line), a pure store
  `if (tap) *tap = pitch;`. When the tap is NULL the DSP executes byte-identical
  instructions, so the master output is bit-identical to an uninstrumented
  render (enforced by the `mdplayer_spc_pitch` CTest).

The wrapper (`src/spc_pitch.h/.cpp`) arms the taps around each render block
(`begin_block` before `emu->play()`, `end_block` after), keeps one int cell per
voice holding the block's LAST-sample effective pitch, and the observer
(`src/spc_capture.cpp`) fills `effective_pitch` from those cells when a block
was rendered, falling back to the register pitch otherwise. `mdp_spc_voice_state`
gains an appended `effective_pitch` field (ABI-compatible) and PITCH_CHANGED
uses effective values (`param0` = new effective pitch, `param1` = previous).
`MDP_SPC_API_VERSION` is bumped to 3.

Update procedure: if the upstream pin changes, re-apply both patches by hand to
the new tree (or regenerate with `diff -u`) and re-run both CTests.

## PR 2 scope notes (deviations recorded honestly)

- `mdp_spc_copy_ram` / `mdp_spc_copy_dsp_registers` return the validated
  initial snapshot state (RAM at file offset 0x100, DSP registers at
  0x10100). Syncing the post-render emulated state is deferred beyond PR 3
  (voice/state work in PR 3 covers observation only, not RAM sync).
- PR 3 implements `mdp_spc_get_voice_state` and per-block capture events
  (KEY_ON, RELEASE_START, VOICE_END, SOURCE/PITCH/VOLUME/NOISE/PITCH_MOD/
  ECHO_SEND_CHANGED) — the PR 2 stubs are gone.
- PR 3 keeps the PR 2 ABI layout: new voice-state fields and the
  `event_overflow` render-result field are appended, `MDP_SPC_API_VERSION`
  bumped to 2.
- PR 2 needs master audio only; voice/echo PCM buffers were accepted but may
  be NULL. PR 6 implements them: when the open options enable_voice_pcm /
  enable_echo_pcm are set, the S-DSP fills them per render block (spec §8.2/§8.4).
  Without those options the buffers stay untouched and the master is
  bit-identical (spec §5.3).
