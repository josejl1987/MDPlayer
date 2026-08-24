#!/bin/bash
# Quick-win benchmark: one build dir per flag combination, core_speed harness
# pinned to a P-core, cpu_core PMU events, -r 3 (perf/README.md methodology:
# rule 1 divide by the final master clock; rule 3 pin to a P-core; rule 4 use
# explicit cpu_core events).
#
# Usage: quickwin_bench.sh [window_pairs] [p_core]
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
cd "$HERE"
WINDOW="${1:-20000000}"
CORE="${2:-4}"
EVENTS=(-e cpu_core/cycles/ -e cpu_core/instructions/ \
        -e cpu_core/L1-icache-load-misses/ -e cpu_core/cache-references/ \
        -e cpu_core/cache-misses/ -e cpu_core/branch-misses/)
RUNS=3
for d in build-qw0 build-qw1 build-qw2 build-qw12 build-qw1245 build-all; do
    [ -x "$d/mdplayer_opna_core_speed" ] || { echo "skip $d (not built)"; continue; }
    echo "==== $d (window=$WINDOW, -r $RUNS, taskset -c $CORE)"
    perf stat -r "$RUNS" "${EVENTS[@]}" \
        taskset -c "$CORE" "$d/mdplayer_opna_core_speed" \
        "$d/libmdplayer_opna.so" "$WINDOW" 2>&1 \
        | grep -E "cpu_core/(cycles|instructions|L1-icache-load-misses|cache-references|cache-misses|branch-misses)|core_speed:" \
        | sed "s/^/  /"
done
