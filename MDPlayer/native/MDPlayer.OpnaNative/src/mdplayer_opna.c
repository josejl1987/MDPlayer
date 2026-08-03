/*
 * MDPlayer YM2608-LLE native adapter driver.
 *
 * This is not copied from Furnace verbatim; it orchestrates the adapted
 * sub-modules (core reset, bus state machine, serial decoder, ADPCM bus,
 * mix) into the Furnace-equivalent per-frame loop. The per-frame loop
 * semantics below are adapted from DivPlatformYM2608::acquire_lle.
 *
 * Original copyright (Furnace integration that the sub-modules adapt):
 * Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_internal.h"

void opna_lle_reset(OpnaLle *ctx, bool clear_external_adpcm_ram)
{
    memset(ctx, 0, sizeof(*ctx));
    ctx->fm_vol = 256;   /* Furnace default fmVol=256 */
    ctx->ssg_vol = 128;  /* Furnace default ssgVol=128 */
    ctx->mem_config = 0; /* Furnace default memConfig=0 */
    ctx->delay = 0;
    opna_lle_queue_reset(&ctx->writes);
    /*
     * The full chip reset (triple 576-pair sequence) and the initial
     * power-on register setup are done here. Furnace writes the default
     * OPN registers after reset (6-channel mode, LFO, RSS/PCM volumes,
     * ADPCM limits, prescaler); we mirror that so a freshly-reset chip is
     * in the same starting state Furnace assumes.
     */
    opna_lle_reset_core(&ctx->core, &ctx->serial, &ctx->adpcm,
                        &ctx->master_clock, NULL, clear_external_adpcm_ram);

    /* enable 6 channel mode */
    opna_lle_write(ctx, 0x29, 0x80);
    /* LFO */
    opna_lle_write(ctx, 0x22, 8);
    /* PCM volume: A, B */
    opna_lle_write(ctx, 0x11, 0x3f);
    opna_lle_write(ctx, 0x10b, 0xff);
    /* ADPCM limit */
    opna_lle_write(ctx, 0x10d, 0xff);
    opna_lle_write(ctx, 0x10c, 0xff);
    /* prescaler */
    opna_lle_write(ctx, 0x2d, 0xff);
}

void opna_lle_write(OpnaLle *ctx, int address, int value)
{
    opna_lle_queue_push(&ctx->writes, address, value);
}

void opna_lle_render(OpnaLle *ctx, int16_t *out_l, int16_t *out_r, size_t frames)
{
    fmopna_t *chip = &ctx->core;

    for (size_t h = 0; h < frames; h++) {
        int16_t left = 0;
        int16_t right = 0;

        /* Furnace per-frame loop: clock full pairs until both serial
         * channels have been captured. */
        for (;;) {
            bool can_write = (chip->prescaler_latch[1] & 1) != 0;

            if (can_write)
                opna_lle_bus_drive(ctx);

            FMOPNA_Clock(chip, 0);
            FMOPNA_Clock(chip, 1);
            ctx->master_clock++;   /* this clock pair is now absolute history */

            /* Drive the ADPRCM external-memory bus each pair (so o_dm/o_a8
             * row/column latches and the returned memory byte are in sync
             * with the core). */
            opna_lle_adpcm_clock(&ctx->adpcm, chip, ctx->mem_config);

            if (can_write)
                opna_lle_bus_settle(ctx);

            if (opna_lle_serial_clock(&ctx->serial, chip, &left, &right))
                break;
        }

        opna_lle_mix_frame(chip, left, right, ctx->fm_vol, ctx->ssg_vol,
                           &out_l[h], &out_r[h]);
    }
}

uint64_t opna_lle_master_clock(const OpnaLle *ctx)
{
    return ctx->master_clock;
}

int opna_lle_obs_status_timer_a(const OpnaLle *ctx)
{
    /* Live internal timer-A status latch (timer_a_status is clocked [1] =
     * previous phase). This is the observation that status reads and the IRQ
     * pull both consume; it is not synthesized in managed/native glue. */
    return ctx->core.timer_a_status[1] != 0;
}

int opna_lle_obs_status_timer_b(const OpnaLle *ctx)
{
    return ctx->core.timer_b_status[1] != 0;
}

int opna_lle_obs_irq_pull(const OpnaLle *ctx)
{
    return ctx->core.o_irq_pull != 0;
}
