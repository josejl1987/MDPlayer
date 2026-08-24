#!/usr/bin/env bash
#
# render_look_sample.sh — render ONE channel through stock MIDITrail under Wine
# on a dedicated Xvfb display, applying a caller-supplied scene conf preset, with
# the standard chrome-strip + frame-exact sync plumbing, producing a short
# 640x540 pure-scene CFR clip plus a sidecar describing the look.
#
# This is the scene-tuning entry point: same proven env as the full 6-channel
# render (Xvfb 640x588, Height=588 window so the scene region is 640x540,
# crop=640:540:0:48, SPACE at LEAD lead-in, onset-aligned trim to TARGET_FRAMES).
#
# Usage:
#   render_look_sample.sh \
#       --mid FILE            (a single channel .mid, e.g. FM1)
#       --conf-ini FILE       (full PianoRollRain2D.ini preset to apply)
#       --out-dir DIR         (where <name>.mp4 + <name>.txt land)
#       --name NAME           (output basename, e.g. "look_A_default")
#       [--vfps N]            (default 30)
#       [--duration S]        (target clip seconds, default 10)
#       [--lead S]            (seconds of lead-in before SPACE, default 1.0)
#       [--tail S]            (seconds captured after target, default 2.0)
#       [--display :N]        (Xvfb display, default :97)
#       [--mt-dir DIR]        (stock MIDITrail dir, default /tmp/mt141/MIDITrail)
#       [--view 'X,Y,Z,Phi,Theta']  (viewpoint override, default 0,6.4,-18,90,90)
#
# Produces:
#   OUT/<name>.mp4   640x540 chrome-free CFR clip at exactly --duration seconds
#   OUT/<name>.txt   sidecar: the exact conf ini + view used (for reproducibility)
set -u

MID=""
CONF_INI=""
OUT_DIR=""
NAME=""
VFPS=30
DURATION=10
LEAD=1.0
TAIL=2.0
DISPLAYNO=":97"
MT_DIR="/tmp/mt141/MIDITrail"
VIEW="0,6.4,-18,90,90"
MENU_H=48
SCENE_H=540
GRAB_H=$(( SCENE_H + MENU_H ))

while [ $# -gt 0 ]; do
  case "$1" in
    --mid) MID="$2"; shift 2;;
    --conf-ini) CONF_INI="$2"; shift 2;;
    --out-dir) OUT_DIR="$2"; shift 2;;
    --name) NAME="$2"; shift 2;;
    --vfps) VFPS="$2"; shift 2;;
    --duration) DURATION="$2"; shift 2;;
    --lead) LEAD="$2"; shift 2;;
    --tail) TAIL="$2"; shift 2;;
    --display) DISPLAYNO="$2"; shift 2;;
    --mt-dir) MT_DIR="$2"; shift 2;;
    --view) VIEW="$2"; shift 2;;
    *) echo "error: unknown option '$1'"; exit 2;;
  esac
done
[ -n "$MID" ] && [ -f "$MID" ] || { echo "error: --mid FILE required"; exit 2; }
[ -n "$CONF_INI" ] && [ -f "$CONF_INI" ] || { echo "error: --conf-ini FILE required"; exit 2; }
[ -n "$OUT_DIR" ] && [ -n "$NAME" ] || { echo "error: --out-dir and --name required"; exit 2; }

export WINEPREFIX="$HOME/.local/share/mdplayer-miditrail"
export DISPLAY="$DISPLAYNO"
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export WINEDEBUG=-all
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:+$WINEDLLOVERRIDES;}winewayland.drv=;"
CFG="$WINEPREFIX/drive_c/users/jose/AppData/Roaming/yknk/MIDITrail"
RAW="$OUT_DIR/_raw"
mkdir -p "$OUT_DIR" "$RAW"

# ---- Xvfb (single 640x588 screen) ----
pkill -f "Xvfb $DISPLAYNO" 2>/dev/null; sleep 1
rm -f "/tmp/.X11-unix/X${DISPLAYNO#:}"
Xvfb "$DISPLAYNO" -screen 0 640x${GRAB_H}x24 >/dev/null 2>&1 &
XVFB_PID=$!
for i in $(seq 1 50); do [ -S "/tmp/.X11-unix/X${DISPLAYNO#:}" ] && break; sleep 0.2; done
[ -S "/tmp/.X11-unix/X${DISPLAYNO#:}" ] || { echo "FAIL: no Xvfb"; exit 1; }

# ---- apply scene conf / view / window height ----
scene="$MT_DIR/conf/PianoRollRain2D.ini"
cp "$scene" "$scene.pre"
cp "$CONF_INI" "$scene"
IFS=',' read -r VX VY VZ VPHI VTHETA <<<"$VIEW"
printf '[WindowSize]\nWidth=640\nHeight=%s\n[Viewpoint-PianoRollRain2D]\nAutoRollVelocity=0.0\nManualRollAngle=0.0\nPhi=%s\nTheta=%s\nX=%s\nY=%s\nZ=%s\n' \
  "$GRAB_H" "$VPHI" "$VTHETA" "$VX" "$VY" "$VZ" > "$CFG/View.ini"

cp "$MID" "$MT_DIR/capture.mid"

echo "[$NAME] capturing (view X=$VX Y=$VY Z=$VZ Phi=$VPHI Theta=$VTHETA, duration=${DURATION}s)"
cd "$MT_DIR" || exit 1
( wine ./MIDITrail.exe capture.mid >/dev/null 2>&1 ) &
WINE_PID=$!
WID=""
for i in $(seq 1 150); do
  WID=$(xdotool search --onlyvisible --name 'MIDITrail' 2>/dev/null | head -1)
  [ -z "$WID" ] && WID=$(xdotool search --onlyvisible --class miditrail 2>/dev/null | head -1)
  [ -n "$WID" ] && break
  sleep 0.2
done
[ -n "$WID" ] || { echo "[$NAME] FAIL: no window"; kill "$WINE_PID" 2>/dev/null; mv "$scene.pre" "$scene"; exit 1; }
sleep 2
xdotool windowmove "$WID" 0 0
xdotool windowfocus --sync "$WID" 2>/dev/null

cap_sec=$(python3 -c "print($DURATION + $LEAD + $TAIL)")
raw="$RAW/$NAME.raw.mp4"
ffmpeg -y -loglevel error -f x11grab -framerate "$VFPS" -video_size 640x$GRAB_H \
  -i "$DISPLAYNO" -t "$cap_sec" -pix_fmt yuv420p -c:v libx264 -preset ultrafast -crf 18 "$raw" &
FF_PID=$!
sleep "$LEAD"
xdotool key --clearmodifiers space
wait "$FF_PID"

kill "$WINE_PID" 2>/dev/null; pkill -f MIDITrail.exe 2>/dev/null
for i in $(seq 1 60); do pgrep -f MIDITrail.exe >/dev/null || break; sleep 0.2; done
mv "$scene.pre" "$scene"
kill "$XVFB_PID" 2>/dev/null

# ---- normalize: onset align + crop chrome + exact target length ----
TARGET=$(( VFPS * DURATION ))
python3 - "$raw" "$OUT_DIR" "$NAME" "$VFPS" "$MENU_H" "$SCENE_H" "$TARGET" <<'PY'
import os, subprocess, sys, numpy as np
raw, OUT, NAME = sys.argv[1], sys.argv[2], sys.argv[3]
VFPS, MENU_H, SCENE_H, TARGET = int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6]), int(sys.argv[7])
# onset detect via sustained frame-diff
W,H=640,588; fs=W*H*3
p=subprocess.Popen(["ffmpeg","-loglevel","error","-i",raw,"-f","rawvideo","-pix_fmt","rgb24","-"],stdout=subprocess.PIPE)
prev=None; d=[]
while True:
    c=p.stdout.read(fs)
    if not c or len(c)<fs: break
    a=np.frombuffer(c,np.uint8).reshape((H,W,3)).astype(np.float32)
    if prev is not None: d.append(float(np.abs(a-prev).mean()))
    prev=a
p.stdout.close(); p.wait()
onset=None
for i in range(len(d)-3):
    if all(d[i+k]>=0.30 for k in range(3)): onset=i+1; break
if onset is None: onset=int(round(1.0*VFPS))
cap=int(subprocess.check_output(["ffprobe","-v","error","-count_frames","-select_streams","v:0","-show_entries","stream=nb_read_frames","-of","default=nw=1:nk=1",raw],text=True).strip())
usable=cap-onset
MIN=min(usable, TARGET)
out=os.path.join(OUT, NAME+".mp4")
vf=f"trim=start_frame={onset}:end_frame={onset+MIN},setpts=PTS-STARTPTS,crop={W}:{SCENE_H}:0:{MENU_H},fps={VFPS}"
subprocess.run(["ffmpeg","-y","-loglevel","error","-i",raw,"-vf",vf,"-c:v","libx264","-preset","veryfast","-crf","18","-pix_fmt","yuv420p",out],check=True)
nb=subprocess.check_output(["ffprobe","-v","error","-count_frames","-select_streams","v:0","-show_entries","stream=nb_read_frames","-of","default=nw=1:nk=1",out],text=True).strip()
print(f"onset={onset} cap={cap} usable={usable} MIN={MIN} -> {out} nb_frames={nb}")
PY

# ---- sidecar: exact conf + view ----
{
  echo "# LOOK: $NAME  (scene: PianoRollRain2D, MIDITrail stock under Wine, chrome-stripped)"
  echo "# view: X=$VX Y=$VY Z=$VZ Phi=$VPHI Theta=$VTHETA  | window Height=$GRAB_H crop=640:540:0:$MENU_H"
  echo "## --- conf/PianoRollRain2D.ini ---"
  cat "$CONF_INI"
} > "$OUT_DIR/$NAME.txt"

echo "[$NAME] done: $OUT_DIR/$NAME.mp4 (+ $NAME.txt)"
