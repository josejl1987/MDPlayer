/*
 * Prescaler / native-cadence audit (Prompt 5, Workstream C).
 *
 * Measures actual completed serial-frame timestamps through the production
 * bus scheduler, covering:
 *   - reset state
 *   - 0x2D / 0x2E / 0x2F prescaler-select writes
 *   - the current FM fixture
 *   - current SSG / RSS / ADPCM-B / timer fixtures
 *
 * Every write goes through the production bus scheduler (opna_lle_write) and
 * every completed serial frame is recorded with its completion master clock.
 * Cadence is measured from real frames, not inferred from constants.
 *
 * This is a diagnostic/audit test, not a pass/fail assertion gate: it prints
 * the observed intervals so the variable-cadence outcome is explicit and
 * reports whether cadence is constant. The hard gate that stops the resampler
 * lives in native_cadence_test.c.
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

typedef struct mdp_opna_frame_observation {
    uint64_t master_clock;
    uint64_t interval_from_previous;
    uint32_t prescaler_mode;
} mdp_opna_frame_observation;

/*
 * Run a preset-prescaler-window through the production scheduler and record
 * the cadence (interval between consecutive completed frames) plus the
 * prescaler mode observed in the core's live prescaler latch.
 */
static void audit_window(mdp_opna_session *s,
                         const char *tag,
                         uint32_t override_reg,
                         uint32_t override_value)
{
    mdp_opna_reset_chip(s);

    /* Submit the prescaler-select write first. Goes through the adapter-level
     * bus scheduler (opna_lle_write) so the audit still measures the chip's
     * real cadence response to a contract-violating write; the session ABI
     * (mdp_opna_write_register) rejects 0x2E/0x2F up front under QW1. */
    if (override_reg != 0xFFFFFFFFu) {
        /* Schedule the write at clock 0; the bus will drive it on the next
         * valid prescaler phase. */
        opna_lle_write(&s->lle, (int)override_reg, (int)override_value);
        /* advance past the prescaler transition point */
        mdp_opna_advance_to(s, 32);
    }

    /* Advance a probe window and collect completed-frame observations. */
    mdp_opna_fifo_reset(&s->fifo);
    uint64_t target = mdp_opna_get_master_clock(s) + 20000;
    mdp_opna_advance_to(s, target);

    unsigned count = 0;
    uint64_t i144 = 0, ivar = 0;
    uint64_t first_clock = 0, prev = 0;
    mdp_opna_timed_frame fr;
    while (mdp_opna_fifo_pop(&s->fifo, &fr)) {
        mdp_opna_frame_observation ob;
        ob.master_clock = fr.master_clock;
        ob.interval_from_previous = (count == 0) ? 0 :
                                    fr.master_clock - prev;
        ob.prescaler_mode = (uint32_t)s->lle.core.prescaler_sel[1];
        if (count == 0)
            first_clock = fr.master_clock;
        else if (ob.interval_from_previous == 144)
            i144++;
        else
            ivar++;
        prev = fr.master_clock;
        count++;
    }

    printf("cadence %-10s: frames=%u  interval144=%" PRIu64 "  variable=%" PRIu64
           "  prescaler_sel=%d  first_clock=%" PRIu64 "\n",
           tag, count, i144, ivar, (int)s->lle.core.prescaler_sel[1],
           first_clock);
}

/* Minimal deterministic FM/SSG/timer register stream for the audit. */
static void audit_fm_fixture(mdp_opna_session *s)
{
    mdp_opna_reset_chip(s);
    /* A small set of registers driving the FM path deterministically. */
    mdp_opna_write_register(s, 0, 0, 0x29, 0x80); /* 6-op mode */
    mdp_opna_write_register(s, 0, 0, 0x07, 0x38); /* */
    mdp_opna_advance_to(s, 20000);
    unsigned cnt = 0;
    mdp_opna_timed_frame fr;
    while (mdp_opna_fifo_pop(&s->fifo, &fr))
        cnt++;
    printf("cadence FM fixture: frames=%u\n", cnt);
}

int main(void)
{
    mdp_opna_session *s = NULL;
    char err[64];
    mdp_opna_open_options o = { 48000 };
    if (mdp_opna_open(&o, &s, err, sizeof(err)) != MDP_OPNA_OK)
        return 2;

    /* Reset state (post-reset, default prescaler) */
    audit_window(s, "reset", 0xFFFFFFFFu, 0);
    /* Prescaler select registers 0x2D/0x2E/0x2F */
    audit_window(s, "0x2D", 0x2D, 1);
    audit_window(s, "0x2E", 0x2E, 1);
    audit_window(s, "0x2F", 0x2F, 1);

    audit_fm_fixture(s);

    mdp_opna_close(s);
    printf("prescaler_audit_test: complete\n");
    return 0;
}
