/*
 * QW3 measurement driver (tests/signal_freq.c).
 *
 * Replays the Furnace reference trace through the production session ABI,
 * then advances a master-clock window while counting per-signal activity in
 * the hot clock function (built with --count-events instrumentation,
 * tools/gen_quickwin_core.py). Prints half-edge counts and per-pair rates
 * for:
 *
 *   calls | reset (chip in reset, input.ic==0) | write0 | write0_en |
 *   write1 | write1_en | write2 | write2_en | write3 | write3_en |
 *   read0 | read2 | read3 | ssg_write0 | ssg_write1 | ssg_read1
 *
 * The counter order must stay in sync with _EV_INJECTIONS in
 * tools/gen_quickwin_core.py. A runtime sanity check (calls must equal
 * exactly 2 x pairs) catches any drift immediately.
 *
 * Built only when MDPLAYER_OPNA_QW3_COUNT_EVENTS is ON. Not a correctness
 * gate; it is the frequency measurement that scopes the QW3 write/read-event
 * extraction (perf/README.md "Quick wins").
 *
 * Usage: signal_freq [window_pairs=20000000]
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna.h"
#include "../tests/furnace_reference_fixture.c"

#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Counter order — keep in sync with tools/gen_quickwin_core.py _EV_INJECTIONS. */
enum {
    EV_CALLS = 0,
    EV_RESET = 1,
    EV_WRITE0 = 2,
    EV_WRITE0_EN = 3,
    EV_WRITE1 = 4,
    EV_WRITE1_EN = 5,
    EV_WRITE2 = 6,
    EV_WRITE2_EN = 7,
    EV_WRITE3 = 8,
    EV_WRITE3_EN = 9,
    EV_READ0 = 10,
    EV_READ2 = 11,
    EV_READ3 = 12,
    EV_SSG_WRITE0 = 13,
    EV_SSG_WRITE1 = 14,
    EV_SSG_READ1 = 15,
    EV_COUNT = 16,
};

extern void mdp_qw3_ev_reset(void);
extern void mdp_qw3_ev_get(uint64_t *out, int n);

static const char *const kNames[EV_COUNT] = {
    "calls", "reset", "write0", "write0_en", "write1", "write1_en",
    "write2", "write2_en", "write3", "write3_en", "read0", "read2",
    "read3", "ssg_write0", "ssg_write1", "ssg_read1",
};

int main(int argc, char **argv)
{
    long long window = (argc >= 2) ? atoll(argv[1]) : 20000000LL;
    if (window <= 0) window = 20000000LL;

    char err[64];
    mdp_opna_session *s = NULL;
    if (mdp_opna_open(&(mdp_opna_open_options){ 48000 }, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "open failed: %s\n", err);
        return 2;
    }
    for (int i = 0; i < kFurnaceRefWriteCount; i++) {
        int rc = mdp_opna_write_register(s, (uint32_t)kFurnaceRefWrites[i].master_clock,
                                         (uint8_t)kFurnaceRefWrites[i].bank,
                                         (uint8_t)kFurnaceRefWrites[i].reg,
                                         (uint8_t)kFurnaceRefWrites[i].value);
        if (rc != MDP_OPNA_OK && rc != MDP_OPNA_ERR_CLOCK_REGRESSION &&
            rc != MDP_OPNA_ERR_UNSUPPORTED_CADENCE) {
            fprintf(stderr, "write %d fail rc=%d\n", i, rc);
            mdp_opna_close(s);
            return 2;
        }
    }

    /* Replay done (trace writes span the first ~124k pairs). Start counting
     * only for the measured window, like core_speed. */
    uint64_t start_clk = mdp_opna_get_master_clock(s);
    mdp_qw3_ev_reset();

    enum { CHUNK = 200000 };
    uint64_t target = start_clk + (uint64_t)window;
    int16_t buf[128];
    while (mdp_opna_get_master_clock(s) < target) {
        uint64_t cur = mdp_opna_get_master_clock(s);
        uint64_t step = (target - cur < CHUNK) ? (target - cur) : CHUNK;
        if (mdp_opna_advance_to(s, cur + step) != MDP_OPNA_OK) {
            fprintf(stderr, "advance failed rc at clock %" PRIu64 "\n", cur);
            mdp_opna_close(s);
            return 2;
        }
        for (;;) {
            int d = -1;
            if (mdp_opna_drain_audio(s, buf, 64, &d) != MDP_OPNA_OK) break;
            if (d <= 0) break;
        }
    }
    uint64_t end_clk = mdp_opna_get_master_clock(s);
    mdp_opna_close(s);

    uint64_t ev[EV_COUNT];
    mdp_qw3_ev_get(ev, EV_COUNT);
    uint64_t pairs = end_clk - start_clk;
    uint64_t calls = ev[EV_CALLS];

    printf("window pairs: %" PRIu64 " (asked %lld)\n", pairs, window);
    if (calls != 2 * pairs) {
        fprintf(stderr, "FATAL: counter drift — calls=%" PRIu64 " != 2*pairs=%" PRIu64
                        "; counter enum out of sync with the generator\n",
                calls, 2 * pairs);
        return 3;
    }

    printf("%-14s %16s %16s %14s\n", "signal", "half-edges", "per-pair rate", "% of pairs");
    for (int i = 1; i < EV_COUNT; i++) {
        double rate = pairs ? (double)ev[i] / (double)pairs : 0.0;
        double pct = rate * 100.0;
        printf("%-14s %16" PRIu64 " %16.6f %13.4f%%\n",
               kNames[i], ev[i], rate, pct);
    }

    uint64_t any_write_en = ev[EV_WRITE0_EN] + ev[EV_WRITE1_EN] +
                            ev[EV_WRITE2_EN] + ev[EV_WRITE3_EN];
    uint64_t any_read = ev[EV_READ0] + ev[EV_READ2] + ev[EV_READ3] + ev[EV_SSG_READ1];
    uint64_t any_bus = ev[EV_WRITE0] + ev[EV_WRITE1] + ev[EV_WRITE2] + ev[EV_WRITE3];
    printf("---\n");
    printf("any delayed write strobe (write*_en sum): %" PRIu64 " half-edges (%.6f/pair)\n",
           any_write_en, pairs ? (double)any_write_en / (double)pairs : 0.0);
    printf("any read strobe (read*/ssg_read1 sum):   %" PRIu64 " half-edges (%.6f/pair)\n",
           any_read, pairs ? (double)any_read / (double)pairs : 0.0);
    printf("any raw bus access (write*/read* sum):   %" PRIu64 " half-edges (%.6f/pair)\n",
           any_bus + ev[EV_READ0] + ev[EV_READ2] + ev[EV_READ3],
           pairs ? (double)(any_bus + ev[EV_READ0] + ev[EV_READ2] + ev[EV_READ3]) / (double)pairs : 0.0);
    return 0;
}
