/*
 * PGO training driver (Experiment A).
 *
 * Purpose: emit GCC profile (`.gcda`) data for the vendored YM2608-LLE core's
 * hot path, `FMOPNA_Clock`, using REPRESENTATIVE real FMP trace data rather
 * than a synthetic microbenchmark.
 *
 * This driver links the SHARED library (mdplayer_opna) and drives it through
 * the public session ABI so the profile data is generated from the exact
 * object files that get rebuilt with `-fprofile-use`, keeping the .gcda keys
 * aligned. It replays the same checked-in real FM trace prefix the Stage-1
 * probe uses (a bounded prefix of XA2047.events.jsonl — a genuine capture of
 * OPNA bus writes from FMP playback), then advances the session through a large
 * window of clock pairs so FMOPNA_Clock is exercised with representative data.
 * The fixed-144 cadence guard holds for this trace (see native_cadence_test),
 * so resampling stays in the supported production profile.
 *
 * This binary is built ONLY in the PGO tree (-DMDPLAYER_OPNA_PGO=*); it is not
 * shipped and is not a correctness gate. It takes an optional window of
 * master-clock pairs (default 300,000,000).
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna.h"
#include "../tests/furnace_reference_fixture.c"

#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    long long window = 30000000LL;      /* master-clock pairs to advance */
    if (argc >= 2) {
        window = atoll(argv[1]);
        if (window <= 0) window = 30000000LL;
    }

    mdp_opna_open_options opt = { 48000 };
    mdp_opna_session *s = NULL;
    char err[64];
    if (mdp_opna_open(&opt, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "pgo_train: open failed: %s\n", err);
        return 2;
    }

    /* Replay the real trace through the production bus. */
    for (int i = 0; i < kFurnaceRefWriteCount; i++) {
        int rc = mdp_opna_write_register(
            s, kFurnaceRefWrites[i].master_clock, (uint8_t)kFurnaceRefWrites[i].bank,
            (uint8_t)kFurnaceRefWrites[i].reg, (uint8_t)kFurnaceRefWrites[i].value);
        if (rc != MDP_OPNA_OK && rc != MDP_OPNA_ERR_CLOCK_REGRESSION &&
            rc != MDP_OPNA_ERR_UNSUPPORTED_CADENCE) {
            fprintf(stderr, "pgo_train: write %d fail rc=%d\n", i, rc);
            mdp_opna_close(s);
            return 2;
        }
    }

    /* Advance a large window in bounded chunks, draining the FIFO each time so
     * the resampling path stays active and the FIFO never overflows (mirrors
     * the production host loop). This keeps FMOPNA_Clock running the
     * representative hot loop for `window` total pairs. */
    enum { CHUNK = 200000 };         /* master-clock pairs per advance */
    uint64_t start = mdp_opna_get_master_clock(s);
    uint64_t target = start + (uint64_t)window;
    int16_t buf[128];
    long long drained_total = 0;
    while (mdp_opna_get_master_clock(s) < target) {
        uint64_t cur = mdp_opna_get_master_clock(s);
        uint64_t step = (target - cur < CHUNK) ? (target - cur) : CHUNK;
        int rc = mdp_opna_advance_to(s, cur + step);
        if (rc != MDP_OPNA_OK) {
            fprintf(stderr, "pgo_train: advance failed rc=%d at clock %llu\n",
                    rc, (unsigned long long)cur);
            mdp_opna_close(s);
            return 2;
        }
        /* Drain completely: loop until the FIFO is empty so no backlog grows
         * between chunks (native rate 55,467 fps >> 48k output rate). */
        for (;;) {
            int drained = -1;
            if (mdp_opna_drain_audio(s, buf, 64, &drained) != MDP_OPNA_OK)
                break;
            if (drained <= 0) break;
            drained_total += drained;
        }
    }

    uint64_t end = mdp_opna_get_master_clock(s);
    fprintf(stderr, "pgo_train: advanced %llu pairs (drained=%lld)\n",
            (unsigned long long)(end - start), drained_total);
    mdp_opna_close(s);
    return 0;
}
