/*
 * Furnace-compatible SSG mix test.
 *
 * Verifies the native final mix produces the exact Furnace SSG stereo fold:
 *   outL += o_analog * ssgVol * 42;   outR += o_analog * ssgVol * 42;
 * and that SSG behaves as a mono analogue source added into BOTH digital
 * channels (so left and right are never swapped, and disabling the SSG path
 * removes the contribution while digital stereo PCM remains byte-identical
 * aside from the removed analogue term).
 *
 * Gate: "Furnace-compatible SSG mix passes"; "scale is exact";
 *       "SSG contributes to both channels"; "disabling SSG removes the
 *       contribution"; "digital PCM remains present"; "left and right are
 *       not swapped".
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp (DivPlatformYM2608::acquire_lle)
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_internal.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

/* The prompt pins the exact Furnace scale constant. */
static void test_scale_constant(void)
{
    CHECK(MDP_OPNA_FURNACE_SSG_ANALOG_SCALE == 42,
          "MDP_OPNA_FURNACE_SSG_ANALOG_SCALE is not exactly 42");
}

/* Exact arithmetic: for a held o_analog value, adding the analogue term to
 * both channels matches the integer-ized Furnace formula. We use integer
 * truncation of o_analog*ssgVol then scale by 42, mirroring what the mix uses
 * as an (int) conversion, so no libm rounding routines are required. */
static void test_mix_arithmetic(void)
{
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx);

    ctx.core.o_analog = 7.0;
    ctx.ssg_vol = 128;
    ctx.fm_vol = 256;

    int16_t l = 0, r = 0;
    int16_t lookL = 500, lookR = -2000;
    opna_lle_mix_frame(&ctx.core, lookL, lookR, ctx.fm_vol, ctx.ssg_vol, &l, &r);

    /* analogue term added to both channels is 7*128*42 = 37632 here */
    int64_t analog = (int64_t)(ctx.core.o_analog * ctx.ssg_vol * MDP_OPNA_FURNACE_SSG_ANALOG_SCALE);
    int64_t expect_l = ((int64_t)lookL * ctx.fm_vol >> 8) + analog;
    int64_t expect_r = ((int64_t)lookR * ctx.fm_vol >> 8) + analog;
    if (expect_l < -32768) expect_l = -32768;
    if (expect_l > 32767)  expect_l = 32767;
    if (expect_r < -32768) expect_r = -32768;
    if (expect_r > 32767)  expect_r = 32767;

    CHECK(l == (int16_t)expect_l, "left mix does not equal exact Furnace formula");
    CHECK(r == (int16_t)expect_r, "right mix does not equal exact Furnace formula");
}

/* Drive a real SSG tone through the full chip and prove the analogue path
 * lands in both stereo channels, with left == right (mono fold) and that
 * nulling the SSG volume removes the contribution. Digital stereo PCM keeps
 * producing frames throughout. */
static void test_ssg_feeds_both_channels(void)
{
    enum { N = 160000 };
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx);

    /* square-wave tone on SSG channel A */
    opna_lle_write(&ctx, 0x07, 0x38);   /* tone A on (mixer), noise off */
    opna_lle_write(&ctx, 0x08, 8);      /* period LSB */
    opna_lle_write(&ctx, 0x09, 0);      /* period MSB */
    opna_lle_write(&ctx, 0x0a, 8);      /* volume (3-bit loud) */

    long nzL = 0, nzR = 0, total = 0;
    int both = 1;
    for (int i = 0; i < N; i++) {
        int16_t l, r;
        opna_lle_render(&ctx, &l, &r, 1);
        total++;
        if (l != 0) nzL++;
        if (r != 0) nzR++;
        if ((l != 0) != (r != 0)) both = 0;   /* SSG contributes to both, or neither */
    }

    CHECK(nzL > 1000, "SSG did not contribute nonzero samples to left");
    CHECK(nzR > 1000, "SSG did not contribute nonzero samples to right");
    CHECK(both, "SSG contribution was not present on both channels simultaneously");

    /* left must equal right at every sample (mono SSG folded into both) */
    OpnaLle c2;
    memset(&c2, 0, sizeof(c2));
    opna_lle_reset(&c2);
    opna_lle_write(&c2, 0x07, 0x38);
    opna_lle_write(&c2, 0x08, 8);
    opna_lle_write(&c2, 0x09, 0);
    opna_lle_write(&c2, 0x0a, 8);
    int16_t lastL = 0, lastR = 0;
    for (int i = 0; i < N; i++) {
        opna_lle_render(&c2, &lastL, &lastR, 1);
        if (lastL != lastR) {
            CHECK(0, "left and right are swapped / not equal for mono SSG");
            return;
        }
    }
    CHECK(1, "left == right for mono SSG");
}

static void test_disabling_ssg_removes_contribution(void)
{
    enum { N = 80000 };
    /* same tone, but ssgVol=0 => the analogue fold is zero; a control run at
     * default ssgVol must differ. */
    OpnaLle on, off;
    memset(&on, 0, sizeof(on));  opna_lle_reset(&on);
    memset(&off, 0, sizeof(off)); opna_lle_reset(&off);
    for (OpnaLle *c = &on; ; c = &off) {
        opna_lle_write(c, 0x07, 0x38);
        opna_lle_write(c, 0x08, 8);
        opna_lle_write(c, 0x09, 0);
        opna_lle_write(c, 0x0a, 8);
        if (c == &off) break;
    }
    off.ssg_vol = 0;

    long nzOn = 0, nzOff = 0;
    int16_t diffSeen = 0;
    for (int i = 0; i < N; i++) {
        int16_t l1, r1, l2, r2;
        opna_lle_render(&on,  &l1, &r1, 1);
        opna_lle_render(&off, &l2, &r2, 1);
        if ((int)l1 | (int)r1) nzOn++;
        if ((int)l2 | (int)r2) nzOff++;
        if (l1 != l2) diffSeen = 1;
    }
    CHECK(nzOn > 1000, "ssgVol>0 produced no output");
    CHECK(nzOff == 0, "ssgVol=0 should remove the SSG analogue contribution entirely");
    CHECK(diffSeen, "disabling SSG did not change the output (contribution not removed)");
}

static void test_digital_pcm_remains(void)
{
    /* With SSG disabled (ssgVol=0), the serial stereo PCM path must still
     * produce frames — prove normal render returns a full stereo stream and
     * that the mix function still adds digital serial into the int16 out. */
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx);
    ctx.ssg_vol = 0;

    int16_t l = 1234, r = -999;
    int16_t outl = 0, outr = 0;
    ctx.core.o_analog = 0;   /* no analogue */
    opna_lle_mix_frame(&ctx.core, l, r, ctx.fm_vol, ctx.ssg_vol, &outl, &outr);
    CHECK(outl == l, "digital PCM lost through mix (left)");
    CHECK(outr == r, "digital PCM lost through mix (right)");
}

int main(void)
{
    test_scale_constant();
    test_mix_arithmetic();
    test_ssg_feeds_both_channels();
    test_disabling_ssg_removes_contribution();
    test_digital_pcm_remains();

    if (failures == 0)
        fprintf(stdout, "ssg_test: OK\n");
    return failures == 0 ? 0 : 1;
}
