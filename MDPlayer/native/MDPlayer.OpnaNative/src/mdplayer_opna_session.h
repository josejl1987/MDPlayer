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
    uint8_t  prescaler_last_write;   /* most recent 0x2D/2E/2F write value  */
    int      prescaler_sel;          /* QW1: expected core prescaler_sel
                                        (2 = fixed 144-clock mode); the write
                                        path enforces it stays 2 */
    bool rate_valid;                 /* true once opened with a supported rate */

    /* Fixed-cadence PC-98 production profile. */
    uint64_t native_frame_clocks;    /* 144: master clocks per complete frame */

    /* Runtime cadence guard state. When a completed frame is not exactly
     * `native_frame_clocks` after the previous one, cadence_error is set and
     * fixed-rate resampling is stopped until the session is reset. */
    bool cadence_synced;             /* false until the first frame is seen  */
    bool cadence_error;              /* sticky: unsupported cadence detected */
    uint64_t cadence_prev_clock;     /* completion clock of previous frame   */
    uint64_t cadence_cur_clock;      /* completion clock of offending frame  */
    uint64_t cadence_starve_base;    /* clock at which the current (unsynced)
                                        period began, for starvation detect  */
    uint64_t cadence_observed_interval;
    uint8_t  cadence_prescaler_write;/* most recent 0x2D/2E/2F write value   */
    int      cadence_prescaler_mode; /* core prescaler_sel[1] at failure     */

    /* Resampler state (fixed-cadence polyphase). NULL until opened. */
    void *resampler;
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
