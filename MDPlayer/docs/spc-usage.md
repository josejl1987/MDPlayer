# SPC SNES S-DSP backend — usage

The SPC backend (`SpcPlaybackBackend`) plugs into the generic playback
pipeline: it recognizes `.spc` files by their **header signature**, not by
extension, renders at the native 32,000 Hz S-DSP rate through the vendored
Game_Music_Emu SPC core, and exposes the SNES S-DSP device
(`VisualizationDeviceCatalog.SnesDsp()`).

## Building the native library

Requires CMake ≥ 3.16, a C++11 compiler and the `patch` utility.

```
cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build
cmake --build native/MDPlayer.SpcNative/build --config Release
```

The post-build step copies the library into the managed runtimes layout:

| OS      | Output                               | Destination                                  |
|---------|--------------------------------------|----------------------------------------------|
| Linux   | `build/libmdplayer_spc.so`           | `runtimes/linux-x64/native/libmdplayer_spc.so` |
| Windows | `build/mdplayer_spc.dll` (WIN32)     | `runtimes/win-x64/native/mdplayer_spc.dll`     |

Built native library files under `runtimes/*/native/` are gitignored while
their `README.md` files remain tracked; the CLI project packages whichever
library exists (`Condition="Exists(...)"`).

### Master-parity test (ctest)

The PR 3 parity guarantee (§5.3) is enforced natively: the instrumented render
(with the voice observer active) must be bit-identical to a plain GME render.

```
ctest --test-dir native/MDPlayer.SpcNative/build --output-on-failure
```

### Sanitizer test builds (§33)

```
cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build-asan \
      -DMDPLAYER_SPC_SANITIZERS=ON -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build native/MDPlayer.SpcNative/build-asan --config Release
ctest --test-dir native/MDPlayer.SpcNative/build-asan --output-on-failure
```

Adds `-fsanitize=address,undefined -fno-omit-frame-pointer -g` to the library
and every native test executable (GCC/Clang only; recommended CI variants:
ASan, UBSan-only, Release — see `native/MDPlayer.SpcNative/UPSTREAM.md`).

## MDPLAYER_SPC_NATIVE override

The managed wrapper (`SpcNativeSession`) locates the library in this order:

1. `MDPLAYER_SPC_NATIVE` environment variable (full path to the library; a set
   but nonexistent path is treated as missing → actionable error).
2. `runtimes/<rid>/native/` under `AppContext.BaseDirectory`, where `<rid>` is
   `win-x64` on Windows (`mdplayer_spc.dll`) and `linux-x64` elsewhere
   (`libmdplayer_spc.so`), plus the base directory itself.

## CLI options relevant to SPC

SPC inputs are accepted by both the generic render and visualize commands:

```
mdplayer-render render song.spc --output song.wav --duration 90
mdplayer-render visualize song.spc --duration 90 --stems-only
```

The render command writes a 32,000 Hz stereo master WAV. The visualize command
also writes `timeline.json` and can compose the video overlay.

The following options apply to the generic visualize command:

| Option | Meaning |
| ------ | ------- |
| `--duration SECONDS` | Maximum capture duration (safety cap 300 s default; SPC metadata/default fallbacks per §24). |
| `--fade SECONDS` | Linear master fade after the resolved duration (default 5). |
| `--spc-pitch estimate\|relative` | **Diagnostic option (§25.3).** `estimate` (default) runs the PR 9 BRR root estimator so instruments carry an estimated musical root; `relative` skips root estimation — instruments keep PitchAccuracy `relative` and EstimatedRootHz stays null. Flows through `PlaybackOptions.SpcPitchMode`; the renderer itself has no `.spc` conditionals. |
| `--spc-stems` | Include SPC voice and echo taps in the visualization output. |
| `--stems-only` | Render stems + corrscope YAML, skip video composition. |

`--spc-stems` (voice/echo taps) is available on the visualize command.

Example:

```
mdplayer-render visualize song.spc --duration 90 --fade 4 --spc-pitch relative
```

## Docs

- `native/MDPlayer.SpcNative/UPSTREAM.md` — vendored core pin, PR 3 patch, PR 10 packaging/sanitizers.
- `THIRD_PARTY_NOTICES.md` and `licenses/game-music-emu-LGPL-2.1.txt` — licensing.
- `docs/spc-implementation-plan.md` — per-PR implementation status.
