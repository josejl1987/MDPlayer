#!/usr/bin/env bash
# Corrected native-audio real-time-factor baseline for the LLE (Furnace YM2608)
# backend, measured AFTER the master-clock timeline fix.
#
# This is a baseline measurement only — it does NOT tune the compiler or change
# production flags. It produces the two numbers the report needs (wall time and
# real-time factor) plus a symbolized profile for the corrected workload.
#
# Usage:
#   native_render_bench.sh <ovi-dir> <FMP.COM> <output-dir> [seconds ...]
#
# Example (single fixture, 12s and 60s):
#   native_render_bench.sh /fixtures /path/to/FMP.COM /tmp/out 12 60
#
# Environment facts for the numbers in README.md (host):
#   CPU : 13th Gen Intel(R) Core(TM) i9-13900HX (32 logical CPUs)
#   OS  : Arch Linux
#   perf: 7.1.x
set -euo pipefail

IN_DIR="${1:?input OVI dir}"
FMP_COM="${2:?FMP.COM path}"
OUT_DIR="${3:?output dir}"
shift 3 || true
SECONDS_AV=( "$@" )
[ ${#SECONDS_AV[@]} -eq 0 ] && SECONDS_AV=(12)

[ -f "$FMP_COM" ] || { echo "FMP.COM missing: $FMP_COM" >&2; exit 1; }
[ -d "$IN_DIR" ] || { echo "input dir missing: $IN_DIR" >&2; exit 1; }
[ -d "$OUT_DIR" ] && rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

CLI="$(cd "$(dirname "$0")/../../../../src/MDPlayer.Fmp.Cli/bin/Release/net8.0" && pwd)/mdplayer-render"
[ -x "$CLI" ] || { echo "Release CLI not built at $CLI" >&2; exit 1; }

for secs in "${SECONDS_AV[@]}"; do
  start=$(date +%s.%N)
  "$CLI" batch "$IN_DIR" --output-dir "$OUT_DIR/native" \
      --opna-backend native-audio --fmp-com "$FMP_COM" \
      --overwrite --max-duration "$secs" --quiet
  end=$(date +%s.%N)
  wall=$(echo "$end - $start" | bc)
  echo "audio=${secs}s wall=${wall}s realtime_factor=$(echo "scale=3; $wall/$secs" | bc)"
done

echo
echo "Corrected 12s render, symbolized profile:"
perf record -o "$OUT_DIR/perf.data" --freq 400 -g -- \
    "$CLI" batch "$IN_DIR" --output-dir "$OUT_DIR/native" \
      --opna-backend native-audio --fmp-com "$FMP_COM" \
      --overwrite --max-duration 12 --quiet
echo "perf record: $OUT_DIR/perf.data"
