#!/usr/bin/env bash
set -euo pipefail

# mdplayer-visualize.sh — One-command chip-tune → visualization video renderer
# Usage: ./mdplayer-visualize.sh <track> [options ...]
#
# Supported formats:
#   FMP family:       .ovi .opi .ozi .mpi .mvi .mzi
#   Register-log family: .vgm .vgz .s98 .xgm .mdx .mid .midi
#
# Options are forwarded to `mdplayer-render visualize` (e.g. --loops 4, --width 1920).
# Defaults: 2 loops, 5s fade, 0.5s tail, 300s max duration, 30fps, 1440×720.
# Use --final-quality for 1080p60 with higher-quality encoding.
#
# Produces: <track>.visualization/
#   - timeline.json    (event timeline)
#   - visualization.mp4 (final composited video with overlay)
#   - scope/           (intermediate: stems + corrscope YAML)
#
# Tool paths can be overridden via environment variables:
#   MDPLAYER_RENDER  mdplayer-render executable (default: repo Release build)
#   FMP_RENDER       legacy alias for MDPLAYER_RENDER
#   FMP_COM          FMP.COM path (required only for FMP-family formats)
#   CORR             Corrscope executable
#   FFMPEG           FFmpeg executable

ROOT="$(cd "$(dirname "$0")" && pwd)"
MDPLAYER_RENDER="${MDPLAYER_RENDER:-${FMP_RENDER:-$ROOT/MDPlayer/src/MDPlayer.Fmp.Cli/bin/Release/net8.0/mdplayer-render}}"
FMP_COM="${FMP_COM:-$ROOT/MDPlayer/MDPlayerx64/bin/Debug/net8.0-windows7.0/win-x64/publish/FMP.COM}"
CORR="${CORR:-$ROOT/_venv/bin/corr}"
FFMPEG="${FFMPEG:-/usr/bin/ffmpeg}"

usage() {
    cat <<'EOF'
Usage: mdplayer-visualize.sh <track> [visualize-options ...]

Examples:
  mdplayer-visualize.sh XA2028.OVI                 # default settings
  mdplayer-visualize.sh XA2047.OVI --loops 4 --width 1920 --height 1080
  mdplayer-visualize.sh song.vgm --fps 30 --tool-timeout-minutes 20
  mdplayer-visualize.sh music.mid --backend mdplayer --scopes channel

Supported input formats:
  FMP family:            .ovi .opi .ozi .mpi .mvi .mzi
  Register-log family:   .vgm .vgz .s98 .xgm .mdx .mid .midi

Common options (both format families):
  --loops COUNT            Loop count (default: 2)
  --fade SECONDS           Fade duration (default: 5)
  --tail SECONDS           Tail duration (default: 0.5)
  --duration SECONDS       Capture exactly this maximum duration
  --max-duration SECONDS   Song-end safety cap (default: 300)
  --ssg-gain-db DB         Adjust YM2608 SSG/PSG audio level (default: 0)
  --width PIXELS           Video width (default: 1440)
  --height PIXELS          Video height (default: 720)
  --fps RATE               Frame rate (default: 30)
  --final-quality          Use 1080p60, veryfast/crf18, Corrscope AA on
  --encoder auto|libx264|nvenc  Encoder (default: auto: NVENC when available, otherwise libx264)
  --tool-timeout-minutes N Corrscope/FFmpeg timeout (default: 60)
  --title TEXT             Overlay title
  --subtitle TEXT          Overlay subtitle
  --credits TEXT           Overlay credits
  --font PATH              TrueType/OpenType font with CJK coverage
  --effects all|none       Active-note flash & ripple (default: all)
  --note-color instrument|pitch|channel  Note coloring (default: instrument)
  --stems-only             Render stems + corrscope YAML, skip video
  --quiet                  Suppress progress output
  --json                   Machine-readable output
  -o, --output DIR         Output directory
  --video PATH             Final MP4 path
  --overwrite              Overwrite existing output

FMP-family only:
  --layout diagnostic|focus  Panel layout (default: diagnostic)
  --analysis                 Run symbolic analysis
  --analysis-detail minimal|standard|full
  --analysis-overlay none|minimal|standard

Register-log only:
  --backend auto|fmp|mdplayer  Playback backend (default: auto)
  --scopes auto|channel|device|master|off  Scope mode
EOF
}

if [ $# -lt 1 ]; then
    usage
    exit 1
fi

if [ "$1" = "-h" ] || [ "$1" = "--help" ]; then
    usage
    exit 0
fi

INPUT="$1"
shift

# Validate input
if [ ! -f "$INPUT" ]; then
    echo "error: input not found: $INPUT"
    exit 2
fi

# Determine format family from extension
INPUT_LOWER="${INPUT,,}"
case "$INPUT_LOWER" in
    *.ovi|*.opi|*.ozi|*.mpi|*.mvi|*.mzi)
        FAMILY="fmp"
        ;;
    *.vgm|*.vgz|*.s98|*.xgm|*.mdx|*.mid|*.midi)
        FAMILY="register-log"
        ;;
    *)
        echo "error: unsupported format: $INPUT"
        echo "supported formats: .ovi .opi .ozi .mpi .mvi .mzi .vgm .vgz .s98 .xgm .mdx .mid .midi"
        exit 2
        ;;
esac

# Validate tools that are always required
if [ ! -x "$MDPLAYER_RENDER" ]; then
    echo "error: mdplayer-render not found at $MDPLAYER_RENDER (build it first: dotnet build -c Release MDPlayer/src/MDPlayer.Fmp.Cli)"
    exit 3
fi
if [ ! -x "$CORR" ]; then
    echo "error: corr not found at $CORR (run: $ROOT/_venv/bin/pip install corrscope)"
    exit 3
fi
if [ ! -x "$FFMPEG" ]; then
    echo "error: ffmpeg not found at $FFMPEG"
    exit 3
fi

# FMP.COM is only required for FMP-family formats
if [ "$FAMILY" = "fmp" ]; then
    if [ ! -f "$FMP_COM" ]; then
        echo "error: FMP.COM not found at $FMP_COM"
        exit 3
    fi
fi

BASENAME="${INPUT%.*}"
VIZ_DIR="${BASENAME}.visualization"

echo "=== $INPUT → Visualization ==="
echo "  Family: ${FAMILY}"
echo "  Input:  $INPUT"
echo "  Output: $VIZ_DIR/"
echo ""

# Build command arguments conditionally
ARGS=(
    visualize "$INPUT"
    --corrscope "$CORR"
    --ffmpeg "$FFMPEG"
    --overwrite
)

if [ "$FAMILY" = "fmp" ]; then
    ARGS+=(--fmp-com "$FMP_COM")
fi

ARGS+=("$@")

MPLCONFIGDIR=/tmp/mplcfg \
"$MDPLAYER_RENDER" "${ARGS[@]}"

echo ""
echo "=== Done ==="
if [ -f "$VIZ_DIR/visualization.mp4" ]; then
    echo "Video: $VIZ_DIR/visualization.mp4"
    echo "Stems: $VIZ_DIR/scope/audio/"
    echo "Timeline: $VIZ_DIR/timeline.json"
else
    echo "Warning: visualization.mp4 not found in $VIZ_DIR"
    echo "Partial output may be in $VIZ_DIR/visualization.partial.mp4"
fi
