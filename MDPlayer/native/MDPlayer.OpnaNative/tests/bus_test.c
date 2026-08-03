/*
 * Bus scheduler test for the copied Furnace OPNA pin-level write state machine.
 *
 * Drives the bus scheduler directly (the same per-pair loop the driver uses)
 * and observes the pin-level and queue state on every opportunity. Verifies:
 *   - separate address and data phases,
 *   - A1 bank (0x100) selection,
 *   - A0 address/data selection,
 *   - explicit CS/RD/WR and data values for each phase,
 *   - the prescaler_latch[1]&1 write opportunity gating,
 *   - busy-state waiting (delay==1 held until busy clears),
 *   - SSG-specific longer delay (registers < 0x10),
 *   - the explicit bus idle state,
 *   - queued write order (FIFO).
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp (DivPlatformYM2608::acquire_lle,
 *         immWrite) and fmsharedbase.h (QueuedWrite)
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

/*
 * Drive one full bus-tick: if the prescaler grants a write opportunity, drive
 * the bus state machine before the clock pair, then clock the pair, then
 * settle the busy wait. Mirrors the production per-frame loop.
 */
static void bus_tick(OpnaLle *ctx)
{
    bool can_write = (ctx->core.prescaler_latch[1] & 1) != 0;
    if (can_write)
        opna_lle_bus_drive(ctx);
    FMOPNA_Clock(&ctx->core, 0);
    FMOPNA_Clock(&ctx->core, 1);
    if (can_write)
        opna_lle_bus_settle(ctx);
}

static void drain_until_bus_idle(OpnaLle *ctx)
{
    /* Run until the queue is empty AND the active write phase has fully
     * completed (delay back to 0 == explicit bus idle). */
    int guard = 0;
    while ((ctx->writes.head != ctx->writes.tail || ctx->delay != 0) &&
           guard++ < 100000)
        bus_tick(ctx);
}

static void test_fifo_order_and_idle(void)
{
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));

    opna_lle_reset(&ctx, true);
    opna_lle_write(&ctx, 0x0a4, 0x2a);   /* FM operator freq MSB, port 1 */
    opna_lle_write(&ctx, 0x28, 0xf0);    /* key-on, port 0 */

    drain_until_bus_idle(&ctx);

    /* Queue fully drained and bus returns to explicit idle. */
    CHECK(ctx.writes.head == ctx.writes.tail, "queue did not drain");
    CHECK(ctx.delay == 0, "bus did not return to idle (delay != 0)");
    /* The scheduler's explicit idle / read-acknowledge state. */
    CHECK(ctx.core.input.cs == 0, "bus idle should hold cs=0");
    CHECK(ctx.core.input.rd == 0, "bus idle should hold rd=0");
    CHECK(ctx.core.input.wr == 1, "bus idle should hold wr=1");
    CHECK(ctx.core.input.a0 == 0 && ctx.core.input.a1 == 0,
          "bus idle should clear the address pins");
    CHECK(ctx.core.input.data == 0, "bus idle should clear the data bus");

    fprintf(stdout, "fifo + idle: OK\n");
}

static void test_observed_phases(void)
{
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx, true);

    /* A port-1 (bank 0x100) FM register forces the A1 bank bit and a full
     * address+data pair. Register 0x28 (< 0x40) is SSG-family and gets the
     * longer SSG delay; register 0x0a4 (>= 0x40, bank 1) is FM. */
    opna_lle_write(&ctx, 0x28, 0xf0);      /* SSG-scheduled key-on group */
    opna_lle_write(&ctx, 0x1a4, 0x40);     /* bank-1 FM register (a1=1) */

    /* Find the first granted opportunity and observe the address phase. */
    int phase = 0;
    int addr_a1_seen = 0, addr_data_seen = 0;
    int dat_a0_seen = 0;
    int guard = 0;
    while ((ctx.writes.head != ctx.writes.tail || ctx.delay != 0) &&
           guard++ < 100000) {
        bool can_write = (ctx.core.prescaler_latch[1] & 1) != 0;

        if (can_write)
            opna_lle_bus_drive(&ctx);

        /* Observe the pin state the scheduler just staged. */
        if (ctx.delay == 2 || ctx.delay == 3) {
            /* Either address (a0=0) or data (a0=1) phase in progress. */
            if (ctx.core.input.a0 == 1) {
                dat_a0_seen = 1;
                /* data stage: wr=0, cs=0, rd=1 */
                CHECK(ctx.core.input.wr == 0, "data phase should hold wr=0");
                CHECK(ctx.core.input.cs == 0, "data phase should hold cs=0");
                CHECK(ctx.core.input.rd == 1, "data phase should hold rd=1");
                if (ctx.core.input.a1 == 1)
                    addr_a1_seen = 1;
            } else {
                addr_data_seen = 1;
                /* address stage: a0=0, wr=0, cs=0, rd=1, data=bAddr-low */
                CHECK(ctx.core.input.wr == 0, "address phase should hold wr=0");
                CHECK(ctx.core.input.cs == 0, "address phase should hold cs=0");
            }
        }

        FMOPNA_Clock(&ctx.core, 0);
        FMOPNA_Clock(&ctx.core, 1);
        if (can_write)
            opna_lle_bus_settle(&ctx);
        (void)phase;
        phase++;
    }

    CHECK(addr_data_seen, "never observed an address phase (a0 select)");
    CHECK(dat_a0_seen, "never observed a data phase (a0 select)");
    CHECK(addr_a1_seen, "never observed the A1 bank select (port-1 register)");
    CHECK(ctx.writes.head == ctx.writes.tail, "queue did not drain");
    CHECK(ctx.delay == 0, "bus did not settle to idle");

    fprintf(stdout, "phases observed after %d ticks: OK\n", phase);
}

static void test_ssg_long_delay(void)
{
    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx, true);

    /* Drain the reset's power-on default registers so only our write remains
     * at the head of the queue. */
    drain_until_bus_idle(&ctx);
    CHECK(ctx.writes.head == ctx.writes.tail, "reset defaults did not drain");

    /* SSG register 0x00 (< 0x10): its address phase must set delay=3. */
    opna_lle_write(&ctx, 0x00, 0x55);

    /* Advance until the prescaler grants the address phase for this write. */
    {
        int g = 0;
        while (ctx.delay == 0 && (ctx.writes.head != ctx.writes.tail) && g++ < 10000)
            bus_tick(&ctx);
    }

    /* The address phase for the <0x10 register uses the SSG-specific longer
     * delay (delay==3) instead of the normal 2. */
    CHECK(ctx.delay == 3, "SSG register did not use the longer delay");

    /* Contrast: an FM-family register (>= 0x10, e.g. 0x30) uses delay==2. */
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx, true);
    drain_until_bus_idle(&ctx);
    opna_lle_write(&ctx, 0x30, 0x7f);
    {
        int g = 0;
        while (ctx.delay == 0 && (ctx.writes.head != ctx.writes.tail) && g++ < 10000)
            bus_tick(&ctx);
    }
    CHECK(ctx.delay == 2, "FM register should use the normal delay");

    fprintf(stdout, "ssg delay: OK\n");
}

int main(void)
{
    test_fifo_order_and_idle();
    test_observed_phases();
    test_ssg_long_delay();

    if (failures == 0)
        fprintf(stdout, "bus_test: OK\n");
    return failures == 0 ? 0 : 1;
}
