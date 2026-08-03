/*
 * Session state for the MDPlayer YM2608-LLE public ABI.
 *
 * Wraps the existing validated OpnaLle adapter plus the timed native-frame
 * FIFO and the current ABI configuration. The session is opaque publicly.
 *
 * Session rules enforced here:
 *   - master_clock is monotonic (never decreases; equal-clock operations
 *     execute in API-call order);
 *   - each clock increment executes one complete low/high FMOPNA_Clock pair;
 *   - every completed stereo serial frame is queued with its completion
 *     clock;
 *   - advance/drain never move the chip clock (drain is a pure FIFO consume);
 *   - power-on/reset run the existing validated reset helper.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef MDP_OPNA_SESSION_H
#define MDP_OPNA_SESSION_H

#include "../include/mdplayer_opna.h"
#include "mdplayer_opna_internal.h"
#include "mdplayer_opna_fifo.h"

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct mdp_opna_session {
    OpnaLle lle;                     /* existing validated Furnace adapter   */
    mdp_opna_fifo fifo;              /* timed bounded native-frame FIFO     */
    uint32_t output_rate_hz;         /* validated 44100 / 48000 / 96000     */
    bool rate_valid;                 /* false until output_rate_hz set      */
};

/*
 * Advance the underlying OpnaLle from its current master clock to exactly
 * `target_clock`, running all existing production components (bus scheduler,
 * ADPCM adapter, serial decoder, mixer, timer/IRQ observation) each clock
 * pair and queuing each completed stereo frame with its completion clock
 * into `sess->fifo`.
 *
 * Returns MDP_OPNA_OK, MDP_OPNA_ERR_CLOCK_REGRESSION when `target_clock` is
 * below the current clock, or MDP_OPNA_ERR_FIFO_OVERFLOW when queuing would
 * exceed the bounded FIFO capacity (in which case the state is unchanged and
 * the caller must drain first).
 */
int mdp_opna_session_advance(mdp_opna_session *sess, uint64_t target_clock);

#ifdef __cplusplus
}
#endif

#endif /* MDP_OPNA_SESSION_H */
