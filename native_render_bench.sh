#!/usr/bin/env bash
# Corrected native-audio real-time-factor benchmark for the LLE (Furnace YM2608)
# backend, applied AFTER the master-clock timeline fix.
#
# This is a MEASUREMENT-ONLY baseline. It does NOT tune the compiler or change
# production flags. It exists to make the profiling numbers reproducible and to
# enforce the accounting rules the LLE-core profile review requires:
#
#  1. Per-pair denominators come from the ACTUAL final native-session master
#     clock reported by the render (mdp_opna_get_master_clock), never from the
#     host output rate (44.1/48 kHz) times 144. The native session advances at
#     7,987,200 master-clock pairs per emulated second, i.e. one complete
#     low/high FMOPNA_Clock pair per master-clock increment.
#
#  2. Timing is reported PER RUN. Multiple runs are taken but each run's numbers
#     are printed and normalized individually. `perf stat -r N` output is NEVER
#     summed and reported as one run (the summed-repetitions x 5 accounting
#     error is explicitly avoided).
#
#  3. On Intel hybrid (P-core/E-core) processors PMU events are pinned to the
#     core PMU explicitly (cpu_core/cycles/, ...) and the enabled / running
#     percentages are recorded so scaled counts on a PMU the thread barely ran
#     on are visible rather than silently trusted.
#
#  4. Duration scaling is checked against the SAME exact library file, CLI,
#     affinity, environment and fixture. The final master clock must be
#     ~duration x 7,987,200; a large deviation flags a non-equivalent benchmark
#     path instead of being attributed to startup overhead.
#
# Artifact provenance (library absolute path, library SHA-256, ELF build ID,
# compiler flags, CLI path/MTIME, final native master clock, wall time,
# task-clock, cycles, instructions) is recorded so two reports are comparable.
#
# Usage:
#   native_render_bench.sh <ovi-dir> <FMP.COM> <output-dir> [seconds ...]
#
# Optional env:
#   MDP_BENCH_RUNS    how many individual per-duration runs (default 3)
#   MDP_BENCH_CPU     taskset CPU (or list) to pin each render to. Default is
#                     the first logical CPU reported by cpu_core/, i.e. a
#                     P-core.
#   MDP_BENCH_LIB     path to the libmdplayer_opna.so to benchmark. Defaults to
#                     the one shipped in the Release CLI's runtimes/ layout so
#                     the measured artifact exactly matches the loaded one.
#
# Example (single fixture, 3s/12s/60s, 3 runs each):
#   native_render_bench.sh /fixtures /path/FMP.COM /tmp/out 3 12 60
set -euo pipefail

IN_DIR="${1:?input OVI dir}"
FMP_COM="${2:?FMP.COM path}"
OUT_DIR="${3:?output dir}"
shift 3 || true
DURATIONS=( "$@" )
[ ${#DURATIONS[@]} -eq 0 ] && DURATIONS=(12)

RUNS="${MDP_BENCH_RUNS:-3}"

CLI="$(cd "$(dirname "$0")/../../../src/MDPlayer.Fmp.Cli/bin/Release/net8.0" && pwd)/mdplayer-render"
[ -x "$CLI" ] || { echo "Release CLI not built at $CLI" >&2; exit 1; }
[ -f "$FMP_COM" ] || { echo "FMP.COM missing: $FMP_COM" >&2; exit 1; }
[ -d "$IN_DIR" ] || { echo "input dir missing: $IN_DIR" >&2; exit 1; }
[ -d "$OUT_DIR" ] && rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

# ---------------------------------------------------------------------------
# Native library provenance. The library actually loaded is the one in the
# Release CLI's runtimes/ layout; hash THAT exact artifact.
# ---------------------------------------------------------------------------
LIB="${MDP_BENCH_LIB:-}"
default_lib="$(cd "$(dirname "$CLI")/runtimes/linux-x64/native" && pwd)/libmdplayer_opna.so"
[ -z "$LIB" ] && LIB="$default_lib"
[ -f "$LIB" ] || { echo "native library missing: $LIB" >&2; exit 1; }

NATIVE_RATE=7987200   # master-clock pairs per emulated second
OUT_RATE=48000        # CLI sample rate used by the benchmark

# Export for the pinned `bash -c` subshells that call run_once/pmu_stat.
export IN_DIR FMP_COM OUT_DIR CLI OUT_RATE LIB

# ---------------------------------------------------------------------------
# Host topology + artifact provenance
# ---------------------------------------------------------------------------
{
  echo "=== provenance ==="
  echo "git_rev=$(git -C "$(dirname "$0")/../../../.." log -1 --format=%H 2>/dev/null || echo unknown)"
  echo "fixture=$(ls -A "$IN_DIR" 2>/dev/null | tr '\n' ' ')"
  echo "cli=$CLI (mtime=$(stat -c %Y "$CLI"))"
  echo "cli_sha256=$(sha256sum "$CLI" | cut -d' ' -f1)"
  echo "lib=$LIB"
  echo "lib_sha256=$(sha256sum "$LIB" | cut -d' ' -f1)"
  echo "lib_buildid=$(readelf -n "$LIB" 2>/dev/null | awk '/Build ID/{print $3; exit}')"
  echo "cpu_model=$(grep 'model name' /proc/cpuinfo | sort -u | head -1 | sed 's/.*: //')"
  echo "cores=$(nproc)"
  echo "cpu_core_cpus=$(cat /sys/bus/event_source/devices/cpu_core/cpus 2>/dev/null)"
  echo "cpu_atom_cpus=$(cat /sys/bus/event_source/devices/cpu_atom/cpus 2>/dev/null)"
  echo "cpu0_thread_siblings=$(cat /sys/devices/system/cpu/cpu0/topology/thread_siblings_list 2>/dev/null)"
  cmake_root="$(cd "$(dirname "$0")/.." && pwd)"   # native module root (build dirs here)
  cmk=""
  if [ -f "$(dirname "$LIB")/../CMakeCache.txt" ]; then
    cmk="$(dirname "$LIB")/../CMakeCache.txt"
  elif [ -f "$cmake_root/build-release/CMakeCache.txt" ]; then
    cmk="$cmake_root/build-release/CMakeCache.txt"
  fi
  if [ -n "$cmk" ]; then
    cc=$(grep '^CMAKE_C_FLAGS:STRING=' "$cmk" 2>/dev/null || true)
    bc=$(grep '^CMAKE_BUILD_TYPE:STRING=' "$cmk" 2>/dev/null || true)
    ccc=$(grep '^CMAKE_C_COMPILER:' "$cmk" 2>/dev/null | cut -d= -f2- || true)
    echo "cmake_cache=$cmk"
    echo "cmake_build_type=${bc#*=}"
    echo "cmake_c_flags=${cc#*=}"
    echo "cmake_c_compiler=${ccc#*=}"
  else
    echo "cmake_cache=(none found)"
  fi
  echo "=== end provenance ==="
} | tee "$OUT_DIR/provenance.txt"

# ---------------------------------------------------------------------------
# One JSON-mode render. Emits WALL=, MASTER=, SAMPLES= (final native master
# clock and rendered stereo frames from the batch JSON tracks[0]).
# ---------------------------------------------------------------------------
run_once() { # $1=seconds
  local secs="$1" start end wall tmp json
  tmp="$(mktemp)"
  start=$(date +%s.%N)
  "$CLI" batch "$IN_DIR" --output-dir "$OUT_DIR/native" \
      --opna-backend native-audio --fmp-com "$FMP_COM" \
      --sample-rate "$OUT_RATE" \
      --overwrite --max-duration "$secs" --quiet --json >"$tmp" 2>/dev/null
  end=$(date +%s.%N)
  wall=$(echo "$end - $start" | bc)
  json="$(jq -c '.tracks[0] // {}' "$tmp")"
  rm -f "$tmp"
  echo "WALL=$wall"
  echo "SUCCESS=$(printf '%s' "$json" | jq -r '.success // false')"
  echo "MASTER=$(printf '%s' "$json" | jq -r '.finalOpnaMasterClock // 0')"
  echo "SAMPLES=$(printf '%s' "$json" | jq -r '.renderedSamples // 0')"
}

# ---------------------------------------------------------------------------
# One explicit core-PMU stat run. Enabled/running % are captured with the raw
# counts so a scaled event is obvious. Per-run only (no aggregation).
# ---------------------------------------------------------------------------
pmu_stat() { # $1=seconds
  local secs="$1"
  perf stat -o "$OUT_DIR/pmu_${secs}s.txt" \
    -e cpu_core/cycles/ -e cpu_core/instructions/ \
    -e cpu_core/L1-icache-load-misses/ \
    -e cpu_core/cache-misses/ -e cpu_core/cache-references/ \
    "$CLI" batch "$IN_DIR" --output-dir "$OUT_DIR/native" \
      --opna-backend native-audio --fmp-com "$FMP_COM" \
      --sample-rate "$OUT_RATE" \
      --overwrite --max-duration "$secs" --quiet --json >/dev/null 2>&1 || true
  cat "$OUT_DIR/pmu_${secs}s.txt"
}

# ---------------------------------------------------------------------------
# Pin to a single P-core by default (first logical CPU in cpu_core/).
# ---------------------------------------------------------------------------
if [ -n "${MDP_BENCH_CPU:-}" ]; then
  P="${MDP_BENCH_CPU}"
else
  P="$(cut -d',' -f1 < /sys/bus/event_source/devices/cpu_core/cpus | cut -d'-' -f1)"
fi
TASKSET=(taskset -c "$P")

echo
echo "pinning each render to cpu_core logical CPU=$P"
printf '%-8s %-10s %-11s %-14s %-14s %-10s %-7s %s\n' \
  "run" "wall" "rt_factor" "final_master" "exp_master" "samples" "status" "master/exp"

for secs in "${DURATIONS[@]}"; do
  echo "--- PMU stat (core PMU), ${secs}s ---"
  "${TASKSET[@]}" bash -c "$(declare -f pmu_stat); pmu_stat \"$secs\"" || true

  for ((run=1; run<=RUNS; run++)); do
    out="$("${TASKSET[@]}" bash -c "$(declare -f run_once); run_once \"$secs\"")"
    wall="$(echo "$out" | awk -F= '$1=="WALL"{print $2}')"
    master="$(echo "$out" | awk -F= '$1=="MASTER"{print $2}')"
    samples="$(echo "$out" | awk -F= '$1=="SAMPLES"{print $2}')"
    succ="$(echo "$out" | awk -F= '$1=="SUCCESS"{print $2}')"
    expected="$(echo "$secs * $NATIVE_RATE" | bc)"
    rt="$(echo "scale=4; $wall / $secs" | bc)"
    if [ "$succ" = true ] && [ "${master:-0}" -gt 0 ] 2>/dev/null; then
      ratio="$(echo "scale=4; $master / $expected" | bc)"
      dev="$(echo "scale=2; ($master-$expected)*100/$expected" | bc)"
    else
      ratio="n/a"; dev="n/a"
    fi
    printf '%-8s %-10s %-11s %-14s %-14s %-10s %-7s %s\n' \
      "run$run@${secs}s" "$wall" "$rt" "$master" "$expected" "$samples" \
      "$succ" "$ratio (dev ${dev}%)"
  done
done

echo
echo "done. PMU stat in $OUT_DIR/pmu_<secs>s.txt ; provenance in $OUT_DIR/provenance.txt"
