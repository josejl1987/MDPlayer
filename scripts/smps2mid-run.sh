#!/usr/bin/env bash
# ============================================================================
# smps2mid-run.sh — drive the VB6 smps2mid GUI to convert raw SMPS sequences
# (.bin) to semantic MIDI, under Wine + Xvfb + xdotool.
#
# Flow (source-mapped in smps2mid.frm):
#   Form_Load fills GameList (SMPS Type); OpenButton_Click -> OpenDialog
#   (*.bin;*.sms;*.gg;*.32x); FILE_TYPE_BIN if LOF<=0x10000; then
#   S2M_Button_Click calls SetConvOptions+SMPS2MID, then SaveDialog -> SaveFile.
# We drive it with the button mnemonics: OpenButton caption "&Open File" -> Alt+O;
# S2M_Button caption "&SMPS2MID" -> Alt+S. Save dialog is the standard comdlg.
#
# Usage: smps2mid-run.sh <in.bin> <out.mid> <smpsTypeName>
#   <smpsTypeName> one of: "Auto","Sonic 1",...,"Generic SMPS 68k" (default),
#   "Gen. SMPS Z80 a","Gen. SMPS Z80 b".
# ============================================================================
set -uo pipefail
IN="${1:?usage: smps2mid-run.sh <in.bin> <out.mid> [smpsType]}" 
OUT="$2"
SMPS_TYPE="${3:-Generic SMPS 68k}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="$ROOT/artifacts/reference-midi"
TOOLS="$ART/tools/smps2mid-0.4.3-bin"
export WINEPREFIX="$ART/wine-prefix"
export LANG=en_US.UTF-8 LANGUAGE=en
export LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe
export WINEDEBUG=-all
LOGS="$ART/logs"; mkdir -p "$LOGS" "$(dirname "$OUT")"
BASE=$(basename "$IN" .bin)

err(){ echo "error: $*" >&2; exit 1; }
# Windows path accepted by the common dialog edit box: Z:\dir\file (one backslash).
zpath(){ printf 'Z:\\%s' "$(printf '%s' "${1%/}" | sed 's|^/||; s|/|\\|g')"; }

# ---- pick a free display (never kill another display's server) ----
for d in 99 98 97 96 95; do
  if ! DISPLAY=:$d xdpyinfo >/dev/null 2>&1; then DISPLAY=:$d; break; fi
done
export DISPLAY
Xvfb "$DISPLAY" -screen 0 640x560x24 -nolisten tcp >/dev/null 2>&1 &
XV=$!
sleep 3
( wineserver -k >/dev/null 2>&1 || true ); sleep 1

rm -f "$OUT"
( cd "$TOOLS" && wine ./smps2mid.exe >"$LOGS/smps2mid_$BASE.log" 2>&1 ) &
WP=$!
sleep 12
WID=$(xdotool search --name '^SMPS -> MID$' | head -1 || true)
if [ -z "$WID" ]; then
  echo "diagnostics:"; xdotool search --name '.*' 2>/dev/null | while read -r id; do echo "  [$id] $(xdotool getwindowname "$id" 2>/dev/null)"; done
  tail -15 "$LOGS/smps2mid_$BASE.log" 2>/dev/null
  kill "$WP" 2>/dev/null; ( wineserver -k >/dev/null 2>&1 || true ); kill "$XV" 2>/dev/null
  err "smps2mid window not found for $BASE"
fi
xdotool windowfocus --sync "$WID" 2>/dev/null || true; sleep 0.5

# 1) Open file: Alt+O -> dialog -> type Z:\path -> Enter
xdotool key --clearmodifiers alt+o; sleep 3
DLG=$(xdotool search --name 'Open' | head -1 || true)
if [ -z "$DLG" ]; then
  xdotool windowfocus --sync "$WID" 2>/dev/null; sleep 0.5; xdotool key alt+o; sleep 3
  DLG=$(xdotool search --name 'Open' | head -1 || true)
fi
[ -n "$DLG" ] || err "open dialog not found for $BASE"
xdotool windowfocus --sync "$DLG" 2>/dev/null || true; sleep 0.5
xdotool type --delay 15 "$(zpath "$IN")"; sleep 1
xdotool key --clearmodifiers Return; sleep 4

# 2) SMPS Type: select in GameList combo. Focus main window, click combo caret,
#    open list, type distinguishing letters, Enter.
xdotool windowfocus --sync "$WID" 2>/dev/null || true; sleep 0.3
xdotool mousemove --window "$WID" 5160 2380 click 1; sleep 0.5
xdotool key --clearmodifiers ctrl+Down; sleep 0.5
case "$SMPS_TYPE" in
  *68k*) K="Generic SMPS 68k" ;;
  *Z80*a*) K="Gen. SMPS Z80 a" ;;
  *Z80*b*) K="Gen. SMPS Z80 b" ;;
  *o1*) K="Sonic 1" ;;
  Auto) K="Auto" ;;
  *) K="$SMPS_TYPE" ;;
esac
xdotool type --delay 15 "$K"; sleep 0.5
xdotool key --clearmodifiers Return; sleep 0.5

# 3) MemBase stays 0000 for rip sequences (data starts at file base).
# 4) Convert: Alt+S
xdotool windowfocus --sync "$WID" 2>/dev/null || true; sleep 0.3
xdotool key --clearmodifiers alt+s; sleep 3

# 5) Save dialog: focus it, clear filename, type output path, Enter
SDLG=$(xdotool search --name 'Save' | head -1 || true)
if [ -n "$SDLG" ]; then
  xdotool windowfocus --sync "$SDLG" 2>/dev/null || true; sleep 0.5
  xdotool key --clearmodifiers ctrl+a; sleep 0.3
  xdotool type --delay 15 "$(zpath "$OUT")"; sleep 0.5
  xdotool key --clearmodifiers Return; sleep 3
fi

# 6) wait for a stable non-empty .mid
for i in $(seq 1 40); do
  if [ -s "$OUT" ]; then
    s1=$(stat -c%s "$OUT" 2>/dev/null || echo 0); sleep 1
    s2=$(stat -c%s "$OUT" 2>/dev/null || echo 0)
    if [ "$s1" = "$s2" ] && [ "$s1" -gt 0 ]; then break; fi
  fi
  sleep 1
done

kill "$WP" 2>/dev/null; ( wineserver -k >/dev/null 2>&1 || true ); sleep 1; kill "$XV" 2>/dev/null
if [ -s "$OUT" ]; then echo "smps2mid OK $BASE -> $OUT ($(stat -c%s "$OUT") bytes)"; else err "smps2mid produced no mid for $BASE"; fi
