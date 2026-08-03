/*
 * Timed native-frame FIFO for the MDPlayer YM2608-LLE session ABI.
 *
 * Simple fixed-capacity ring buffer. head points at the next write slot;
 * count tracks occupancy; the mask gives deterministic non-modulo wraparound
 * for a power-of-two capacity. See mdplayer_opna_fifo.h for the contract.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_fifo.h"

void mdp_opna_fifo_reset(mdp_opna_fifo *f)
{
    f->head = 0;
    f->count = 0;
    /* max_observed is intentionally preserved across reset: it records the
     * busiest depth ever reached and is only cleared on (re)init. */
}

uint32_t mdp_opna_fifo_size(const mdp_opna_fifo *f)
{
    return f->count;
}

bool mdp_opna_fifo_empty(const mdp_opna_fifo *f)
{
    return f->count == 0;
}

bool mdp_opna_fifo_full(const mdp_opna_fifo *f)
{
    return f->count == MDP_OPNA_FIFO_CAPACITY;
}

bool mdp_opna_fifo_push(mdp_opna_fifo *f,
                        uint64_t master_clock,
                        int16_t left,
                        int16_t right)
{
    if (f->count == MDP_OPNA_FIFO_CAPACITY) {
        /* Overflow is an explicit error; old data is never overwritten. */
        return false;
    }
    mdp_opna_timed_frame *slot = &f->buf[(f->head + f->count) & MDP_OPNA_FIFO_MASK];
    slot->master_clock = master_clock;
    slot->left = left;
    slot->right = right;
    f->count++;
    if (f->count > f->max_observed)
        f->max_observed = f->count;
    return true;
}

bool mdp_opna_fifo_pop(mdp_opna_fifo *f, mdp_opna_timed_frame *out)
{
    if (f->count == 0)
        return false;
    const mdp_opna_timed_frame *slot = &f->buf[f->head];
    *out = *slot;
    f->head = (f->head + 1u) & MDP_OPNA_FIFO_MASK;
    f->count--;
    return true;
}

bool mdp_opna_fifo_peek(const mdp_opna_fifo *f, mdp_opna_timed_frame *out)
{
    if (f->count == 0)
        return false;
    *out = f->buf[f->head];
    return true;
}

uint32_t mdp_opna_fifo_max_observed(const mdp_opna_fifo *f)
{
    return f->max_observed;
}
