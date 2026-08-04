/*
 * MDPlayer YM2608-LLE session ABI implementation.
 *
 * Implements the versioned public session ABI on top of the existing
 * validated Furnace-compatible OpnaLle adapter. This file owns the opaque
 * session lifecycle (open/close/reset), clock advancement with timed
 * native-frame queuing and runtime cadence guarding, register writes and
 * pin-level status reads through the production scheduler, FIFO drain and
 * fixed-cadence resampling, and IRQ observation.
 *
 * Time semantics (see mdplayer_opna_internal.h): master_clock is absolute
 * time in complete low/high FMOPNA_Clock pairs; it never decreases; each
 * increment runs exactly one low/high pair. No floating-point timeline
 * arithmetic is used.
 *
 * Cadence policy (ABI version 1): the supported production profile is a fixed
 * 144 master clocks per complete stereo serial frame. Every completed frame is
 * compared with the previous one; any interval other than 144 latches a sticky
 * MDP_OPNA_ERR_UNSUPPORTED_CADENCE error and stops fixed-rate resampling until
 * the session is reset. Prescaler-select writes are always honored through the
 * bus; a cadence-changing write is detected, not silently ignored.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_session.h"

#include "../include/mdplayer_opna.h"
#include "mdplayer_opna_resampler.h"

#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

/* Set an error message (truncated to fit); safe with error==NULL. */
static void set_error(char *error, size_t error_size, const char *msg)
{
    if (error && error_size > 0) {
        size_t n = strlen(msg);
        if (n >= error_size)
            n = error_size - 1;
        memcpy(error, msg, n);
        error[n] = '\0';
    }
}

/* --------------------------------------------------------------------- */
/* Rate validation                                                       */
/* --------------------------------------------------------------------- */

static bool rate_supported(uint32_t rate)
{
    return rate == 44100u || rate == 48000u || rate == 96000u;
}

/* --------------------------------------------------------------------- */
/* Internal clock loop                                                   */
/* --------------------------------------------------------------------- */

/*
 * Clock one low/high pair on the existing OpnaLle driver and return true
 * when it produced a completed stereo frame (into *l, *r). Reflects the
 * production acquire loop in opna_lle_render: bus drive on prescaler grant,
 * low/high clock, master_clock advance, ADPCM bus, bus settle, serial decode.
 */
static bool clock_pair(OpnaLle *ctx, int16_t *l, int16_t *r)
{
    fmopna_t *chip = &ctx->core;
    bool can_write = (chip->prescaler_latch[1] & 1) != 0;

    if (can_write)
        opna_lle_bus_drive(ctx);

    FMOPNA_Clock(chip, 0);
    FMOPNA_Clock(chip, 1);
    ctx->master_clock++;

    opna_lle_adpcm_clock(&ctx->adpcm, chip, ctx->mem_config);

    if (can_write)
        opna_lle_bus_settle(ctx);

    return opna_lle_serial_clock(&ctx->serial, chip, l, r);
}

/*
 * Clock one low/high pair WITHOUT driving the write bus. Used to complete a
 * pin-level status read transaction so that the pre-driven read pins (rd/cs
 * asserted, a1/bank select) are not overwritten by a queued write opportunity.
 * Mirrors clock_pair in every other respect (master_clock advance, ADPCM bus,
 * serial decode).
 */
static bool clock_pair_read(OpnaLle *ctx)
{
    fmopna_t *chip = &ctx->core;

    FMOPNA_Clock(chip, 0);
    FMOPNA_Clock(chip, 1);
    ctx->master_clock++;
    opna_lle_adpcm_clock(&ctx->adpcm, chip, ctx->mem_config);

    int16_t l, r;
    (void)l; (void)r;
    return opna_lle_serial_clock(&ctx->serial, chip, &l, &r);
}

/*
 * Advance the adapter and, per completed frame, run the cadence guard. Queues
 * each completed stereo frame with the session FIFO. Returns an error if the
 * foo overflow happens, or if the cadence guard latches unsupported cadence
 * (in which case the offending frame is still queued but resampling stops).
 */
static int advance_frames(mdp_opna_session *sess, uint64_t target_clock)
{
    OpnaLle *ctx = &sess->lle;

    while (ctx->master_clock < target_clock) {
        int16_t l, r;
        if (clock_pair(ctx, &l, &r)) {
            uint64_t cur = ctx->master_clock;

            /* Cadence guard. The frame cadence is native_frame_clocks; after
             * the first frame, the interval from the previous frame must be
             * exactly native_frame_clocks. Any other interval latches a sticky
             * unsupported-cadence error and stops resampling. */
            if (!sess->cadence_synced) {
                sess->cadence_synced = true;
            } else {
                uint64_t interval = cur - sess->cadence_prev_clock;
                if (interval != sess->native_frame_clocks && !sess->cadence_error) {
                    sess->cadence_error = true;
                    sess->cadence_cur_clock = cur;
                    sess->cadence_observed_interval = interval;
                    /* Prescaler mode / most recent prescaler write, recorded
                     * from the live core for diagnostics. */
                    sess->cadence_prescaler_mode =
                        (int)ctx->core.prescaler_sel[1];
                    sess->cadence_prescaler_write =
                        (uint8_t)sess->prescaler_last_write;
                }
            }
            sess->cadence_prev_clock = cur;

            if (!mdp_opna_fifo_push(&sess->fifo, cur, l, r))
                return MDP_OPNA_ERR_FIFO_OVERFLOW;
        }
    }

    /* Frame starvation: the fixed-144 profile must produce a completed frame
     * within a bounded number of native clock pairs. If none arrives after
     * many frame periods (e.g. honoring 0x2F yields no serial output at all),
     * the cadence is unsupported. Deterministic: a real 144-clock profile
     * produces a frame every native_frame_clocks pairs, so FRAME_STARVE_CLOCKS
     * is many frames' worth of headroom without being fragile. */
    {
        const uint64_t FRAME_STARVE_CLOCKS = 16u * sess->native_frame_clocks;
        if (!sess->cadence_synced && !sess->cadence_error &&
            ctx->master_clock >= sess->cadence_starve_base + FRAME_STARVE_CLOCKS) {
            sess->cadence_error = true;
            sess->cadence_cur_clock = ctx->master_clock;
            sess->cadence_observed_interval = 0;
            sess->cadence_prescaler_mode = (int)ctx->core.prescaler_sel[1];
            sess->cadence_prescaler_write = sess->prescaler_last_write;
            return MDP_OPNA_ERR_UNSUPPORTED_CADENCE;
        }
    }

    return MDP_OPNA_OK;
}

/*
 * Clock from the current clock to `target_clock`. This is the ABI
 * advance path; it drives the production scheduler and queues frames, running
 * the cadence guard. It does NOT resample or drain.
 */
int mdp_opna_session_advance(mdp_opna_session *sess, uint64_t target_clock)
{
    uint64_t cur = sess->lle.master_clock;
    if (target_clock < cur)
        return MDP_OPNA_ERR_CLOCK_REGRESSION;
    if (sess->cadence_error)
        return MDP_OPNA_ERR_UNSUPPORTED_CADENCE;
    return advance_frames(sess, target_clock);
}

/* --------------------------------------------------------------------- */
/* ABI: open / close / reset                                             */
/* --------------------------------------------------------------------- */

uint32_t mdp_opna_get_abi_version(void)
{
    return MDP_OPNA_ABI_VERSION;
}

int mdp_opna_open(const mdp_opna_open_options *options,
                  mdp_opna_session **out_session,
                  char *error,
                  size_t error_size)
{
    if (!options || !out_session) {
        set_error(error, error_size, "mdp_opna_open: null argument");
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    }
    if (!rate_supported(options->output_rate_hz)) {
        set_error(error, error_size, "mdp_opna_open: unsupported output rate");
        return MDP_OPNA_ERR_UNSUPPORTED_RATE;
    }

    mdp_opna_session *sess = (mdp_opna_session *)calloc(1, sizeof(*sess));
    if (!sess) {
        set_error(error, error_size, "mdp_opna_open: out of memory");
        if (out_session)
            *out_session = NULL;
        return MDP_OPNA_ERR_OUT_OF_MEMORY;
    }

    sess->output_rate_hz = options->output_rate_hz;
    sess->rate_valid = true;
    sess->native_frame_clocks = MDP_OPNA_NATIVE_FRAME_CLOCKS;
    sess->prescaler_last_write = 0xFFu; /* Furnace default prescaler 0x2D */

    /*
     * Power-on: zero external RAM, reset chip state, reset scheduler/serial.
     * opna_lle_reset(clear_external_adpcm_ram=true) zeroes the whole context
     * (RAM included), runs the validated 576/576/576 sequence and enqueues the
     * Furnace default register init.
     */
    opna_lle_reset(&sess->lle, true);

    /* The public timeline origin is zero; the helper advanced master_clock by
     * 1728 while blanking the chip — no production time has elapsed yet. */
    sess->lle.master_clock = 0;

    /* Idle scheduler. The power-on default register init already enqueued
     * writes; we clear the queue so no stale write leaks, then reset the
     * per-session resetable state. */
    opna_lle_queue_reset(&sess->lle.writes);

    /* Create the fixed-cadence resampler for the requested output rate. */
    sess->resampler = mdp_opna_resampler_create(options->output_rate_hz);
    if (!sess->resampler) {
        set_error(error, error_size, "mdp_opna_open: resampler alloc failed");
        free(sess);
        if (out_session)
            *out_session = NULL;
        return MDP_OPNA_ERR_INTERNAL;
    }

    /* FIFO, cadence guard and resampler start clean. */
    mdp_opna_fifo_reset(&sess->fifo);
    mdp_opna_resampler_reset((mdp_opna_resampler *)sess->resampler);
    sess->cadence_synced = false;
    sess->cadence_error = false;

    *out_session = sess;
    return MDP_OPNA_OK;
}

int mdp_opna_reset_chip(mdp_opna_session *session)
{
    if (!session)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;

    OpnaLle *ctx = &session->lle;

    /*
     * Chip reset: run the existing validated 576/576/576 chip-reset helper
     * while PRESERVING the external ADPCM RAM (its contents are owned by the
     * session adapter, not by transient reset state). No 256 KiB copy, no heap
     * allocation. The serial decoder and ADPCM bus latch are re-zeroed by the
     * helper; the external RAM backing buffer is untouched.
     */
    opna_lle_reset_chip_state(&ctx->core, &ctx->serial, &ctx->adpcm,
                              &ctx->master_clock, NULL);

    /* Scheduler idle: drop pending writes and clean the reg shadow. */
    opna_lle_queue_reset(&ctx->writes);
    for (int i = 0; i < 512; i++)
        ctx->reg_pool[i] = 0;

    /* Public timeline origin returns to zero. */
    ctx->master_clock = 0;

    /* Empty the FIFO, reset the resampler and clear the cadence guard. The
     * configured output rate is preserved (it is not touched here). */
    mdp_opna_fifo_reset(&session->fifo);
    mdp_opna_resampler_reset((mdp_opna_resampler *)session->resampler);
    session->cadence_synced = false;
    session->cadence_error = false;
    session->cadence_prev_clock = 0;
    session->cadence_cur_clock = 0;
    session->cadence_observed_interval = 0;
    session->cadence_prescaler_mode = 0;
    session->cadence_prescaler_write = 0;
    session->cadence_starve_base = 0;

    return MDP_OPNA_OK;
}

int mdp_opna_clear_adpcm_ram(mdp_opna_session *session, uint8_t fill_value)
{
    if (!session)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    /* Fill all 256 KiB. Does not reset the chip, alter time, clear queued
     * audio, alter the scheduler or the resampler history. */
    memset(session->lle.adpcm.mem, fill_value, MDP_OPNA_ADPCM_RAM_BYTES);
    return MDP_OPNA_OK;
}

void mdp_opna_close(mdp_opna_session *session)
{
    if (!session)
        return;
    mdp_opna_resampler_destroy((mdp_opna_resampler *)session->resampler);
    free(session);
}

/* --------------------------------------------------------------------- */
/* ABI: time, writes, reads, IRQ                                         */
/* --------------------------------------------------------------------- */

uint64_t mdp_opna_get_master_clock(const mdp_opna_session *session)
{
    if (!session)
        return 0;
    return session->lle.master_clock;
}

int mdp_opna_get_output_latency_frames(const mdp_opna_session *session,
                                       uint32_t *out_frames)
{
    if (!session || !out_frames)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    int latency = mdp_opna_resampler_output_latency(
        (const mdp_opna_resampler *)session->resampler);
    if (latency < 0) {
        *out_frames = 0;
        return MDP_OPNA_ERR_INTERNAL;
    }
    *out_frames = (uint32_t)latency;
    return MDP_OPNA_OK;
}

int mdp_opna_advance_to(mdp_opna_session *session, uint64_t master_clock)
{
    if (!session)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    return mdp_opna_session_advance(session, master_clock);
}

int mdp_opna_write_register(mdp_opna_session *session,
                            uint64_t requested_master_clock,
                            uint8_t bank,
                            uint8_t address,
                            uint8_t value)
{
    if (!session)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    if (requested_master_clock < session->lle.master_clock)
        return MDP_OPNA_ERR_CLOCK_REGRESSION;

    int rc = mdp_opna_session_advance(session, requested_master_clock);
    if (rc != MDP_OPNA_OK)
        return rc;

    int full_address = (bank ? (0x100 | address) : address);
    /* Use the existing production bus scheduler (queue). */
    opna_lle_write(&session->lle, full_address, value);

    /* Track the most recent prescaler-select write (any bank for simplicity;
     * 0x2D/0x2E/0x2F are bank 0). Used by the cadence-guard diagnostics. */
    if (address == 0x2d || address == 0x2e || address == 0x2f)
        session->prescaler_last_write = value;

    return MDP_OPNA_OK;
}

/*
 * Perform a pin-level status read: CS asserted, RD asserted, WR deasserted,
 * A0 low (status), A1 = bank. Run one complete low/high clock pair so the core
 * latches read_bus -> o_data, capture the output data bus, then return the bus
 * to idle (cs=1, rd=1, a0=0, a1=0). The requested_master_clock is the earliest
 * transaction time; the session clock advances by the read clock pairs.
 */
static int do_status_read(mdp_opna_session *session, uint8_t bank,
                          uint8_t *out_value)
{
    OpnaLle *ctx = &session->lle;
    fmopna_t *chip = &ctx->core;

    /* Drive the read pins: cs=0, rd=0, wr=1, a0=0, a1=bank. */
    chip->input.cs = 0;
    chip->input.rd = 0;
    chip->input.wr = 1;
    chip->input.a0 = 0;
    chip->input.a1 = (bank != 0) ? 1 : 0;
    chip->input.data = 0;

    /* One complete low/high pair latches read_bus -> o_data and advances the
     * adapter (ADPCM bus, serial decoder) in lock-step. Uses clock_pair_read so
     * the driven read pins are not overwritten by a queued write opportunity. */
    clock_pair_read(ctx);

    /* Return the bus to idle. The core computed read_bus during the pair and
     * o_data now mirrors it. */
    chip->input.cs = 1;
    chip->input.rd = 1;
    chip->input.a0 = 0;
    chip->input.a1 = 0;

    *out_value = (uint8_t)(chip->o_data & 0xff);
    return MDP_OPNA_OK;
}

int mdp_opna_read_status(mdp_opna_session *session,
                         uint64_t requested_master_clock,
                         uint8_t bank,
                         uint8_t *out_value)
{
    if (!session || !out_value)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    if (requested_master_clock < session->lle.master_clock)
        return MDP_OPNA_ERR_CLOCK_REGRESSION;

    int rc = mdp_opna_session_advance(session, requested_master_clock);
    if (rc != MDP_OPNA_OK)
        return rc;

    /* Complete any writes queued at/before the request time (they were flushed
     * during advance). Then perform the pin-level read transaction. */
    return do_status_read(session, bank, out_value);
}

int mdp_opna_get_irq(mdp_opna_session *session, int *out_asserted)
{
    if (!session || !out_asserted)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    /* Return current chip IRQ level. Never advances time, never clears IRQ,
     * never modifies the core. Uses the existing IRQ observation helper. */
    *out_asserted = opna_lle_obs_irq_pull(&session->lle) ? 1 : 0;
    return MDP_OPNA_OK;
}

/* --------------------------------------------------------------------- */
/* ABI: drain + fixed-cadence resampling                                 */
/* --------------------------------------------------------------------- */

int mdp_opna_drain_audio(mdp_opna_session *session,
                         int16_t *interleaved_stereo,
                         int requested_frames,
                         int *out_drained_frames)
{
    if (!session || !interleaved_stereo || !out_drained_frames)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;
    if (requested_frames < 0)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;

    /* After a cadence-guard failure the fixed-rate resampler is stopped.
     * We still return the error to the caller on the next drain/advance call. */
    if (session->cadence_error)
        return MDP_OPNA_ERR_UNSUPPORTED_CADENCE;

    uint64_t clock_before = session->lle.master_clock;

    /* Fixed-rate cadence state is clean here (cadence_error was already
     * rejected above). Feed queued native frames through the SpeexDSP-backed
     * resampler into the caller's output buffer, honoring partial consumption:
     * we only pop from the FIFO the frames SpeexDSP actually consumed, keeping
     * any unconsumed tail queued for the next drain. */
    mdp_opna_resampler *rs = (mdp_opna_resampler *)session->resampler;
    mdp_opna_timed_frame frame;
    int produced = 0;

    /* Reusable bounded input staging buffer; no allocation during drain. */
    enum { STAGE_CAP = 256 };
    int16_t stage[STAGE_CAP * 2];
    mdp_opna_timed_frame meta[STAGE_CAP];

    for (;;) {
        /* A bounded chunk of the FIFO front, without consuming anything. */
        uint32_t n = mdp_opna_fifo_copy_front(&session->fifo, meta, STAGE_CAP);
        if (n == 0)
            break;
        for (uint32_t i = 0; i < n; i++) {
            stage[2 * i] = meta[i].left;
            stage[2 * i + 1] = meta[i].right;
        }

        int out_room = requested_frames - produced;
        if (out_room <= 0)
            break;

        int in_frames = (int)n;
        int out_frames = out_room;
        int rc = mdp_opna_resampler_process(rs, stage, &in_frames,
                                            interleaved_stereo + 2 * produced,
                                            &out_frames);
        if (rc != 0) {
            /* SpeexDSP failed: no frames were consumed from the FIFO (we only
             * peeked). Leave the queue untouched and surface an error. */
            *out_drained_frames = produced;
            return MDP_OPNA_ERR_INTERNAL;
        }

        /* SpeexDSP consumed a strict prefix of the staged block. Remove exactly
         * those from the FIFO; the unconsumed tail stays queued for the next
         * drain call. */
        for (int k = 0; k < in_frames; k++)
            mdp_opna_fifo_pop(&session->fifo, &frame);

        produced += out_frames;
        if (out_frames == 0)
            break;   /* caller's buffer filled before any more output could fit */
    }

    *out_drained_frames = produced;

    /* Drain is a pure FIFO+resampler consume: master clock must be unchanged,
     * and no scheduling happens. */
    if (session->lle.master_clock != clock_before)
        return MDP_OPNA_ERR_INTERNAL;

    return MDP_OPNA_OK;
}