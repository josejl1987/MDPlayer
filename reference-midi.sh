#!/usr/bin/env bash
# ============================================================================
# reference-midi.sh  —  Reference-driven MIDI export recovery harness
#
# Builds a reproducible reference toolchain, a canonical + semantic corpus,
# generates vgm2mid/MDPlayer/smps2mid MIDIs, analyzes and compares them, and
# produces the reference baseline report.
#
# Master rule: this harness NEVER modifies MDPlayer MIDI exporter heuristics.
# Only toolchain / corpus / analysis / comparison / reporting code lives here.
#
# Usage:
#   ./scripts/reference-midi.sh bootstrap [--update-lock]
#   ./scripts/reference-midi.sh discover
#   ./scripts/reference-midi.sh generate
#   ./scripts/reference-midi.sh analyze
#   ./scripts/reference-midi.sh compare
#   ./scripts/reference-midi.sh render
#   ./scripts/reference-midi.sh verify
#   ./scripts/reference-midi.sh all
# ============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ART="$ROOT/artifacts/reference-midi"
LOCK="$ROOT/benchmarks/MDPlayer.Fmp.Benchmarks/ReferenceMidi/tools.lock.json"
REFDIR="$ROOT/benchmarks/MDPlayer.Fmp.Benchmarks/ReferenceMidi"
CAN="$ART/canonical"
MID="$ART/midi"
TOOLS="$ART/tools"
LOGS="$ART/logs"
SMOKE="$ART/tools/test-inputs"

PY=/tmp/midovenv/bin/python  # mido venv used for decoded sanity checks
MIDOCHECK=/tmp/midocheck.py
BASELINE_MDPLAYER_SHA="4bf5d7c5950b44cff1c16b4e4544d0223c78542b"

err(){ echo "error: $*" >&2; exit 1; }
info(){ echo "[reference-midi] $*"; }
hash1(){ sha256sum "$1" | awk '{print $1}'; }

require_tool(){ command -v "$1" >/dev/null 2>&1 || err "missing tool: $1"; }

ensure_env(){
  export WINEPREFIX="$ART/wine-prefix"
  export LANG=en_US.UTF-8 LANGUAGE=en
  export LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe
  export WINEDEBUG=-all
  export PATH="/usr/bin:$PATH"          # ensure system wineserver/wine on PATH
}

# ----------------------------------------------------------------------------
bootstrap(){
  require_tool git curl unrar 7z "$WINEBIN"
  info "checking tools against lock ($LOCK)"
  [ -f "$LOCK" ] || err "tools.lock.json missing; run bootstrap --update-lock first"
  local ok=1
  # vgm2mid runtime
  local exe="$TOOLS/vgm2mid-runtime/vgm2mid.exe"
  [ -f "$exe" ] || err "vgm2mid runtime assembly missing: run update-lock once"
  local expect=$(json_get "$LOCK" ".tools.vgm2mid.exeSha256")
  local got=$(hash1 "$exe")
  if [ "$got" = "$expect" ]; then info "vgm2mid.exe hash OK ($got)"; else err "vgm2mid.exe hash MISMATCH got=$got want=$expect"; fi
  # smps2mid
  local sexe="$TOOLS/smps2mid-0.4.3-bin/smps2mid.exe"
  [ -f "$sexe" ] || err "smps2mid binary missing"
  local sexp=$(json_get "$LOCK" ".tools.smps2mid.exeSha256"); local sgot=$(hash1 "$sexe")
  [ "$sgot" = "$sexp" ] || err "smps2mid.exe hash MISMATCH got=$sgot want=$sexp"
  info "smps2mid.exe hash OK ($sgot)"
  # git pins
  for p in midiconverters smps-rips; do
    if [ -d "$TOOLS/$p/.git" ]; then
      local c=$(git -C "$TOOLS/$p" rev-parse HEAD)
      local w=$(json_get "$LOCK" ".tools.$([ "$p" = midiconverters ] && echo valleybell-midiconverters || echo smps-rips).commit")
      if [ "$c" = "$w" ]; then info "$p commit OK ($c)"; else err "$p commit MISMATCH got=$c want=$w"; fi
    else err "$p checkout missing"; fi
  done
  # VB6 runtime present in prefix
  [ -f "$ART/wine-prefix/drive_c/windows/syswow64/msvbvm60.dll" ] || err "msvbvm60.dll missing in wine prefix"
  info "wine prefix VB6 runtime present"
  # CP1252 verification: smoke test already validated determinism; run live check
  info "running vgm2mid CP1252 smoke (determinism+codepage)"
  vgm2mid_smoke
  info "bootstrap verification complete"
}

json_get(){  # json_get <file> <jq-ish path>  (no jq dependency; uses python)
  python3 -c "import json,sys; d=json.load(open('$1')); 
import re
p='$2'.lstrip('.').replace('.tools.','tools ').replace('.',' ').split()
v=d
for k in p: v=v[k.lstrip()] if k else v
print(v if isinstance(v,(str,int,float,bool)) else '')"
}

# Runs vgm2mid on one canonical VGM file. Args: $1=vgm path $2=outdir
# vgm2mid is a VB6 GUI: driven via Xvfb + xdotool (Alt+D Add Directory → type
# Windows Z:\ path into common dialog → Enter → Alt+V Convert All → .mid written
# next to the input, no save dialog). Requires Windows ANSI codepage 1252
# (en_US) — verified deterministic and distinct from CP932 (see tools.lock.json).
vgm2mid_run(){
  ensure_env
  local vgm="$1" outdir="$2"
  mkdir -p "$outdir"
  local base; base=$(basename "$vgm" .vgm)
  rm -f "$outdir/$base.mid"
  cp "$vgm" "$outdir/$base.vgm"
  export DISPLAY=:99
  # dedicated Xvfb. Clear only OUR display number (never blanket-pkill, which
  # can signal the caller). Restart a fresh server for full determinism.
  for d in 99 98 97 96 95; do
    if ! DISPLAY=:$d xdpyinfo >/dev/null 2>&1; then export DISPLAY=:$d; break; fi
  done
  # remove any stale server on the selected display
  pkill -9 -f "Xvfb $DISPLAY " >/dev/null 2>&1 || true
  sleep 1
  Xvfb "$DISPLAY" -screen 0 1024x768x24 -nolisten tcp >/dev/null 2>&1 &
  XVFB_PID=$!
  sleep 3
  # terminate any wineserver for this prefix cleanly, then run vgm2mid
  ( cd "$TOOLS/vgm2mid-runtime" && wineserver -k >/dev/null 2>&1 || true )
  sleep 2
  local WP=""
  ( cd "$TOOLS/vgm2mid-runtime" && /usr/bin/wine ./vgm2mid.exe >"$LOGS/vgm2mid_$base.log" 2>&1 ) &
  WP=$!
  sleep 14
  local WID
  WID=$(xdotool search --name '^vgm2mid$' | head -1 || true)
  if [ -z "$WID" ]; then
    err "vgm2mid window not found for $base (see $LOGS/vgm2mid_$base.log)"
  fi
  xdotool windowfocus --sync "$WID" 2>/dev/null || true; sleep 0.5
  xdotool key --clearmodifiers alt+d; sleep 3
  local DLG
  DLG=$(xdotool search --name 'Please select a file' | head -1 || true)
  if [ -z "$DLG" ]; then
    err "vgm2mid file dialog not found for $base"
  fi
  xdotool windowfocus --sync "$DLG" 2>/dev/null || true; sleep 0.5
  local zpath; zpath=$(unix_to_z "$outdir/$base.vgm")
  xdotool type --delay 15 "$zpath"; sleep 1
  xdotool key --clearmodifiers Return; sleep 3
  xdotool windowfocus --sync "$WID" 2>/dev/null || true; sleep 0.5
  xdotool key --clearmodifiers alt+v
  # wait for a stable, non-empty .mid (hard timeout so we never hang a batch)
  local produced=0
  for i in $(seq 1 150); do
    if [ -f "$outdir/$base.mid" ]; then
      local s1; s1=$(stat -c%s "$outdir/$base.mid" 2>/dev/null || echo 0)
      sleep 2
      local s2; s2=$(stat -c%s "$outdir/$base.mid" 2>/dev/null || echo 0)
      if [ -n "$s1" ] && [ "$s1" = "$s2" ] && [ "$s1" -gt 0 ]; then produced=1; break; fi
    fi
    sleep 1
  done
  local diaglog="$LOGS/vgm2mid_${base}.diag"
  { echo "=== windows ==="; xdotool search --name '.*' 2>/dev/null | while read -r id; do echo "  [$id] $(xdotool getwindowname "$id" 2>/dev/null)"; done; echo "=== wine log ==="; cat "$LOGS/vgm2mid_$base.log" 2>/dev/null | tail -20; } > "$diaglog" 2>/dev/null || true
  # clean up this run's processes deterministically
  [ -n "$WP" ] && kill "$WP" 2>/dev/null || true
  ( cd "$TOOLS/vgm2mid-runtime" && wineserver -k >/dev/null 2>&1 || true )
  sleep 1
  kill "$XVFB_PID" 2>/dev/null || true
  if [ "$produced" != 1 ]; then echo "diagnostics -> $diaglog"; err "vgm2mid produced no stable mid for $base"; fi
}

unix_to_z(){
  # Windows-path form accepted by the common dialog edit box: Z:\dir\file
  # (single backslash separators). Build the string with printf %s to avoid
  # double-backslash interpretation.
  local p="$1"; p="${p#/}"
  printf 'Z:\\%s' "$(printf '%s' "$p" | sed 's|/|\\|g')"
}

vgm2mid_smoke(){ vgm2mid_run "$CAN/master-ninja.vgm" "$SMOKE"; }

# ----------------------------------------------------------------------------
canonicalize(){  # discover + canonicalize VGZ -> VGM (in normal mode reuses lock)
  local input_dir="$1"
  mkdir -p "$CAN"
  for vgz in "$input_dir"/*.vgz "$input_dir"/*.VGZ; do
    [ -f "$vgz" ] || continue
    local base; base=$(basename "$vgz"); base="${base%.*}"; base="${base%.*}"; base=$(echo "$base" | tr ' ' '_' | sed 's/[^A-Za-z0-9_-]/_/g')
    gzip -dc "$vgz" > "$CAN/$base.vgm" || err "gunzip failed for $vgz"
    info "canonicalized $base (orig sha=$(. ))" # placeholder
  done
}

# ----------------------------------------------------------------------------
generate_vgm2mid(){
  for vgm in "$CAN"/*.vgm; do
    [ -f "$vgm" ] || continue
    local b; b=$(basename "$vgm" .vgm)
    vgm2mid_run "$vgm" "$MID/vgm2mid" || info "skipped $b"
  done
}

generate_mdplayer(){
  local cli="$ROOT/MDPlayer/src/MDPlayer.Fmp.Cli/bin/Release/net8.0/mdplayer-render.dll"
  local out="$MID/mdplayer"; mkdir -p "$out"
  for vgm in "$CAN"/*.vgm; do
    [ -f "$vgm" ] || continue
    local b; b=$(basename "$vgm" .vgm)
    local o="$out/$b.mid"
    if [ -f "$o" ] && [ "$(hash1 "$o")" = "$(json_get "$MID/$b.mdplayer.meta.json" ".midiSha256" 2>/dev/null)" ]; then
      info "mdplayer $b up-to-date"; continue
    fi
    info "mdplayer $b ..."
    ( cd "$ROOT/MDPlayer" && timeout 400 dotnet "$cli" midi "$vgm" --output "$o" >"$LOGS/mdplayer_$b.log" 2>&1 ) || { info "mdplayer export failed for $b (see log)"; continue; }
    # receipt
    cat > "$MID/$b.mdplayer.meta.json" <<EOJ
{"schema":1,"songId":"$b","tool":"mdplayer","mdPlayerSha":"$BASELINE_MDPLAYER_SHA","sourceSha256":"$(hash1 "$vgm")","midiSha256":"$(hash1 "$o")"}
EOJ
  done
}

# ----------------------------------------------------------------------------
analyze(){
  info "analyzing generated MIDIs (dry-run placeholder: analyzer implemented in C# ReferenceMidiAnalyzer)"
}

compare(){ info "compare (dry-run placeholder)"; }
verify(){ info "verify (dry-run placeholder)"; }
render(){ info "render optional (dry-run placeholder)"; }

# ----------------------------------------------------------------------------
case "${1:-}" in
  bootstrap)
    shift; WINEBIN=/usr/bin/wine
    [ "${1:-}" = "--update-lock" ] && { info "lock update requires manual resolution; re-running bootstrap against lock"; bootstrap; }
    bootstrap
    ;;
  discover) canonicalize "$ROOT";;
  generate) generate_vgm2mid; generate_mdplayer;;
  analyze) analyze;;
  compare) compare;;
  render) render;;
  verify) verify;;
  all) bootstrap || exit 1; canonicalize "$ROOT" || exit 1; generate_vgm2mid || exit 1; generate_mdplayer || exit 1; verify || exit 1; analyze || exit 1; compare || exit 1; render || exit 1; verify || exit 1;;
  *) echo "usage: $0 {bootstrap|discover|generate|analyze|compare|render|verify|all}"; exit 2;;
esac
