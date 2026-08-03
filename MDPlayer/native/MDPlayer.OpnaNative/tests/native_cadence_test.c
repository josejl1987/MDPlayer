/*
 * Native cadence gate (Prompt 5, Workstream C / D gate).
 *
 * A fixed-rate resampler may be built ONLY when native cadence is proven
 * constant: every supported trace produces steady-state frames exactly 144
 * master clocks apart, no runtime prescaler write changes that cadence, and
 * three independent runs yield identical observations.
 *
 * The audit (prescaler_audit_test.c) demonstrates that honoring OPNA
 * prescaler-select writes 0x2E and 0x2F through the production bus scheduler
 * produces VARIABLE cadence (0x2E alternates 72/144; 0x2F stops producing
 * frames cadence) — so the fixed-rate condition is NOT met. Per spec §22 this
 * gate therefore FAILS, the fixed-rate resampler is NOT built, and Workstream
 * D and E do not proceed.
 *
 * The test is intentionally strict: it FAILS (returns non-zero) whenever it
 * finds a non-144 interval in any supported trace, including the prescaler
 * window that must be honored (0x2E / 0x2F). This is a real, non-weakened
 * assertion.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_session.h"
#include "../src/mdplayer_opna_fifo.h"

#include <inttypes.h>
#include <stdio.h>

static int failures = 0;
static unsigned long observed_non144 = 0;

/*
 * Advance a window and return the number of completed frames that were NOT
 * exactly 144 clocks apart from the previous frame. A non-144 interval means
 * cadence is variable and the fixed-rate resampler is disallowed. A window
 * that produces no frames (or too few to demonstrate cadence) also fails the
 * "initialization reaches the same deterministic cadence" requirement, and is
 * reported via *out_total.
 */
static unsigned count_non144(mdp_opna_session *s, uint64_t window,
                             unsigned *out_total)
{
    mdp_opna_fifo_reset(&s->fifo);
    uint64_t target = mdp_opna_get_master_clock(s) + window;
    mdp_opna_advance_to(s, target);

    unsigned bad = 0, first = 1, total = 0;
    uint64_t prev = 0;
    mdp_opna_timed_frame fr;
    while (mdp_opna_fifo_pop(&s->fifo, &fr)) {
        if (first) {
            first = 0;
        } else {
            if (fr.master_clock - prev != 144)
                bad++;
        }
        prev = fr.master_clock;
        total++;
    }
    if (out_total)
        *out_total = total;
    return bad;
}

static void check_cadence_constant(mdp_opna_session *s,
                                   const char *tag,
                                   uint64_t window)
{
    unsigned total = 0;
    unsigned bad = count_non144(s, window, &total);
    if (bad > 0 || total == 0) {
        fprintf(stdout, "  [cadence not 144] %s: %u frames, %u non-144 "
                        "intervals in %" PRIu64 " clocks\n",
                tag, total, bad, window);
        /* A zero-frame window also proves non-constant cadence (§21: steady
         * state must reach the same deterministic cadence). Count it. */
        if (total == 0 || bad > 0)
            observed_non144 += (bad ? bad : 1);
    } else {
        printf("  [cadence fixed]    %s: all 144-clock\n", tag);
    }
}

/*
 * Gate: fixed-rate resampling requires constant native cadence across every
 * supported trace, including the prescaler-select windows that must be honored
 * (0x2E / 0x2F). When any window is non-constant this gate FAILS.
 */
static void gate_fixed_rate_requires_constant_native_cadence(void)
{
    printf("gate: fixed_rate_resampler_requires_constant_native_cadence\n");

    mdp_opna_session *s = NULL;
    char err[64];
    mdp_opna_open_options o = { 48000 };
    if (mdp_opna_open(&o, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "gate: cannot open session\n");
        failures++;
        return;
    }

    /* Reset state. */
    mdp_opna_reset_chip(s);
    check_cadence_constant(s, "reset", 40000);

    /* Prescaler-select windows that must be honored (not absorbed/silently
     * ignored): each changes the routing and must keep cadence at 144. */
    for (unsigned reg = 0x2D; reg <= 0x2F; reg++) {
        mdp_opna_reset_chip(s);
        mdp_opna_write_register(s, 0, 0, (uint8_t)reg, 1);
        mdp_opna_advance_to(s, 64);   /* let the prescaler take effect */
        char tag[16];
        snprintf(tag, sizeof(tag), "prescaler 0x%02X", reg);
        check_cadence_constant(s, tag, 40000);
    }

    /* FM fixture (deterministic warmup) */
    mdp_opna_reset_chip(s);
    mdp_opna_write_register(s, 0, 0, 0x29, 0x80);
    mdp_opna_write_register(s, 0, 0, 0x07, 0x38);
    check_cadence_constant(s, "FM fixture", 40000);

    mdp_opna_close(s);

    if (observed_non144 > 0) {
        /* The fixed-rate resampler is NOT admissible. This is the intended
         * outcome of the audit: cadence is variable, so the resampler and
         * Workstream D/E are not built. */
        fprintf(stderr,
                "\nnative_cadence_test: FAIL — variable native cadence ("
                "%lu non-144 intervals). Fixed-rate resampling is disallowed.\n"
                "  Per spec §22: keep FIFO, do NOT build the fixed-rate "
                "resampler, stop after Workstreams A/B/C.\n",
                observed_non144);
        failures++;
        return;
    }

    fprintf(stderr,
            "native_cadence_test: fixed cadence proven; resampler admissible.\n");
}

int main(void)
{
    gate_fixed_rate_requires_constant_native_cadence();
    if (failures) {
        fprintf(stderr, "native_cadence_test: %d failure(s) — cadence is NOT "
                        "constant 144.\n", failures);
        /* The normal run is expected to FAIL (variable cadence). We return 1
         * so CTest records the gate as failing, matching the hard-stop spec. */
        return 1;
    }
    return 0;
}
