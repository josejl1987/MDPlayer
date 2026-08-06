#!/usr/bin/env bash
# Experiment B: compile-time low/high phase specialization build + provenance.
#
# Generates mdp_fmopna_clock_low/high from the SAME vendored body
# (tools/gen_phase_specialize.py), builds a library that routes the adapter's
# FMOPNA_Clock half-edge calls through them (-DMDPLAYER_OPNA_PHASE_SPEC=ON), and
# writes a provenance record (SHA-256 / build ID / text size / flags). The
# resulting .so is measured with tools/core_speed + `perf stat -e cpu_core/*`,
# exactly like PGO (Experiment A), for an apples-to-apples comparison.
#
# This does NOT change -march, LTO, or unrolling; it only changes the derivation
# of the phase, so it is a controlled experiment against the measurement.
#
# Usage:
#   phase_spec_build.sh [build_dir] [out_lib]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"          # native module root
BUILD="${1:-$ROOT/build-phase}"
OUT_ROOT="$BUILD/out"
OUT_LIB="${2:-$OUT_ROOT/libmdplayer_opna_phase.so}"
mkdir -p "$(dirname "$OUT_LIB")"

cmake -S "$ROOT" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DMDPLAYER_OPNA_PHASE_SPEC=ON >/dev/null

echo "== building phase-specialized library =="
cmake --build "$BUILD" --target mdplayer_opna >/dev/null

cp "$BUILD/libmdplayer_opna.so" "$OUT_LIB"

{
  echo "=== phase-specialization artifact (experiment B) ==="
  echo "lib=$OUT_LIB"
  echo "lib_sha256=$(sha256sum "$OUT_LIB" | cut -d' ' -f1)"
  echo "lib_buildid=$(readelf -n "$OUT_LIB" 2>/dev/null | awk '/Build ID/{print $3; exit}')"
  echo "text_size=$(size "$OUT_LIB" | awk 'NR==2{print $1}')"
  echo "data_size=$(size "$OUT_LIB" | awk 'NR==2{print $2}')"
  echo "low_size=$(nm -S --size-sort "$OUT_LIB" | awk '$4=="mdp_fmopna_clock_low"{print "0x"$2; exit}')"
  echo "high_size=$(nm -S --size-sort "$OUT_LIB" | awk '$4=="mdp_fmopna_clock_high"{print "0x"$2; exit}')"
  echo "monolith_size=57901"
  echo "cmake_build_type=$(grep '^CMAKE_BUILD_TYPE:STRING=' "$BUILD/CMakeCache.txt" | cut -d= -f2)"
  echo "cmake_c_flags=$(grep '^CMAKE_C_FLAGS:STRING=' "$BUILD/CMakeCache.txt" | cut -d= -f2)"
  echo "=== end ==="
} | tee "$OUT_ROOT/phase_provenance.txt"

echo
echo "phase-spec .so: $OUT_LIB"
echo "Provenance: $OUT_ROOT/phase_provenance.txt"
echo "Measure with: perf stat -r 2 -e cpu_core/cycles/ -e cpu_core/instructions/ -e cpu_core/L1-icache-load-misses/ taskset -c <P> $ROOT/build-release/mdplayer_opna_core_speed $OUT_LIB 20000000"
