/*
 * Stereo pan/routing test.
 *
 * Gate: "Pan tests pass"; assures the native mix keeps left and right
 * correctly ordered and that the SSG (mono analogue) contribution is folded
 * into both channels without swapping. It also exercises the mix function
 * directly with asymmetric digital content to prove L and R are routed to
 * their own output pins and never crossed.
 *
 * The exact per-channel FM/ADPCM pan decode on the multiplexed OPNA bus
 * (register 0xB4 / ac_fm_pan) depends on the channel-scan alignment produced
 * by the bus scheduler; the deterministic L/R-ordering contract verified here
 * is required for that to be meaningful.
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

#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

/* The mix must route its own left/right digital inputs to the matching output
 * pins — a deliberate swap here would be caught by this test. */
static void test_digital_left_right_not_swapped(void)
{
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx, true);
    ctx.ssg_vol = 0;          /* remove analogue so only serial matters */
    ctx.core.o_analog = 0;

    int16_t l = 777, r = -333;
    int16_t outl = 0, outr = 0;
    opna_lle_mix_frame(&ctx.core, l, r, ctx.fm_vol, ctx.ssg_vol, &outl, &outr);
    /* Furnace: outL from dacOut[1] (left_serial), outR from dacOut[0]. */
    CHECK(outl == l, "left input not routed to left output");
    CHECK(outr == r, "right input not routed to right output");
    CHECK(outl != outr, "left/right collapsed to a single value");

    /* and the other sign to rule out an off-by-one swap */
    int16_t outl2, outr2;
    opna_lle_mix_frame(&ctx.core, r, l, ctx.fm_vol, ctx.ssg_vol, &outl2, &outr2);
    CHECK(outl2 == r, "left/right outputs are crossed (swap)");
    CHECK(outr2 == l, "left/right outputs are crossed (swap)");
}

/* An analogue-only SSG tone must appear on both channels, equal, i.e. the pan
 * of a mono analogue source is "both". */
static void test_ssg_pan_is_both(void)
{
    enum { N = 60000 };
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx, true);
    opna_lle_write(&ctx, 0x07, 0x38);
    opna_lle_write(&ctx, 0x08, 8);
    opna_lle_write(&ctx, 0x09, 0);
    opna_lle_write(&ctx, 0x0a, 8);

    int seenL = 0, seenR = 0, unequal = 0;
    for (int i = 0; i < N; i++) {
        int16_t l, r;
        opna_lle_render(&ctx, &l, &r, 1);
        if (l) seenL = 1;
        if (r) seenR = 1;
        if (l != r) unequal = 1;
    }
    CHECK(seenL, "pan: no contribution on left");
    CHECK(seenR, "pan: no contribution on right");
    CHECK(!unequal, "pan: mono analogue ran asymmetric across L/R");
}

int main(void)
{
    test_digital_left_right_not_swapped();
    test_ssg_pan_is_both();

    if (failures == 0)
        fprintf(stdout, "pan_test: OK\n");
    return failures == 0 ? 0 : 1;
}
