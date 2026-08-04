/*
 * OPNA status-read cadence regression (Prompt 8.1, "First action").
 *
 * A status read must be cadence-neutral: it must not advance the master clock
 * and must not run (or discard) the serial decoder, so every raw native
 * frame-completion interval stays exactly 144 master clocks and no
 * unsupported-cadence error is ever latched — regardless of how densely and
 * at which phase of the 144-clock frame period the OPNA status registers are
 * polled.
 *
 * This regression does NOT involve FMP.COM. It drives the session ABI
 * directly:
 *   1. open a native session at 48 kHz,
 *   2. reset it,
 *   3. advance through enough master clocks to establish stable native output,
 *   4. record raw native frame-completion timestamps (from the FIFO) and
 *      require every interval to be exactly 144,
 *   5. repeat the same requirement while injecting bank-0 status reads,
 *   6. repeat with bank-1 status reads,
 *   7. repeat with dense status polling at several positions inside the
 *      144-clock frame period.
 *
 * We inspect the RAW native frame-completion timestamps (mdp_opna_fifo_pop),
 * never only the resampled output-frame counts, so a dropped/merged serial
 * frame is caught exactly.
 *
 * The executable runs a single scenario when given `-t <key>` (used to
 * register each named CTest) or all scenarios when run bare.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_session.h"
#include "../src/mdplayer_opna_fifo.h"

#include <inttypes.h>
#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "  FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

/* This regression's fixed native frame cadence. */
enum { FRAME_CLOCKS = 144 };

/*
 * Open a session at 48 kHz, reset it, and advance far enough to establish
 * stable native frame output (>= ~48 frames), leaving a clean FIFO for the
 * caller. On failure increments the global counter and returns NULL.
 */
static mdp_opna_session *open_established(mdp_opna_open_options *opt)
{
    mdp_opna_session *s = NULL;
    char err[64];
    if (mdp_opna_open(opt, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "open failed: %s\n", err);
        failures++;
        return NULL;
    }
    mdp_opna_reset_chip(s);
    int rc = mdp_opna_advance_to(s, FRAME_CLOCKS * 60u);
    if (rc != MDP_OPNA_OK) {
        fprintf(stderr, "establish advance failed: %d\n", rc);
        failures++;
        mdp_opna_close(s);
        return NULL;
    }
    /* Drop warm-up frames; the cadence guard is already synced. */
    mdp_opna_fifo_reset(&s->fifo);
    return s;
}

/*
 * Advance `frames` frame periods, then drain `frames` raw frame-completion
 * clocks and require every interval to be exactly 144. Also asserts no
 * cadence error was latched.
 */
static void advance_and_check(mdp_opna_session *s, const char *tag, int frames)
{
    uint64_t target = mdp_opna_get_master_clock(s) + (uint64_t)frames * FRAME_CLOCKS;
    int rc = mdp_opna_advance_to(s, target);
    CHECK(rc == MDP_OPNA_OK, "advance_to after reads failed");
    if (rc != MDP_OPNA_OK)
        return;

    int bad = 0;
    uint64_t prev = 0;
    int first = 1;
    mdp_opna_timed_frame fr;
    while (mdp_opna_fifo_pop(&s->fifo, &fr)) {
        if (!first && fr.master_clock - prev != FRAME_CLOCKS) {
            fprintf(stderr, "  %s: bad interval=%" PRIu64 " at clock %" PRIu64
                            " (expected %d)\n",
                    tag, fr.master_clock - prev,
                    fr.master_clock, FRAME_CLOCKS);
            bad++;
        }
        prev = fr.master_clock;
        first = 0;
    }
    CHECK(bad == 0, "non-144 raw frame interval detected after status reads");
    CHECK(!s->cadence_error, "unsupported-cadence error latched after status reads");
    if (bad != 0)
        fprintf(stderr, "  %s: %d non-144 interval(s) over %d frames\n",
                tag, bad, frames);
}

/*
 * Poll status `reads` times at each of `phases` offsets within every frame
 * period, for `frames` consecutive periods. `phase_offsets[]` are the offsets
 * (0 <= offset < 144) at which a dense cluster of reads is injected. This
 * stresses every position in the frame period including the boundary-1 offset
 * (offset 143) that previously dropped the 144-th clock frame.
 */
static void run_phase_polling(mdp_opna_open_options *opt, const char *tag,
                              uint8_t bank, int frames, int cluster,
                              const int *phase_offsets, int n_phases)
{
    mdp_opna_session *s = open_established(opt);
    if (!s)
        return;

    uint8_t st = 0;
    uint64_t tick = mdp_opna_get_master_clock(s); /* on a frame boundary */
    for (int f = 0; f < frames; f++) {
        for (int ph = 0; ph < n_phases && phase_offsets[ph] < FRAME_CLOCKS; ph++) {
            uint64_t here = tick + phase_offsets[ph];
            for (int r = 0; r < cluster; r++) {
                int rc = mdp_opna_read_status(s, here, bank, &st);
                CHECK(rc == MDP_OPNA_OK, "phase-poll status read failed");
            }
        }
        tick += FRAME_CLOCKS;
    }

    advance_and_check(s, tag, frames);
    mdp_opna_close(s);
}

/* Baseline: no status reads at all, 32 consecutive raw intervals == 144. */
static void run_baseline(mdp_opna_open_options *opt)
{
    mdp_opna_session *s = open_established(opt);
    if (!s)
        return;
    advance_and_check(s, "baseline", 32);
    mdp_opna_close(s);
}

/* Bank-0 / bank-1 reads spread through the frame period. */
static void run_bank_reads(mdp_opna_open_options *opt, const char *tag, uint8_t bank)
{
    static const int phases[] = { 0, 20, 40, 60, 80, 100, 120, 143 };
    run_phase_polling(opt, tag, bank, 32, /*cluster*/3,
                      phases, (int)(sizeof(phases) / sizeof(phases[0])));
}

/* Dense polling: several reads at many offsets inside each 144-clock frame. */
static void run_dense_polling(mdp_opna_open_options *opt)
{
    /* Poll at 16 evenly spaced offsets plus the boundary-1 offset, several
     * reads each, every frame period, for both banks. */
    static const int phases[] = { 0, 8, 16, 24, 32, 40, 48, 56,
                                  64, 72, 80, 88, 96, 104, 112, 120, 128, 136, 143 };
    run_phase_polling(opt, "dense-bank0", 0, 32, 5,
                      phases, (int)(sizeof(phases) / sizeof(phases[0])));
    run_phase_polling(opt, "dense-bank1", 1, 32, 5,
                      phases, (int)(sizeof(phases) / sizeof(phases[0])));
}

int main(int argc, char **argv)
{
    const char *only = NULL;
    if (argc >= 3 && strcmp(argv[1], "-t") == 0)
        only = argv[2];

    mdp_opna_open_options opt = { 48000 };

    if (!only || strcmp(only, "baseline_cadence_144") == 0) {
        if (!only) printf("scenario: baseline_cadence_144\n");
        run_baseline(&opt);
    }
    if (!only || strcmp(only, "bank0_reads_keep_144") == 0) {
        if (!only) printf("scenario: bank0_reads_keep_144\n");
        run_bank_reads(&opt, "bank0", 0);
    }
    if (!only || strcmp(only, "bank1_reads_keep_144") == 0) {
        if (!only) printf("scenario: bank1_reads_keep_144\n");
        run_bank_reads(&opt, "bank1", 1);
    }
    if (!only || strcmp(only, "dense_polling_keep_144") == 0) {
        if (!only) printf("scenario: dense_polling_keep_144\n");
        run_dense_polling(&opt);
    }

    if (failures) {
        fprintf(stderr, "status_cadence_test: %d FAILURE(s)\n", failures);
        return 1;
    }
    printf("status_cadence_test: all status-read cadence scenarios passed\n");
    return 0;
}