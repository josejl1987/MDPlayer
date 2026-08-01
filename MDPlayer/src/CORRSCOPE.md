# Corrscope integration

`mdplayer-render visualize` renders the oscilloscope visualization with
[Corrscope](https://github.com/corrscope/corrscope), an external Python tool,
then composites it with the built-in CPU overlay renderer and encodes once.

## Output structure

```
TRACK.visualization/
  timeline.json            # event timeline (notes, rhythm, instruments)
  scope/
    audio/
      master.wav           # stereo 16-bit PCM (Corrscope master audio)
      ym2608-fm1.wav       # mono 16-bit PCM per channel
      ym2608-fm2.wav
      ...
      ym2608-ssg1.wav
      ppz8-01.wav
    corrscope-grid.yaml    # ready-to-run Corrscope project file
  visualization.mp4        # final video: scope grid + musical overlay
```

### Key details

- **Master WAV** is stereo (2 channels) — used as the audio track in the final video.
- **Per-channel stem WAVs** are **mono** (1 channel) — Corrscope expects mono
  channel inputs. The stereo rendered output is downmixed to mono via max-abs.
- All WAVs are signed 16-bit PCM at the configured sample rate (default 44100 Hz).
- The `corrscope.yaml` is a fixed, opinionated Corrscope project file: a stable
  grid of all channels, a correlation trigger tuned for Yamaha FM waveforms,
  and pre-configured layout/renderer settings.
- **Single-pass composition (default):** a small Python bridge
  (`corrscope-frames.py`) drives Corrscope's renderer and streams raw RGB0
  frames to stdout — no intermediate video is encoded. `mdplayer-render` composites
  the musical overlay onto each frame in memory
  (`PanelOverlayRenderer.RenderCompositeFrame`) and sends the composited frames
  to one FFmpeg process that encodes video + audio once. There is no
  intermediate H.264 encode/decode round-trip, and no crop/reassembly
  filter graph.
- **Development defaults** are 1440×720 at 30 fps with `libx264 ultrafast` and
  Corrscope antialiasing off. Use `--final-quality` for 1080p60 with
  `veryfast`/`crf 18` and Corrscope antialiasing on.
- **Encoding:** `--encoder libx264` is the default explicit software path.
  `--encoder nvenc` selects NVIDIA `h264_nvenc` for the final encode only;
  initialization errors are reported and never fall back silently to libx264.
  NVENC presets are derived from the same quality setting: dev → `p1`/`cq 20`,
  final → `p4`/`cq 18`.

## Usage

### Stems + YAML only (no Corrscope dependency)

```bash
mdplayer-render visualize track.ovi --fmp-com FMP.COM --stems-only
```

This produces `TRACK.visualization/timeline.json` + `scope/audio/*.wav` +
`scope/corrscope-grid.yaml`. Render the scope grid separately:

```bash
pip install corrscope
corr TRACK.visualization/scope/corrscope-grid.yaml -o TRACK.visualization/scope/corrscope-grid.mp4
```

### One-command video (Corrscope required)

```bash
mdplayer-render visualize track.ovi --fmp-com FMP.COM
```

If Corrscope and FFmpeg are available, this produces stems + YAML, streams
Corrscope frames through the single-pass bridge, composites the overlay in
memory, and encodes the final `visualization.mp4`.

### Options

| Flag | Description |
|------|-------------|
| `--stems-only` | Render stems + corrscope YAML, skip Corrscope and video composition |
| `--corrscope <path>` | Path to the `corr` binary (override PATH lookup) |
| `--ffmpeg <path>` | Path to the FFmpeg binary (override PATH lookup) |
| `--video <path>` | Final MP4 path (default: `outputDir/visualization.mp4`) |
| `--width` / `--height` / `--fps` | Video geometry and frame rate (default 1440×720 @ 30) |
| `--final-quality` | 1080p60, `veryfast`/`crf 18`, Corrscope antialiasing on |
| `--encoder libx264\|nvenc` | Explicit final-encode encoder (default `libx264`) |
| `--font <path>` | TrueType/OpenType font with CJK coverage for overlay text |
| `--tool-timeout-minutes N` | Corrscope/FFmpeg timeout (default: 60) |
| `--overwrite` | Replace existing timeline/video outputs |

## Architecture

```
OVI → per-channel stem export (MaskedChipSink) → corrscope-grid.yaml
                                                      ↓
timeline capture → PanelOverlayRenderer (chrome once) ← corrscope-frames.py
                                                      ↓       (raw RGB0 pipe)
                        in-memory composite (RenderCompositeFrame)
                                                      ↓
                                              one FFmpeg encode → visualization.mp4
```

Corrscope (BSD-2-Clause) handles all oscilloscope rendering: triggering, layout,
labels, scaling and antialiasing. This codebase contributes the synchronized
per-channel stem extraction, the fixed grid configuration, the musical overlay
renderer, the single-pass Python bridge, and the in-memory composition pipeline.
