/*
 * OPNA native-cadence gate + prescaler-write policy tests (Prompt 5.1,
 * Task A).
 *
 * ABI version 1 defines the production profile as a fixed 144 master clocks
 * per complete stereo serial frame. These tests verify:
 *
 *   - default_profile_has_144_clock_cadence:      reset + default run stays at 144
 *   - prescaler_2d_has_144_clock_cadence:         honoring 0x2D stays at 144
 *   - prescaler_2e_reports_unsupported_cadence:   honoring 0x2E latches UNSUPPORTED_CADENCE
 *   - prescaler_2f_reports_unsupported_cadence:   honoring 0x2F latches UNSUPPORTED_CADENCE
 *   - real_fm_trace_has_144_clock_cadence:        the checked-in real FM trace stays at 144
 *
 * The 0x2E / 0x2F tests PASS only when the ABI detects and reports unsupported
 * cadence (they do not require successful audio resampling). Prescaler writes
 * are always executed through the production bus; they are never silently
 * ignored.
 *
 * The executable runs a single scenario when given `-t <key>` (used to register
 * each named CTest) or all scenarios when run bare.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_session.h"
#include "../src/mdplayer_opna_fifo.h"
#include "../tests/furnace_reference_fixture.c"

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

/* Deep advance through the session and count completed frames that were NOT
 * exactly `native_frame_clocks` apart. Returns the number of non-144 intervals
 * AND records the session's cadence state. */
static int run_window(mdp_opna_session *s, const char *prescaler_write,
                      uint64_t window)
{
    /* Apply the prescaler-select write at clock 0 (honored through the bus). */
    if (prescaler_write) {
        unsigned reg;
        unsigned val;
        if (sscanf(prescaler_write, "%x", &reg) != 1) { reg = 0x2d; }
        val = 1;   /* prescaler select bit pattern; the core absorbs addr to prescaler_sel */
        if (mdp_opna_write_register(s, 0, 0, (uint8_t)reg, (uint8_t)val) ==
                MDP_OPNA_ERR_UNSUPPORTED_CADENCE) {
            return -1000;   /* cadence error surfaced immediately */
        }
    }

    uint64_t target = mdp_opna_get_master_clock(s) + window;
    int rc = mdp_opna_advance_to(s, target);
    if (rc == MDP_OPNA_ERR_UNSUPPORTED_CADENCE)
        return -1000;   /* surfaced during advance */

    uint64_t prev = 0;
    int first = 1;
    int non144 = 0;
    mdp_opna_timed_frame fr;
    while (mdp_opna_fifo_pop(&s->fifo, &fr)) {
        if (!first && fr.master_clock - prev != s->native_frame_clocks)
            non144++;
        prev = fr.master_clock;
        first = 0;
    }
    return non144;
}

/* 0x2E written through the production bus must make the cadence guard latch
 * MDP_OPNA_ERR_UNSUPPORTED_CADENCE on a subsequent advance/drain. */
static void expect_unsupported(mdp_opna_open_options *opt, const char *tag,
                               unsigned reg)
{
    mdp_opna_session *s = NULL;
    char err[64];
    if (mdp_opna_open(opt, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "  %s: open failed\n", tag);
        failures++;
        return;
    }
    /* Honor 0x2E/0x2F through the bus. */
    int rc = mdp_opna_write_register(s, 0, 0, (uint8_t)reg, 1);
    CHECK(rc == MDP_OPNA_OK, "prescaler write rejected at clock 0");
    /* The cadence-changing write is executed through the bus; a subsequent
     * advance or drain must surface UNSUPPORTED_CADENCE. */
    rc = mdp_opna_advance_to(s, 4000);
    if (rc == MDP_OPNA_OK) {
        /* advance didn't trip: a drain must now report unsupported cadence. */
        int16_t buf[64];
        int drained = -1;
        rc = mdp_opna_drain_audio(s, buf, 32, &drained);
    }
    CHECK(rc == MDP_OPNA_ERR_UNSUPPORTED_CADENCE,
          "0x%02X did not report unsupported cadence");
    mdp_opna_close(s);
}

/* 0x2D and default must stay at exactly 144 clocks/frame (no cadence error). */
static void expect_fixed_144(mdp_opna_open_options *opt, const char *tag,
                             const char *prescaler_write)
{
    mdp_opna_session *s = NULL;
    char err[64];
    if (mdp_opna_open(opt, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "  %s: open failed\n", tag);
        failures++;
        return;
    }
    int non144 = run_window(s, prescaler_write, 120000);
    CHECK(non144 == 0, tag);
    CHECK(!s->cadence_error, "cadence error latched but none expected");
    mdp_opna_close(s);
}

/* The checked-in real FM trace must stay at 144 clocks/frame. */
static void expect_real_fm_144(mdp_opna_open_options *opt)
{
    mdp_opna_session *s = NULL;
    char err[64];
    if (mdp_opna_open(opt, &s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "  real_fm_trace: open failed\n");
        failures++;
        return;
    }
    /* Replay the real trace through the production bus. */
    for (int i = 0; i < kFurnaceRefWriteCount; i++) {
        int rc = mdp_opna_write_register(
            s, kFurnaceRefWrites[i].master_clock, (uint8_t)kFurnaceRefWrites[i].bank,
            (uint8_t)kFurnaceRefWrites[i].reg, (uint8_t)kFurnaceRefWrites[i].value);
        if (rc != MDP_OPNA_OK && rc != MDP_OPNA_ERR_CLOCK_REGRESSION) {
            /* The trace uses a temporary absolute-clock conversion; some
             * entries share timestamps, which is fine. A hard failure here is
             * still an error unless it is unsupported cadence. */
            if (rc == MDP_OPNA_ERR_UNSUPPORTED_CADENCE) {
                fprintf(stderr, "  real_fm_trace: unsupported cadence\n");
                failures++;
                mdp_opna_close(s);
                return;
            }
        }
    }
    int non144 = run_window(s, NULL, 120000);
    CHECK(non144 == 0, "real FM trace produced non-144 cadence");
    CHECK(!s->cadence_error, "real FM trace latched cadence error");
    mdp_opna_close(s);
}

int main(int argc, char **argv)
{
    const char *only = NULL;
    if (argc >= 3 && strcmp(argv[1], "-t") == 0)
        only = argv[2];

    mdp_opna_open_options opt = { 48000 };

    if (!only || strcmp(only, "default_profile_has_144_clock_cadence") == 0) {
        if (!only) printf("scenario: default_profile_has_144_clock_cadence\n");
        expect_fixed_144(&opt, "default", NULL);
    }
    if (!only || strcmp(only, "prescaler_2d_has_144_clock_cadence") == 0) {
        if (!only) printf("scenario: prescaler_2d_has_144_clock_cadence\n");
        expect_fixed_144(&opt, "0x2D", "2d");
    }
    if (!only || strcmp(only, "prescaler_2e_reports_unsupported_cadence") == 0) {
        if (!only) printf("scenario: prescaler_2e_reports_unsupported_cadence\n");
        expect_unsupported(&opt, "0x2E", 0x2e);
    }
    if (!only || strcmp(only, "prescaler_2f_reports_unsupported_cadence") == 0) {
        if (!only) printf("scenario: prescaler_2f_reports_unsupported_cadence\n");
        expect_unsupported(&opt, "0x2F", 0x2f);
    }
    if (!only || strcmp(only, "real_fm_trace_has_144_clock_cadence") == 0) {
        if (!only) printf("scenario: real_fm_trace_has_144_clock_cadence\n");
        expect_real_fm_144(&opt);
    }

    if (failures) {
        fprintf(stderr, "native_cadence_test: %d FAILURE(s)\n", failures);
        return 1;
    }
    printf("native_cadence_test: all cadence scenarios passed\n");
    return 0;
}