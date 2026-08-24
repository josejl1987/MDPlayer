#!/usr/bin/env bash
# Experiment A: GCC Profile-Guided Optimization build + measurement harness.
#
# Builds a two-phase PGO variant of the native YM2608-LLE shared library,
# trains it on a representative real FM trace, and produces the artifacts the
# profile review asks for: the PGO .so, its text size, and a provenance record
# (SHA-256 / build ID / flags). The resulting .so is ready to measure with
# native_render_bench.sh by pointing MDP_BENCH_LIB at it.
#
# Because PGO only reorders blocks / guides branch layout (it never changes the
# floating sign-of-undefined behavior the vendored core performs by design),
# it is orthogonal to the existing UBSan exemption and to the "measurement only"
# invariant: this script does NOT change -march, LTO, or unrolling.
#
# Resources used: a fresh build dir (default <module>/build-pgo) configured with
# MDPLAYER_OPNA_PGO=GENERATE, then reconfigured to USE in-place (same object
# paths => .gcda keys stay aligned).
#
# Usage:
#   pgo_build.sh [--frames N] [out_lib]
# where --frames N is the size of the training advance (master-clock pairs),
# and out_lib is where the PGO .so is copied (default <out>/libmdplayer_opna_pgo.so).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"          # native module root
BUILD="$ROOT/build-pgo"
OUT_ROOT="$BUILD/out"

FRAMES=30000000   # training advance in master-clock pairs
if [ "${1:-}" = "--frames" ]; then
    FRAMES="${2:?--frames needs a value}"
    shift 2 || true
elif [ $# -ge 1 ]; then
    BUILD="${1}"
fi
OUT_ROOT="$BUILD/out"
mkdir -p "$OUT_ROOT"

cmake -S "$ROOT" -B "$BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DMDPLAYER_OPNA_PGO=GENERATE >/dev/null

echo "== Phase 1/2: instrumented GENERATE build ==" 
cmake --build "$BUILD" --target mdplayer_opna mdplayer_opna_pgo_train >/dev/null

echo "== running instrumented training driver (real FM trace, ${FRAMES} pairs) =="
traindir="$BUILD"/pgo-train
mkdir -p "$traindir"
env LD_LIBRARY_PATH="$BUILD:${LD_LIBRARY_PATH:-}" "$BUILD/mdplayer_opna_pgo_train" "$FRAMES" \
    >/dev/null 2>"$traindir/train.stderr"
cat "$traindir/train.stderr"
echo "gcda count: $(find "$BUILD" -name '*.gcda' | wc -l)"
echo "  (sample): $(find "$BUILD" -name '*.gcda' | head -4 | tr '\n' ' ')"

echo "== Phase 2/2: profile-use rebuild (same build dir => aligned .gcda keys) =="
cmake --build "$BUILD" --target mdplayer_opna >/dev/null

# Collect the PGO artifact + provenance.
LIB="${LIB:-$BUILD/libmdplayer_opna.so}"
OUT_LIB="$OUT_ROOT/libmdplayer_opna_pgo.so"
cp "$LIB" "$OUT_LIB"

{
  echo "=== PGO artifact ==="
  echo "lib=$OUT_LIB"
  echo "lib_sha256=$(sha256sum "$OUT_LIB" | cut -d' ' -f1)"
  echo "lib_buildid=$(readelf -n "$OUT_LIB" 2>/dev/null | awk '/Build ID/{print $3; exit}')"
  echo "text_size=$(size "$OUT_LIB" | awk 'NR==2{print $1}')"
  echo "data_size=$(size "$OUT_LIB" | awk 'NR==2{print $2}')"
  echo "training_pairs=$FRAMES"
  echo "cmake_build_type=$(grep '^CMAKE_BUILD_TYPE:STRING=' "$BUILD/CMakeCache.txt" | cut -d= -f2)"
  echo "cmake_c_flags=$(grep '^CMAKE_C_FLAGS:STRING=' "$BUILD/CMakeCache.txt" | cut -d= -f2)"
  echo "=== end ==="
} | tee "$OUT_ROOT/pgo_provenance.txt"

echo
echo "PGO .so: $OUT_LIB"
echo "Provenance: $OUT_ROOT/pgo_provenance.txt"
echo "Measure it with native_render_bench.sh -- MDP_BENCH_LIB=$OUT_LIB"
