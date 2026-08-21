# Third-Party Notices

This project uses or references the following third-party software.

## YM2608-LLE (native YM2608/OPNA backend)

- **Licence:** GPL-2.0-or-later
- **Source:** https://github.com/tildearrow/furnace (`extern/YM2608-LLE`,
  original core by nukeykt)
- **Pinned Furnace commit:** `3bdfc824fb7d2e813852f6fcfa482d8ea999588a`
- **Usage:** The native YM2608-LLE backend contains GPL-2.0-or-later code. It
  is not yet packaged in this stage. The exact Furnace commit is pinned and the
  vendored core files are kept byte-identical under
  `native/MDPlayer.OpnaNative/upstream/furnace-ym2608-lle`. See
  `native/MDPlayer.OpnaNative/UPSTREAM.md` for the pin, per-file hashes and the
  update procedure. The full licence text is in
  `native/MDPlayer.OpnaNative/LICENSES/GPL-2.0-or-later.txt`.
- **Note:** No compatibility claim is made here about the complete binary
  distribution until packaging of the GPL-covered backend is addressed.

## Game_Music_Emu (SNES SPC core)

- **Licence:** LGPL-2.1 with the static-linking exception in `gme/gme.h`
- **Source:** https://github.com/libgme/game-music-emu
- **Pinned commit:** `fe8da4b6d3876d7542c2fb69d94487e19836d678`
- **Usage:** The SPC backend vendors the SPC core (`gme/Spc_Emu.cpp`,
  `Snes_Spc.cpp`, `Spc_Cpu.cpp`, `Spc_Dsp.cpp`, `SPC_Filter.cpp`,
  `Music_Emu.*`, `Gme_File.*`, `Data_Reader.*`, `Blip_Buffer.*`,
  `Multi_Buffer.*`, `Effects_Buffer.*`, `Fir_Resampler.h`, `M3u_Playlist.h`
  and the shared blargg headers) byte-identical into
  `native/MDPlayer.SpcNative/upstream/` and compiles it into the
  `mdplayer_spc` native library. The full library text is in
  `licenses/game-music-emu-LGPL-2.1.txt`; see
  `native/MDPlayer.SpcNative/UPSTREAM.md` for the pin, the update procedure and
  the build-time instrumentation.
- **Local modifications:** PR 3 applies `patches/0001-voice-capture.patch` at
  build time to a *copy* of the vendored tree
  (`build/upstream_patched/`); the patch only adds read-only inline accessors
  for voice-state capture and never changes emulation. The vendored files on
  disk stay byte-identical. PR 10 packaging/sanitizer changes live entirely in
  `CMakeLists.txt`/managed code and add no further upstream edits. (0002/0003
  are reserved for future instrumentation patches; none are applied today.)

## Corrscope

- **Licence:** BSD-2-Clause
- **Source:** https://github.com/corrscope/corrscope
- **Usage:** Corrscope is used as an external tool for oscilloscope rendering.
  It is not vendored; it must be installed separately. The renderer reads
  Corrscope's raw RGB frame output via a bridge script.

## FFmpeg

- **Licence:** LGPL-2.1+ (or GPL, depending on build configuration)
- **Source:** https://ffmpeg.org
- **Usage:** FFmpeg is used as an external tool for video encoding. It is not
  vendored; it must be installed separately.

## music21

- **Licence:** BSD-3-Clause
- **Source:** https://github.com/cuthbertLab/music21
- **Usage:** Optional external Python dependency for offline symbolic music
  analysis. It is not vendored and its corpus files are not distributed.

## .NET Runtime

- **Licence:** MIT
- **Source:** https://github.com/dotnet/runtime
- **Usage:** The project targets .NET 8.0+.

## MIDITrail

- **Licence:** BSD-3-Clause
- **Source:** https://github.com/wdmss/MIDITrail-Windows and https://github.com/wdmss/MIDITrail-macOS
- **Pinned releases:** Windows 1.4.1 and macOS 2.1.0.
- **Usage:** The vendored scene/object and graphics-abstraction sources under
  `third_party/miditrail/upstream/` are used as the visual-behavior reference
  for the embedded renderer. MIDITrail's application shell, MIDI I/O,
  synthesizer, realtime player and platform UI are not reused. Provenance,
  archive hashes and the imported component list are in
  `third_party/miditrail/UPSTREAM.md`; the full BSD text is in
  `third_party/miditrail/LICENSE`.
- **Resources:** No MIDITrail artwork, fonts, soundfonts or other binary
  resources are redistributed by this import. Any future resource addition
  requires a separate license audit.

## Kiva (design reference)

- **Licence:** Don't Be a Dick (incompatible — no code used)
- **Source:** https://github.com/SayuriForce/Kiva
- **Usage:** Design reference only.

## Pianola (design reference)

- **Licence:** AGPL-3.0 (incompatible — no code used)
- **Usage:** Design reference only.

## SeeMusic (design reference)

- **Licence:** Proprietary
- **Usage:** Visual design reference only. No code or assets used.
