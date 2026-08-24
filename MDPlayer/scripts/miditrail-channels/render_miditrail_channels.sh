#!/usr/bin/env bash
#
# render_miditrail_channels.sh — render each per-channel .mid through STOCK
# MIDITrail.exe under Wine on a dedicated Xvfb display, producing one clean CFR
# mp4 per channel with NO window chrome and FRAME-EXACT cross-channel sync.
#
# Pipeline: serial 6-channel MIDITrail capture -> normalize (crop chrome +
# SCORE-ANCHORED sync + identical frame count) -> xstack 3x2 tile -> mux
# MDPlayer audio (tile_and_mux.sh).
#
# Two required fixes baked in (capture/presentation only; transport parity of
# the per-channel .mid is untouched):
#
#   1. CHROME-FREE TILES. MIDITrail's own ~48px menu bar is app-drawn and F11
#      does not strip it under Wine+Xvfb; no CLI fullscreen flag removes it.
#      Fix: run the app window at Height = 540 + 48 = 588 so the scene region is
#      exactly 640x540, grab the full 640x588 window and crop the top 48px band
#      (crop=640:540:0:48) — leaving pure scene edge-to-edge in each tile.
#
#   2. FRAME-EXACT SYNC (SCORE-ANCHORED). The old approach aligned each channel
#      to ITS OWN first visible note — wrong, because each per-channel .mid
#      carries only that channel's notes, so their first note-ons land at very
#      different score instants (FM2/FM3 ~0.73s vs FM1/4/5 ~3.14s vs FM6
#      ~3.34s). Aligning first-onsets shifted whole channels arbitrarily.
#      Correct fix: every channel shares the SAME score clock (SMF1, 960 PPQ,
#      fixed 120 BPM at tick 0 => score_sec = tick/1920). We detect the
#      per-channel score-view activation frame (score time 0), gate it on a
#      tight cross-channel spread, derive the output frame mapping purely from
#      the score, then VERIFY cross-channel simultaneity at score checkpoints
#      (see normalize_score_align.py). Capture LONGER than the target with a
#      lead-in + tail as slack; trim each channel to identical frame count so
#      all six report the same nb_frames.
#
# Usage:
#   render_miditrail_channels.sh  \
#       --channels-dir DIR   (dir of per-channel .mid files from `mdplayer-render midi --channels`)
#       --out-dir DIR        (where final channel mp4s are written)
#       --mt-dir DIR         (stock MIDITrail dir, default /tmp/mt141/MIDITrail)
#       --vfps N             (capture fps, default 30)
#       --duration S         (target seconds per channel, default 20)
#       --lead S             (seconds of lead-in before SPACE, default 1.0)
#       --tail S             (seconds captured after target, default 2.0)
#       [--channels 'id1 id2 ...']  (default: all .mid under --channels-dir)
#       [--display :N]       (Xvfb display, default :97)
#
# Scene: MIDITrail PianoRollRain2D (selected via [Viewpoint-PianoRollRain2D] in
# View.ini), no stars, black background, per-channel hue via Ch-01 color (every
# per-channel .mid voices its notes on MIDI channel 0) and an adapted pitch
# window (KeyDispRangeStart/End) from the channel's actual note range.
#
# No MIDI sound: MIDI.ini "Midi Through Port-0" is silent in the Wine prefix;
# the real MDPlayer audio is muxed by tile_and_mux.sh.
set -u

# Resolve this script's own directory to an ABSOLUTE path up-front (before any
# `cd` that changes the working directory mid-script), so we can invoke the
# co-located score-anchored aligner regardless of how this script is launched.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)"
[ -n "$SCRIPT_DIR" ] || SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

CHANNELS_DIR=""
OUT_DIR=""
MT_DIR="/tmp/mt141/MIDITrail"
VFPS=30
DURATION=20
LEAD=1.0
TAIL=2.0
DISPLAYNO=":97"
CHANNELS=""
MENU_H=48          # MIDITrail app-drawn menu bar height (pixels), verified constant.
SCENE_H=540         # target pure-scene height per tile
GRAB_H=$(( SCENE_H + MENU_H ))   # 588 -> app window height so scene = 540

while [ $# -gt 0 ]; do
  case "$1" in
    --channels-dir) CHANNELS_DIR="$2"; shift 2;;
    --out-dir) OUT_DIR="$2"; shift 2;;
    --mt-dir) MT_DIR="$2"; shift 2;;
    --vfps) VFPS="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    --lead) LEAD="$2"; shift 2;;
    --tail) TAIL="$2"; shift 2;;
    --display) DISPLAYNO="$2"; shift 2;;
    --channels) CHANNELS="$2"; shift 2;;
    *) echo "error: unknown option '$1'"; exit 2;;
  esac
done

[ -n "$CHANNELS_DIR" ] || { echo "error: --channels-dir required"; exit 2; }
[ -n "$OUT_DIR" ] || { echo "error: --out-dir required"; exit 2; }
[ -f "$MT_DIR/MIDITrail.exe" ] || { echo "error: MIDITrail.exe not found in $MT_DIR"; exit 2; }

export WINEPREFIX="$HOME/.local/share/mdplayer-miditrail"
export DISPLAY="$DISPLAYNO"
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export WINEDEBUG=-all
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+$WINEDLLOVERRIDES;}winewayland.drv=;"

CFG="$WINEPREFIX/drive_c/users/jose/AppData/Roaming/yknk/MIDITrail"
RAW_DIR="$OUT_DIR/_raw"     # 640x588 raw captures (with chrome + lead-in + tail)
mkdir -p "$OUT_DIR" "$RAW_DIR"

# ---- discover per-channel .mid files ----
if [ -z "$CHANNELS" ]; then
  # Prefer FM voices (semantic: *fm.N.mid); fall back to all if none match.
  mapfile -t FILES < <(ls "$CHANNELS_DIR"/*fm.*.mid 2>/dev/null | sort -V)
  if [ ${#FILES[@]} -eq 0 ]; then
    mapfile -t FILES < <(ls "$CHANNELS_DIR"/*.mid 2>/dev/null | sort)
  fi
else
  FILES=()
  for c in $CHANNELS; do FILES+=("$CHANNELS_DIR/$c.mid"); done
fi
[ ${#FILES[@]} -gt 0 ] || { echo "error: no .mid files under $CHANNELS_DIR"; exit 2; }

# ---- Xvfb (single 640x588 screen; kept warm across channels) ----
pkill -f "Xvfb $DISPLAYNO" 2>/dev/null; sleep 1
rm -f "/tmp/.X11-unix/X${DISPLAYNO#:}"
Xvfb "$DISPLAYNO" -screen 0 640x${GRAB_H}x24 >/dev/null 2>&1 &
XVFB_PID=$!
for i in $(seq 1 50); do [ -S "/tmp/.X11-unix/X${DISPLAYNO#:}" ] && break; sleep 0.2; done
[ -S "/tmp/.X11-unix/X${DISPLAYNO#:}" ] || { echo "FAIL: no Xvfb"; exit 1; }
echo "[init] Xvfb up ($DISPLAYNO 640x$GRAB_H)"

# Static scene template (identical to previous version; height handled separately).
write_scene() {
  local scene="$1" color="$2" kstart="$3" kend="$4"
  cat > "$scene" <<EOF
[FirstPersonCam]
VelocityFB=10.0
VelocityLR=10.0
VelocityUD=5.0
VelocityPT=6.0
AcceleRate=2.0
VelocityAutoRoll=6.0
VelocityManualRoll=1.0

[Scale]
QuarterNoteLength=1.0
NoteBoxHeight=0.1
NoteBoxWidth=0.1
NoteStep=0.1
ChStep=0.5
RippleHeight=1.0
RippleWidth=1.0
PictBoardRelativePos=1.0
LiveNoteLengthPerSecond=2.0
LiveMonitorDisplayDuration=30000

[Color]
NoteColorType=CHANNEL
Ch-01-NoteRGBA=$color
Ch-02-NoteRGBA=81EF72FF
Ch-03-NoteRGBA=7291EFFF
Ch-04-NoteRGBA=EFA272FF
Ch-05-NoteRGBA=72EF91FF
Ch-06-NoteRGBA=8372EFFF
Ch-07-NoteRGBA=EFD072FF
Ch-08-NoteRGBA=72EFC1FF
Ch-09-NoteRGBA=B072EFFF
Ch-10-NoteRGBA=DEEF72FF
Ch-11-NoteRGBA=72EFEFFF
Ch-12-NoteRGBA=E072EFFF
Ch-13-NoteRGBA=B0EF72FF
Ch-14-NoteRGBA=72BFEFFF
Ch-15-NoteRGBA=EF72D0FF
Ch-16-NoteRGBA=EF72A0FF
Scale-01-NoteRGBA=D50000FF
Scale-02-NoteRGBA=480BFFFF
Scale-03-NoteRGBA=FFFF40FF
Scale-04-NoteRGBA=9F009FFF
Scale-05-NoteRGBA=AAFFD5FF
Scale-06-NoteRGBA=9F001CFF
Scale-07-NoteRGBA=409FFFFF
Scale-08-NoteRGBA=FFAF0BFF
Scale-09-NoteRGBA=9F00D5FF
Scale-10-NoteRGBA=75FF75FF
Scale-11-NoteRGBA=6A0035FF
Scale-12-NoteRGBA=75E7FFFF
GridLineRGBA=444444FF
PlaybackSectionRGBA=AAAAAA38
CaptionRGBA=AAAAAAFF
BackGroundRGB=000000

[ActiveNote]
Duration=400
WhiteRate=0.9
EmissiveRGBA=1A1A1A1A
SizeRatio=1.4

[Ripple]
Duration=1600

[Stars]
NumberOfStars=0

[Bitmap]
Board=data\Board.png
Ripple=data\Ripple.png
Keyboard=data\Keyboard.png

[PianoKeyboard]
KeyDownDuration=40
KeyUpDuration=40
KeyboardStepY=0
KeyboardStepZ=0.001
KeyboardMaxDispNum=1
WhiteKeyColor=FFFFFFFF
BlackKeyColor=FFFFFFFF
ActiveKeyColorType=NOTE
ActiveKeyColor=FF0000FF
ActiveKeyColorDuration=400
ActiveKeyColorTailRate=0.3
KeyDispRangeStart=$kstart
KeyDispRangeEnd=$kend
EOF
}

# 6 distinct hues (RGBA) for the 6 panels.
HUES=(EF7272FF 7291EFFF 72EF91FF EFA272FF DEEF72FF 72EFEFFF)

# basename of channel .mid -> raw capture lambda
capture_one() {
  local mid="$1" idx="$2"
  local base; base=$(basename "$mid" .mid)
  local raw="$RAW_DIR/$base.raw.mp4"
  local color="${HUES[$(( idx % 6 ))]}"

  # Adapt pitch window from the channel's actual note range (python MIDI parse).
  local kstart=0 kend=127
  local range
  range=$(python3 - "$mid" <<'PY' 2>/dev/null
import sys, struct
def vlq(d,i):
    v=0
    while True:
        b=d[i]; i+=1; v=(v<<7)|(b&0x7f)
        if not b&0x80: return v,i
d=open(sys.argv[1],'rb').read()
i=14; lo=127; hi=0
for _ in range(struct.unpack('>H',d[10:12])[0]):
    ln=struct.unpack('>I',d[i+4:i+8])[0]; end=i+8+ln; j=i+8; tick=0; rstatus=0
    while j<end:
        dl,j=vlq(d,j); tick+=dl
        st=d[j]
        if st==0xFF:
            mt=d[j+1]; ml=d[j+2]; j+=3+ml
        elif st&0x80:
            rstatus=st; j+=1
        else:
            st=rstatus
        hi2=st&0xF0
        if hi2==0x90 and st!=0xF0:
            note=d[j]; j+=2
            if note<lo: lo=note
            if note>hi: hi=note
        elif hi2 in (0x80,0xA0,0xB0,0xE0):
            j+=2
        elif hi2 in (0xC0,0xD0):
            j+=1
        elif st==0xF0 or st==0xF7:
            ln=v2=0
            while True:
                b2=d[j]; j+=1; ln=(ln<<7)|(b2&0x7f)
                if not b2&0x80: break
            j+=ln
        elif st==0xFF:
            continue
    i=end
print(f"{lo} {hi}")
PY
)
  if [ -n "$range" ]; then
    set -- $range
    kstart=$(( $1 - 3 )); [ $kstart -lt 0 ] && kstart=0
    kend=$(( $2 + 3 ));   [ $kend -gt 127 ] && kend=127
    [ $(( kend - kstart )) -lt 12 ] && { kend=$(( kstart + 12 )); [ $kend -gt 127 ] && { kend=127; kstart=$((kend-12)); }; }
  fi

  # Config swap: per-channel scene + view-mode switch selecting PianoRollRain2D.
  scene="$MT_DIR/conf/PianoRollRain2D.ini"
  cp "$scene" "$scene.pre"
  write_scene "$scene" "$color" "$kstart" "$kend"
  # Height=GRAB_H so the scene region is exactly SCENE_H after the MENU_H band.
  printf '[WindowSize]\nWidth=640\nHeight=%s\n[Viewpoint-PianoRollRain2D]\nAutoRollVelocity=0.0\nManualRollAngle=0.0\nPhi=90.0\nTheta=90.0\nX=0.0\nY=6.4\nZ=-18.0\n' "$GRAB_H" > "$CFG/View.ini"

  cp "$mid" "$MT_DIR/capture.mid"

  echo "[$idx] capturing $base (color=$color keys=$kstart..$kend) -> $raw"
  cd "$MT_DIR" || return 1
  ( wine ./MIDITrail.exe capture.mid >/dev/null 2>&1 ) &
  WINE_PID=$!

  WID=""
  for i in $(seq 1 150); do
    WID=$(xdotool search --onlyvisible --name 'MIDITrail' 2>/dev/null | head -1)
    [ -z "$WID" ] && WID=$(xdotool search --onlyvisible --class miditrail 2>/dev/null | head -1)
    [ -n "$WID" ] && break
    sleep 0.2
  done
  if [ -z "$WID" ]; then
    echo "[$idx] FAIL: no MIDITrail window"
    kill "$WINE_PID" 2>/dev/null; mv "$scene.pre" "$scene"; return 1
  fi
  sleep 2
  xdotool windowmove "$WID" 0 0
  xdotool windowfocus --sync "$WID" 2>/dev/null

  # Capture LONGER than the target: LEAD seconds of empty lead-in before SPACE,
  # plus TAIL seconds of slack after the target duration (used for sync trim).
  local cap_sec
  cap_sec=$(python3 -c "print($DURATION + $LEAD + $TAIL)")
  ffmpeg -y -loglevel error -f x11grab -framerate "$VFPS" -video_size 640x$GRAB_H \
    -i "$DISPLAYNO" -t "$cap_sec" -pix_fmt yuv420p \
    -c:v libx264 -preset ultrafast -crf 18 "$raw" &
  FF_PID=$!
  # keep frame indexing exact: sleep LEAD seconds before triggering SPACE
  sleep "$LEAD"
  xdotool key --clearmodifiers space
  wait "$FF_PID"

  # Tear down only MIDITrail; keep wineserver + Xvfb warm.
  kill "$WINE_PID" 2>/dev/null; pkill -f MIDITrail.exe 2>/dev/null
  for i in $(seq 1 60); do pgrep -f MIDITrail.exe >/dev/null || break; sleep 0.2; done
  mv "$scene.pre" "$scene"
  echo "[$idx] captured $raw"
}

# ---- capture every channel to RAW (chrome + lead-in + tail) ----
idx=0
for f in "${FILES[@]}"; do
  capture_one "$f" "$idx" || echo "[$idx] FAILED"
  idx=$(( idx + 1 ))
done

# ---- NORMALIZATION PASS: chrome-free + SCORE-ANCHORED frame-exact sync ----
# Replaces the previous (BROKEN) first-visible-note alignment. Each per-channel
# .mid carries only that channel's notes, so its first note-on sits at a
# DIFFERENT score instant (e.g. FM2/FM3 at tick 1409 vs FM1/4/5 at tick 6031);
# aligning first-onsets shifted whole channels by arbitrary amounts.
#
# Correct approach: every channel shares the SAME score clock (SMF1, 960 PPQ,
# fixed 120 BPM at tick 0 => score_sec = tick/1920). We detect the per-channel
# score-view activation frame (score time 0), gate it on a tight cross-channel
# spread, derive the output frame mapping purely from the score, then VERIFY
# cross-channel simultaneity at score checkpoints. See normalize_score_align.py.
echo "[norm] normalizing ${#FILES[@]} raw captures: score-anchored chrome + sync"

"$SCRIPT_DIR/normalize_score_align.py" \
    --raw-dir "$RAW_DIR" \
    --mid-dir "$CHANNELS_DIR" \
    --out-dir "$OUT_DIR" \
    --duration "$DURATION" \
    --vfps "$VFPS" \
    --menu-h "$MENU_H" \
    --scene-h "$SCENE_H" \
  || { echo "FATAL: score-anchored normalization failed"; exit 1; }

echo "$XVFB_PID" > "$OUT_DIR/.xvfb.pid"
echo "[done] per-channel renders + normalization complete in $OUT_DIR"
