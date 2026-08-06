/*
 * Native core speed benchmark (Experiment A comparison harness).
 *
 * Loads ANY mdplayer_opna .so (baseline or PGO) via dlopen at runtime and
 * drives it through the public session ABI over a fixed window of master-clock
 * pairs, using the same real FM trace template as the PGO trainer. Prints the
 * emulated master-clock pairs advanced, the wall time, and the throughput
 * (pairs/second). This isolates FMOPNA_Clock throughput on each artifact with
 * the identical harness, so baseline vs PGO is a like-for-like comparison
 * (the fixed-144 cadence holds for this trace, so both use the same production
 * resampling profile).
 *
 * The caller typically wraps this in `perf stat -e cpu_core/cycles/ ...` to get
 * cycles per pair. Not a correctness gate; built only for the perf experiment.
 *
 * Usage:
 *   core_speed <lib.so> [window_pairs=30000000]
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <dlfcn.h>
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "../tests/furnace_reference_fixture.c"

typedef struct mdp_opna_session mdp_opna_session;
typedef struct mdp_opna_open_options {
    int sample_rate;
} mdp_opna_open_options;
enum {
    MDP_OPNA_OK = 0,
    MDP_OPNA_ERR_CLOCK_REGRESSION = -3,
    MDP_OPNA_ERR_UNSUPPORTED_CADENCE = -6,
};

static double now_s(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + (double)ts.tv_nsec / 1e9;
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: core_speed <lib.so> [window_pairs]\n");
        return 2;
    }
    const char *libpath = argv[1];
    long long window = (argc >= 3) ? atoll(argv[2]) : 30000000LL;
    if (window <= 0) window = 30000000LL;

    void *h = dlopen(libpath, RTLD_NOW | RTLD_LOCAL);
    if (!h) { fprintf(stderr, "dlopen %s: %s\n", libpath, dlerror()); return 2; }

    int (*open_fn)(const mdp_opna_open_options *, mdp_opna_session **, char *, size_t);
    int (*write_fn)(mdp_opna_session *, uint32_t, uint8_t, uint8_t, uint8_t);
    int (*advance_fn)(mdp_opna_session *, uint64_t);
    int (*drain_fn)(mdp_opna_session *, int16_t *, int, int *);
    uint64_t (*mclock_fn)(const mdp_opna_session *);
    void (*close_fn)(mdp_opna_session *);

    *(void **)(&open_fn)    = dlsym(h, "mdp_opna_open");
    *(void **)(&write_fn)   = dlsym(h, "mdp_opna_write_register");
    *(void **)(&advance_fn) = dlsym(h, "mdp_opna_advance_to");
    *(void **)(&drain_fn)   = dlsym(h, "mdp_opna_drain_audio");
    *(void **)(&mclock_fn)  = dlsym(h, "mdp_opna_get_master_clock");
    *(void **)(&close_fn)   = dlsym(h, "mdp_opna_close");
    if (!open_fn || !write_fn || !advance_fn || !drain_fn || !mclock_fn || !close_fn) {
        fprintf(stderr, "dlsym failed: %s\n", dlerror());
        return 2;
    }

    char err[64];
    mdp_opna_session *s = NULL;
    if (open_fn(&(mdp_opna_open_options){ 48000 }, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "open failed: %s\n", err);
        dlclose(h);
        return 2;
    }
    for (int i = 0; i < kFurnaceRefWriteCount; i++) {
        int rc = write_fn(s, (uint32_t)kFurnaceRefWrites[i].master_clock,
                          (uint8_t)kFurnaceRefWrites[i].bank,
                          (uint8_t)kFurnaceRefWrites[i].reg,
                          (uint8_t)kFurnaceRefWrites[i].value);
        if (rc != MDP_OPNA_OK && rc != MDP_OPNA_ERR_CLOCK_REGRESSION &&
            rc != MDP_OPNA_ERR_UNSUPPORTED_CADENCE) {
            fprintf(stderr, "write %d fail rc=%d\n", i, rc);
            close_fn(s); dlclose(h);
            return 2;
        }
    }

    enum { CHUNK = 200000 };
    uint64_t start_clk = mclock_fn(s);
    uint64_t target = start_clk + (uint64_t)window;
    int16_t buf[128];
    long long drained = 0;
    double t0 = now_s();
    while (mclock_fn(s) < target) {
        uint64_t cur = mclock_fn(s);
        uint64_t step = (target - cur < CHUNK) ? (target - cur) : CHUNK;
        if (advance_fn(s, cur + step) != MDP_OPNA_OK) {
            fprintf(stderr, "advance failed rc at clock %" PRIu64 "\n", cur);
            close_fn(s); dlclose(h);
            return 2;
        }
        for (;;) {
            int d = -1;
            if (drain_fn(s, buf, 64, &d) != MDP_OPNA_OK) break;
            if (d <= 0) break;
            drained += d;
        }
    }
    double t1 = now_s();
    uint64_t end_clk = mclock_fn(s);
    uint64_t advanced = end_clk - start_clk;
    double secs = t1 - t0;
    fprintf(stderr, "core_speed: %s window=%lld advanced=%llu pairs in %.3fs => %.2f pairs/s (drained=%lld)\n",
            libpath, window, (unsigned long long)advanced,
            secs, secs > 0 ? (double)advanced / secs : 0.0, drained);
    printf("%llu\n", (unsigned long long)advanced);   /* machine-readable pairs for the shell */
    close_fn(s);
    dlclose(h);
    return 0;
}
