/*
 * Timed native-frame FIFO for the MDPlayer YM2608-LLE session ABI.
 *
 * A fixed-capacity ring buffer of complete stereo serial frames, each tagged
 * with the master clock at which it became available. The buffer is bounded
 * (capacity defined once here), never allocates per frame, preserves exact
 * insertion order, wraps deterministically, treats overflow as an explicit
 * error (never overwriting old data), and can be emptied on reset.
 *
 * The session is single-threaded; no locking is used.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef MDP_OPNA_FIFO_H
#define MDP_OPNA_FIFO_H

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* --------------------------------------------------------------------- */
/* Capacity                                                              */
/* --------------------------------------------------------------------- */

/*
 * Fixed capacity defined exactly once here. Chosen to comfortably hold a
 * wide drain window: the slowest supportable drain ratio (native 55466 Hz ->
 * 44100 Hz) consumes ~0.795 native frames per output frame, so a 96 ms
 * window needs ~10665 native frames; 16384 leaves headroom for jitter while
 * remaining a small power-of-two ring (16384 x 16 bytes = 256 KiB).
 */
#define MDP_OPNA_FIFO_CAPACITY_SHIFT 14
#define MDP_OPNA_FIFO_CAPACITY (1u << MDP_OPNA_FIFO_CAPACITY_SHIFT)
#define MDP_OPNA_FIFO_MASK    (MDP_OPNA_FIFO_CAPACITY - 1u)

/* --------------------------------------------------------------------- */
/* Frame and ring                                                         */
/* --------------------------------------------------------------------- */

typedef struct mdp_opna_timed_frame {
    uint64_t master_clock;  /* clock at which this complete stereo frame appears */
    int16_t left;
    int16_t right;
} mdp_opna_timed_frame;

typedef struct {
    mdp_opna_timed_frame buf[MDP_OPNA_FIFO_CAPACITY];
    uint32_t head;          /* index of next write slot    */
    uint32_t count;         /* number of elements present   */
    uint32_t max_observed;  /* highest count ever reached   */
} mdp_opna_fifo;

/* Empty the FIFO. O(1); clears the observed-watermark and leaves the backing
 * array untouched. Callers must have a valid, initialized fifo struct. */
void mdp_opna_fifo_reset(mdp_opna_fifo *f);

/* Number of elements currently in the FIFO. */
uint32_t mdp_opna_fifo_size(const mdp_opna_fifo *f);

/* True when the FIFO has no elements. */
bool mdp_opna_fifo_empty(const mdp_opna_fifo *f);

/* True when the FIFO cannot accept another element. */
bool mdp_opna_fifo_full(const mdp_opna_fifo *f);

/*
 * Push one timed frame at the head. Returns false on overflow WITHOUT
 * overwriting old data (the ring is left unchanged). On success `max_observed`
 * is updated.
 */
bool mdp_opna_fifo_push(mdp_opna_fifo *f,
                        uint64_t master_clock,
                        int16_t left,
                        int16_t right);

/*
 * Pop the oldest frame into *out. Returns false when empty. Out-of-order
 * peeking is not supported; callers must drain in FIFO order.
 */
bool mdp_opna_fifo_pop(mdp_opna_fifo *f, mdp_opna_timed_frame *out);

/*
 * Return the oldest frame without consuming it (as needed by the drain logic
 * to decide whether enough input remains). Returns false when empty.
 */
bool mdp_opna_fifo_peek(const mdp_opna_fifo *f, mdp_opna_timed_frame *out);

/* Highest depth ever observed (internal diagnostic / tests). */
uint32_t mdp_opna_fifo_max_observed(const mdp_opna_fifo *f);

#ifdef __cplusplus
}
#endif

#endif /* MDP_OPNA_FIFO_H */
