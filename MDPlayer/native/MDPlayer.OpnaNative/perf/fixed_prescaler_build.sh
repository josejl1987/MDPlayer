#!/usr/bin/env bash
# Experiment C: fixed-prescaler (select=2) specialization build + provenance.
#
# Derives mdp_fmopna_clock_fixedpresc from the SAME vendored body
# (tools/gen_fixed_prescaler.py, --select 2 matching the adapter session path's
# IC-reset/Furnace-default), builds a library that routes the adapter's
# FMOPNA_Clock half-edge calls through it (-DMDPLAYER_OPNA_FIXED_PRESCALER=ON),
# and writes provenance (SHA-256 / build ID / text size / flags). Measure it
# with tools/core_speed + `perf stat -e cpu_core/*` for an apples-to-apples
# comparison with baseline / Experiments A and B.
#
# Bit-exact only while the prescaler select stays 2; opt-in, never ships.
#
# Usage:
#   fixed_prescaler_build.sh [build_dir] [out_lib]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"          # native module root
BUILD="${1:-$ROOT/build-c}"
OUT_ROOT="$BUILD/out"
OUT_LIB="${2:-$OUT_ROOT/libmdplayer_opna_fixedpresc.so}"
mkdir -p "$(dirname "$OUT_LIB")"

cmake -S "$ROOT" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DMDPLAYER_OPNA_FIXED_PRESCALER=ON >/dev/null

echo "== building fixed-prescaler library =="
cmake --build "$BUILD" --target mdplayer_opna >/dev/null

cp "$BUILD/libmdplayer_opna.so" "$OUT_LIB"

{
  echo "=== fixed-prescaler artifact (experiment C, select=2) ==="
  echo "lib=$OUT_LIB"
  echo "lib_sha256=$(sha256sum "$OUT_LIB" | cut -d' ' -f1)"
  echo "lib_buildid=$(readelf -n "$OUT_LIB" 2>/dev/null | awk '/Build ID/{print $3; exit}')"
  echo "text_size=$(size "$OUT_LIB" | awk 'NR==2{print $1}')"
  echo "data_size=$(size "$OUT_LIB" | awk 'NR==2{print $2}')"
  echo "fn_size=$(nm -S --size-sort "$OUT_LIB" | awk '$4=="mdp_fmopna_clock_fixedpresc"{print "0x"$2; exit}')"
  echo "prescaler_select=2"
  echo "cmake_build_type=$(grep '^CMAKE_BUILD_TYPE:STRING=' "$BUILD/CMakeCache.txt" | cut -d= -f2)"
  echo "cmake_c_flags=$(grep '^CMAKE_C_FLAGS:STRING=' "$BUILD/CMakeCache.txt" | cut -d= -f2)"
  echo "=== end ==="
} | tee "$OUT_ROOT/fixedpresc_provenance.txt"

echo
echo "fixed-prescaler .so: $OUT_LIB"
echo "Provenance: $OUT_ROOT/fixedpresc_provenance.txt"
