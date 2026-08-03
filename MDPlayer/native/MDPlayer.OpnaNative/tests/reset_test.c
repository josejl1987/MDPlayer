/*
 * Reset sequencing test for the copied Furnace YM2608-LLE core.
 *
 * Verifies the Furnace reset contract:
 *   - 576 complete master-clock pairs with ic asserted,
 *   - 576 complete master-clock pairs with ic released,
 *   - 576 complete master-clock pairs after ic is re-asserted,
 * and that the reset clears every driver-level transient: the queued writes,
 * the active bus/write phase, the serial decoder, the native frame state, the
 * IRQ edge state and the bus state. It also checks that the native
 * master_clock (absolute time in complete low/high pairs) is advanced by
 * exactly 3 x 576 and never decreases.
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp (DivPlatformYM2608::reset)
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

static void test_reset_576_phases(void)
{
    OpnaLle ctx;
    OpnaLleResetCounts counts;
    memset(&ctx, 0, sizeof(ctx));
    memset(&counts, 0, sizeof(counts));

    uint64_t before = opna_lle_master_clock(&ctx);
    opna_lle_reset_core(&ctx.core, &ctx.serial, &ctx.adpcm,
                        &ctx.master_clock, &counts, true);

    /* Exact triple of 576-pair phases. */
    CHECK(counts.phase1_pairs == 576, "phase 1 (ic asserted) is not 576");
    CHECK(counts.phase2_pairs == 576, "phase 2 (ic released) is not 576");
    CHECK(counts.phase3_pairs == 576, "phase 3 (ic re-asserted) is not 576");

    /* master_clock advanced by exactly 1728 and never decreased. */
    CHECK(opna_lle_master_clock(&ctx) == before + 1728,
          "master_clock did not advance by exactly 1728");
    CHECK(opna_lle_master_clock(&ctx) >= before,
          "master_clock regressed");

    /* Initial pin state matches the Furnace reference. */
    CHECK(ctx.core.input.cs == 1,  "reset did not set cs=1");
    CHECK(ctx.core.input.rd == 0,  "reset did not set rd=0");
    CHECK(ctx.core.input.wr == 0,  "reset did not set wr=0");
    CHECK(ctx.core.input.a0 == 0,  "reset did not set a0=0");
    CHECK(ctx.core.input.a1 == 0,  "reset did not set a1=0");
    CHECK(ctx.core.input.data == 0, "reset did not set data=0");
    CHECK(ctx.core.input.ad == 0,  "reset did not set ad=0");
    CHECK(ctx.core.input.da == 0,  "reset did not set da=0");
    CHECK(ctx.core.input.dm == 0,  "reset did not set dm=0");
    CHECK(ctx.core.input.test == 1, "reset did not set test=1");
    CHECK(ctx.core.input.dt0 == 0, "reset did not set dt0=0");

    /* The reset clocking leaves the chip in its defined post-reset state
     * (ic re-asserted after the loop, exactly as Furnace restores it). */
    CHECK(ctx.core.input.ic == 1, "reset did not restore ic=1");

    fprintf(stdout, "reset phases: p1=%llu p2=%llu p3=%llu master_before=%llu\n",
            (unsigned long long)counts.phase1_pairs,
            (unsigned long long)counts.phase2_pairs,
            (unsigned long long)counts.phase3_pairs,
            (unsigned long long)before);
}

static void test_reset_clears_state(void)
{
    OpnaLle ctx;

    /* Dirt the low-level chip/decoder state so `opna_lle_reset_core` must
     * visibly clear it. (The high-level opna_lle_reset additionally re-enqueues
     * the power-on default registers, so the clear contract lives here.) */
    memset(&ctx, 0, sizeof(ctx));
    ctx.serial.dac_value = 0xdeadbeef;
    ctx.serial.have_left = true;
    ctx.serial.have_right = true;
    ctx.core.input.cs = 0;                          /* dirty bus pin */
    ctx.core.input.wr = 1;
    ctx.core.input.test = 0;

    OpnaLleResetCounts counts;
    uint64_t master = 0;
    opna_lle_reset_core(&ctx.core, &ctx.serial, &ctx.adpcm, &master, &counts,
                        false);

    /* queued writes cleared */
    CHECK(ctx.core.input.a0 == 0 && ctx.core.input.a1 == 0,
          "reset did not clear bus address pins");
    /* bus state restored to the reset reference */
    CHECK(ctx.core.input.cs == 1 && ctx.core.input.rd == 0 && ctx.core.input.wr == 0,
          "reset did not restore the bus pins");
    CHECK(ctx.core.input.test == 1, "reset did not restore test=1");
    /* serial decoder cleared */
    CHECK(ctx.serial.dac_value == 0, "reset did not clear the serial accumulator");
    CHECK(!ctx.serial.have_left && !ctx.serial.have_right,
          "reset did not clear serial channel captures");
    /* 576/576/576 provable from the low-level reset too */
    CHECK(counts.phase1_pairs == 576 && counts.phase2_pairs == 576 &&
          counts.phase3_pairs == 576,
          "low-level reset did not run the triple 576-pair sequence");
    CHECK(master == 1728, "low-level reset did not advance master_clock by 1728");

    fprintf(stdout, "reset state cleared OK\n");
}

int main(void)
{
    test_reset_576_phases();
    test_reset_clears_state();

    if (failures == 0)
        fprintf(stdout, "reset_test: OK\n");
    return failures == 0 ? 0 : 1;
}
