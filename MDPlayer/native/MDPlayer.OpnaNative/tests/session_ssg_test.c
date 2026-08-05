/*
 * Session SSG test (Prompt 8R follow-up: SSG audible on native-audio).
 *
 * Regression: the session/replay advance path (mdp_opna_session_advance ->
 * clock_pair) used to return only the serial FM DAC and never folded in the
 * SSG analogue (o_analog). The raw opna_lle_render path applied the full
 * opna_lle_mix_frame (serial + o_analog*ssgVol*42); the session path did not,
 * so native-audio renders had no SSG at all.
 *
 * Gate: "session path hears an SSG tone" — write the same SSG register
 * sequence through the public write ABI (bank 0) that the raw opna_lle_render
 * test uses, advance, drain, and require nonzero output on both channels and
 * an audible (not just one-spike) level.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_session.h"

#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

static void test_session_ssg_tone_audible(void)
{
    mdp_opna_session *s = NULL;
    char err[64];
    mdp_opna_open_options o = { 44100 };
    CHECK(mdp_opna_open(&o, &s, err, sizeof(err)) == MDP_OPNA_OK, "open failed");
    if (!s) return;

    /* Same SSG tone sequence as ssg_test's raw adapter test, driven through
     * the public session write ABI (bank 0 = SSG low register space). */
    CHECK(mdp_opna_write_register(s, 100, 0, 0x07, 0x38) == MDP_OPNA_OK, "write mixer");
    CHECK(mdp_opna_write_register(s, 110, 0, 0x08, 8)    == MDP_OPNA_OK, "write volA");
    CHECK(mdp_opna_write_register(s, 120, 0, 0x09, 0)    == MDP_OPNA_OK, "write volB");
    CHECK(mdp_opna_write_register(s, 130, 0, 0x0a, 8)    == MDP_OPNA_OK, "write volC");

    /* Advance + drain in bounded chunks (the FIFO holds 16384 frames and
     * never overflows when drained incrementally, matching the managed
     * replay path). Collect ~0.55 s of native audio. */
    enum { N = 24000 };
    int16_t buf[N * 2];
    long nzL = 0, nzR = 0;
    int drained_total = 0, rc_last = MDP_OPNA_OK;
    uint64_t frames = s->native_frame_clocks;
    int done = 0;
    while (!done) {
        uint64_t target = mdp_opna_get_master_clock(s);
        for (int i = 0; i < 8000; i++) {
            if (mdp_opna_advance_to(s, target + (uint64_t)(i + 1) * frames) != MDP_OPNA_OK)
                { rc_last = MDP_OPNA_ERR_INTERNAL; break; }
        }
        if (rc_last != MDP_OPNA_OK) break;

        int drained = 0;
        int avail = drained_total < N ? N - drained_total : 0;
        if (avail > 0 && mdp_opna_drain_audio(s, buf + drained_total * 2, avail, &drained) != MDP_OPNA_OK)
            { rc_last = MDP_OPNA_ERR_INTERNAL; break; }
        drained_total += drained;
        done = drained_total >= N;
    }
    CHECK(rc_last == MDP_OPNA_OK, "advance failed");
    for (int i = 0; i + 1 < drained_total * 2; i += 2) {
        if (buf[i] != 0) nzL++;
        if (buf[i + 1] != 0) nzR++;
    }
    CHECK(drained_total > 0, "no frames drained for an SSG tone");
    CHECK(nzL > 1000, "SSG tone did not produce nonzero output on left (session path)");
    CHECK(nzR > 1000, "SSG tone did not produce nonzero output on right (session path)");

    mdp_opna_close(s);
}

static void test_fm_only_differs_from_ssg(void)
{
    /* A control session with FM left at reset (no SSG writes) must produce
     * different audio than the SSG session above — otherwise SSG contributes
     * nothing. Both start from power-on reset, so the difference can only be
     * the SSG register writes. */
    mdp_opna_session *s1 = NULL, *s2 = NULL;
    char err[64];
    mdp_opna_open_options o = { 44100 };
    mdp_opna_open(&o, &s1, err, sizeof(err));
    mdp_opna_open(&o, &s2, err, sizeof(err));
    if (!s1 || !s2) return;

    mdp_opna_write_register(s1, 100, 0, 0x07, 0x38);
    mdp_opna_write_register(s1, 110, 0, 0x08, 8);
    mdp_opna_write_register(s1, 120, 0, 0x09, 0);
    mdp_opna_write_register(s1, 130, 0, 0x0a, 8);

    enum { N = 24000 };
    int16_t b1[N * 2], b2[N * 2];
    int d1 = 0, d2 = 0;
    uint64_t frames = s1->native_frame_clocks;
    int fail = 0;
    while (d1 < N || d2 < N) {
        for (int i = 0; i < 8000; i++) {
            uint64_t base = mdp_opna_get_master_clock(s1);
            if (mdp_opna_advance_to(s1, base + frames) != MDP_OPNA_OK) fail = 1;
            if (mdp_opna_advance_to(s2, base + frames) != MDP_OPNA_OK) fail = 1;
            if (fail) break;
        }
        if (fail) break;
        int da = 0, db = 0;
        int a1 = d1 < N ? N - d1 : 0;
        int a2 = d2 < N ? N - d2 : 0;
        mdp_opna_drain_audio(s1, b1 + d1 * 2, a1, &da);
        mdp_opna_drain_audio(s2, b2 + d2 * 2, a2, &db);
        d1 += da; d2 += db;
    }

    long diffs = 0;
    int m = d2 < d1 ? d2 : d1;
    for (int i = 0; i < m * 2; i += 2)
        if (b1[i] != b2[i] || b1[i + 1] != b2[i + 1]) diffs++;
    CHECK(diffs > 100, "SSG writes did not change the session output at all");

    mdp_opna_close(s1);
    mdp_opna_close(s2);
}

int main(void)
{
    test_session_ssg_tone_audible();
    test_fm_only_differs_from_ssg();

    if (failures == 0)
        fprintf(stdout, "session_ssg_test: OK\n");
    return failures == 0 ? 0 : 1;
}
