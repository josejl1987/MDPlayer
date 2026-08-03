/*
 * MDPlayer YM2608-LLE native backend — public session ABI (version 1).
 *
 * A small, opaque, versioned C ABI exposing the validated
 * Furnace-compatible YM2608-LLE implementation through a session object.
 *
 * The YM2608 is driven in native master-clock pairs. This ABI:
 *   - opens/closes a session (power-on reset + zeroed 256 KiB ADPCM RAM),
 *   - runs the existing production reset / write / read / IRQ paths,
 *   - advances to an absolute monotonic master clock,
 *   - queues complete stereo serial frames into a bounded timed FIFO,
 *   - drains queued frames (fixed-rate resampling reserved for a case where
 *     native cadence is proven constant; when cadence is variable the FIFO
 *     is drained without resampling).
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp / ym2608.h
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef MDPLAYER_OPNA_H
#define MDPLAYER_OPNA_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* --------------------------------------------------------------------- */
/* Version                                                               */
/* --------------------------------------------------------------------- */

/* ABI version this header/library implements. */
#define MDP_OPNA_ABI_VERSION 1u

/* --------------------------------------------------------------------- */
/* Fixed hardware constants (not configurable in ABI version 1)         */
/* --------------------------------------------------------------------- */

/* YM2608 master clock, in Hz. */
#define MDP_OPNA_MASTER_CLOCK_HZ 7987200u
/* External ADPCM-B DRAM capacity, in bytes (256 KiB). */
#ifndef MDP_OPNA_ADPCM_RAM_BYTES
#define MDP_OPNA_ADPCM_RAM_BYTES 262144u
#endif

/* --------------------------------------------------------------------- */
/* Result codes (never expose raw errno)                                */
/* --------------------------------------------------------------------- */

typedef enum mdp_opna_result {
    MDP_OPNA_OK = 0,
    MDP_OPNA_ERR_INVALID_ARGUMENT = -1,
    MDP_OPNA_ERR_OUT_OF_MEMORY = -2,
    MDP_OPNA_ERR_CLOCK_REGRESSION = -3,
    MDP_OPNA_ERR_FIFO_OVERFLOW = -4,
    MDP_OPNA_ERR_UNSUPPORTED_RATE = -5,
    MDP_OPNA_ERR_UNSUPPORTED_CADENCE = -6,
    MDP_OPNA_ERR_INTERNAL = -7
} mdp_opna_result;

#define MDP_OPNA_RESULT_MAX_TEXT 128

/* --------------------------------------------------------------------- */
/* Open options                                                          */
/* --------------------------------------------------------------------- */

typedef struct mdp_opna_open_options {
    /* Output sample rate in Hz. Only 44100, 48000 and 96000 are accepted.
     * Zero is not a default. */
    uint32_t output_rate_hz;
} mdp_opna_open_options;

/* --------------------------------------------------------------------- */
/* Opaque session                                                        */
/* --------------------------------------------------------------------- */

typedef struct mdp_opna_session mdp_opna_session;

/* --------------------------------------------------------------------- */
/* Public ABI                                                            */
/* --------------------------------------------------------------------- */

/* Return exactly MDP_OPNA_ABI_VERSION. */
uint32_t mdp_opna_get_abi_version(void);

/*
 * Open a session at the requested output rate and run the existing power-on
 * sequence. On success *out_session points at an opaque session. On failure
 * *out_session is NULL and an explicit result code is returned; `error` (when
 * supplied with error_size > 0) receives a NUL-terminated message.
 */
int mdp_opna_open(const mdp_opna_open_options *options,
                  mdp_opna_session **out_session,
                  char *error,
                  size_t error_size);

/*
 * Run the existing validated chip-reset helper. Preserves the external ADPCM
 * RAM and the configured output rate; empties queued audio and resets time
 * to zero. Performs no memory allocation.
 */
int mdp_opna_reset_chip(mdp_opna_session *session);

/*
 * Fill all 256 KiB of external ADPCM RAM with `fill_value`. Does not reset
 * the chip, alter time, clear queued audio, alter the scheduler or the
 * resampler history.
 */
int mdp_opna_clear_adpcm_ram(mdp_opna_session *session, uint8_t fill_value);

/*
 * Advance the chip so that `master_clock` is the current absolute time.
 * Rejects a clock that would regress. Queues each completed stereo frame
 * with its completion clock. Stops exactly at the requested clock.
 */
int mdp_opna_advance_to(mdp_opna_session *session, uint64_t master_clock);

/*
 * Schedule one register write at the requested clock via the existing
 * production bus scheduler. Preserves call order for equal clocks.
 */
int mdp_opna_write_register(mdp_opna_session *session,
                            uint64_t requested_master_clock,
                            uint8_t bank,
                            uint8_t address,
                            uint8_t value);

/*
 * Read the live LLE status at the requested clock. `out_value` receives the
 * status byte.
 */
int mdp_opna_read_status(mdp_opna_session *session,
                         uint64_t requested_master_clock,
                         uint8_t bank,
                         uint8_t *out_value);

/*
 * Query the current chip IRQ level. Returns 0 or 1 through *out_asserted.
 * Never advances time, never clears IRQ, never modifies the core.
 */
int mdp_opna_get_irq(mdp_opna_session *session, int *out_asserted);

/*
 * Drain already-queued timed frames into `interleaved_stereo`
 * (left,right,left,right,...). Returns no more than `requested_frames`;
 * returns fewer when insufficient input is queued. Never advances time.
 * `out_drained_frames` receives the count actually produced.
 */
int mdp_opna_drain_audio(mdp_opna_session *session,
                         int16_t *interleaved_stereo,
                         int requested_frames,
                         int *out_drained_frames);

/* Current absolute master clock. Pure query; never advances or mutates. */
uint64_t mdp_opna_get_master_clock(const mdp_opna_session *session);

/* Close and free the session. Accepts NULL (no-op). */
void mdp_opna_close(mdp_opna_session *session);

#ifdef __cplusplus
}
#endif

#endif /* MDPLAYER_OPNA_H */
