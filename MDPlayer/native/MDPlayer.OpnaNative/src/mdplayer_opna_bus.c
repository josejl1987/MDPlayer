/*
 * YM2608-LLE pin-level write state machine.
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
#include "mdplayer_opna_internal.h"

/* --------------------------------------------------------------------- */
/* Queue                                                                */
/* --------------------------------------------------------------------- */

void opna_lle_queue_reset(OpnaLleWriteQueue *q)
{
    q->head = 0;
    q->tail = 0;
}

bool opna_lle_queue_push(OpnaLleWriteQueue *q, int address, int value)
{
    int next = (q->head + 1) % OPNA_LLE_WRITE_QUEUE_SIZE;
    if (next == q->tail)
        return false;   /* full */
    q->w[q->head].address = address;
    q->w[q->head].value = value;
    q->w[q->head].addr_written = false;
    q->head = next;
    return true;
}

bool opna_lle_queue_front(OpnaLleWriteQueue *q, OpnaLleQueuedWrite *out)
{
    if (q->head == q->tail)
        return false;
    *out = q->w[q->tail];
    return true;
}

void opna_lle_queue_pop(OpnaLleWriteQueue *q)
{
    if (q->head != q->tail)
        q->tail = (q->tail + 1) % OPNA_LLE_WRITE_QUEUE_SIZE;
}

/* --------------------------------------------------------------------- */
/* Bus drive (before clock pair) — Furnace's `if (canWeWrite)` branch    */
/* --------------------------------------------------------------------- */
void opna_lle_bus_drive(OpnaLle *ctx)
{
    fmopna_t *chip = &ctx->core;

    if (ctx->delay > 0) {
        if (ctx->delay == 3) {
            /* SSG-specific longer delay completes into an idle bus. */
            chip->input.cs = 1;
            chip->input.rd = 1;
            chip->input.wr = 1;
            chip->input.a0 = 0;
            chip->input.a1 = 0;
            ctx->delay = 0;
        } else {
            /* Release the bus (read-acknowledge) and hold the write pending
             * until the chip's busy counter clears. */
            chip->input.cs = 0;
            chip->input.rd = 0;
            chip->input.wr = 1;
            chip->input.a0 = 0;
            chip->input.a1 = 0;
            chip->input.data = 0;
            ctx->delay = 1;
        }
        return;
    }

    OpnaLleQueuedWrite w;
    if (opna_lle_queue_front(&ctx->writes, &w)) {
        if (w.addr_written) {
            /* Data phase. */
            chip->input.cs = 0;
            chip->input.rd = 1;
            chip->input.wr = 0;
            chip->input.a1 = w.address >> 8;
            chip->input.a0 = 1;
            chip->input.data = w.value;
            ctx->delay = 2;
            if (w.address < 0x10)
                ctx->delay = 3;   /* SSG needs a longer delay */
            ctx->reg_pool[w.address & 0x1ff] = w.value;
            opna_lle_queue_pop(&ctx->writes);
        } else {
            /* Address phase. */
            chip->input.cs = 0;
            chip->input.rd = 1;
            chip->input.wr = 0;
            chip->input.a1 = w.address >> 8;
            chip->input.a0 = 0;
            chip->input.data = w.address & 0xff;
            ctx->delay = 2;
            if (w.address < 0x10)
                ctx->delay = 3;   /* SSG needs a longer delay */
            w.addr_written = true;
            ctx->writes.w[ctx->writes.tail].addr_written = true;
        }
    } else {
        /* Explicit idle bus state. */
        chip->input.cs = 1;
        chip->input.rd = 1;
        chip->input.wr = 1;
        chip->input.a0 = 0;
        chip->input.a1 = 0;
    }
}

/* --------------------------------------------------------------------- */
/* Bus settle (after clock pair) — Furnace's busy-status wait            */
/* --------------------------------------------------------------------- */
void opna_lle_bus_settle(OpnaLle *ctx)
{
    if (ctx->delay == 1) {
        if (!ctx->core.busy_cnt_en[1])
            ctx->delay = 0;
    }
}
