#!/usr/bin/env bash
#
# tile_and_mux.sh — tile six 640x540 per-channel mp4s 3x2 into 1920x1080 and mux
# MDPlayer's real audio, then deliver the final six-panel MP4.
#
# Usage:
#   tile_and_mux.sh --channels-dir DIR --out-dir OUT \
#       --audio-master WAV  [--duration S] [--order 'f1 f2 ... f6']
#
# The six channel files are taken in the order given by --order (comma/space
# list of .mid basenames), or the first six .mid-derived mp4s in the out dir.
# Left-to-right, top-to-bottom fills FM1 FM2 FM3 / FM4 FM5 FM6 by default.
set -u

CHANNELS_DIR=""
OUT_DIR=""
AUDIO_MASTER=""
DURATION=""
ORDER=""
FINAL_NAME="MDPlayer_miditrail_6ch_clean_sync.mp4"

while [ $# -gt 0 ]; do
  case "$1" in
    --channels-dir) CHANNELS_DIR="$2"; shift 2;;
    --out-dir) OUT_DIR="$2"; shift 2;;
    --audio-master) AUDIO_MASTER="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    --order) ORDER="$2"; shift 2;;
    --final-name) FINAL_NAME="$2"; shift 2;;
    *) echo "error: unknown option '$1'"; exit 2;;
  esac
done
[ -n "$OUT_DIR" ] || { echo "error: --out-dir required"; exit 2; }

if [ -z "$ORDER" ]; then
  # default FM order: fm1 fm2 fm3 / fm4 fm5 fm6
  ORDER="channel-ym2608.0.fm.1 channel-ym2608.0.fm.2 channel-ym2608.0.fm.3 channel-ym2608.0.fm.4 channel-ym2608.0.fm.5 channel-ym2608.0.fm.6"
fi

V=()
for b in $ORDER; do
  f="$OUT_DIR/$b.mp4"
  [ -f "$f" ] || f="$OUT_DIR/$(basename "$b" .mid).mp4"
  [ -f "$f" ] || { echo "error: missing channel mp4 for '$b'"; exit 1; }
  V+=("$f")
done
[ ${#V[@]} -eq 6 ] || { echo "error: need exactly 6 channel mp4s, got ${#V[@]}"; exit 2; }

# Duration: default to the shortest channel (or explicit).
if [ -z "$DURATION" ]; then
  DURATION=$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "${V[0]}")
fi

INPUTS=()
for f in "${V[@]}"; do INPUTS+=(-i "$f"); done

echo "[tile] xstack 3x2 -> $OUT_DIR/tiled.mp4"
ffmpeg -y -loglevel error "${INPUTS[@]}" \
  -filter_complex "[0][1][2][3][4][5]xstack=inputs=6:layout=0_0|w0_0|w0+w1_0|0_h0|w0_h0|w0+w1_h0" \
  -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p "$OUT_DIR/tiled.mp4"

# Frame-exact duration comes from the (now normalized) video itself.
VID_DUR=$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "$OUT_DIR/tiled.mp4")

# Audio: slice to the exact normalized video duration and mux.
if [ -n "$AUDIO_MASTER" ] && [ -f "$AUDIO_MASTER" ]; then
  ffmpeg -y -loglevel error -ss 0 -i "$AUDIO_MASTER" -t "$VID_DUR" -c:a pcm_s16le "$OUT_DIR/slice.wav"
  ffmpeg -y -loglevel error -i "$OUT_DIR/tiled.mp4" -i "$OUT_DIR/slice.wav" \
    -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 256k -shortest \
    "$OUT_DIR/$FINAL_NAME"
  FINAL="$OUT_DIR/$FINAL_NAME"
else
  FINAL="$OUT_DIR/tiled.mp4"
fi

echo "[done] final: $FINAL"
ffprobe -v error -show_entries format=duration,size -of default=nw=1 "$FINAL"
ffprobe -v error -show_entries stream=codec_type,codec_name,width,height -of default=nw=1 "$FINAL"
