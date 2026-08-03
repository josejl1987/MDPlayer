/*
 * MDPlayer YM2608-LLE session ABI implementation.
 *
 * Implements the versioned public session ABI on top of the existing
 * validated Furnace-compatible OpnaLle adapter. This file owns the opaque
 * session lifecycle (open/close/reset), clock advancement with timed
 * native-frame queuing, register writes and status reads through the existing
 * production scheduler, and FIFO drain.
 *
 * Time semantics (see mdplayer_opna_internal.h): master_clock is absolute
 * time in complete low/high FMOPNA_Clock pairs; it never decreases; each
 * increment runs exactly one low/high pair. No floating-point timeline
 * arithmetic is used.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_session.h"

#include "../include/mdplayer_opna.h"

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

int mdp_opna_session_advance(mdp_opna_session *sess, uint64_t target_clock)
{
    uint64_t cur = sess->lle.master_clock;
    if (target_clock < cur)
        return MDP_OPNA_ERR_CLOCK_REGRESSION;

    OpnaLle *ctx = &sess->lle;

    while (ctx->master_clock < target_clock) {
        int16_t l, r;
        if (clock_pair(ctx, &l, &r)) {
            mdp_opna_timed_frame frame;
            frame.master_clock = ctx->master_clock;
            frame.left = l;
            frame.right = r;
            if (!mdp_opna_fifo_push(&sess->fifo,
                                    frame.master_clock,
                                    frame.left,
                                    frame.right))
                return MDP_OPNA_ERR_FIFO_OVERFLOW;
        }
    }
    return MDP_OPNA_OK;
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

    /* calloc zeroes the entire session: external ADPCM RAM, FIFO, LLE state
     * struct, master clock = 0, IRQ deasserted (o_irq_pull starts 0+chip). */
    sess->output_rate_hz = options->output_rate_hz;
    sess->rate_valid = true;

    /*
     * Power-on: initialize the existing native adapter and run the existing
     * validated 576/576/576 reset sequence. opna_lle_reset() clears the write
     * queue, runs the triple reset, and enqueues the Furnace default register
     * init. It also zeroes ADPCM RAM (opna_lle_adpcm_reset fills it with 0),
     * which satisfies the "fresh RAM is zero" requirement without duplicating
     * reset logic here.
     */
    opna_lle_reset(&sess->lle);

    /*
     * The power-on reset helper advanced master_clock by 1728 (3 x 576) while
     * blanking the chip. The ABI session clock must start at zero (§7 step 12),
     * so we reset it. No queued audio is produced during this initial reset.
     */
    sess->lle.master_clock = 0;

    /* Clear the bus scheduler (post-reset the queue already holds the default
     * init registers; draining to empty would lose them, so we instead rely on
     * opna_lle_reset's own queue state as the power-on scheduler state. The
     * queue is empty-after-render; here we clear it so no stale write leaks). */
    opna_lle_queue_reset(&sess->lle.writes);

    /* Clear empty the serial decoder (already reset by opna_lle_reset). */
    opna_lle_serial_reset(&sess->lle.serial);

    /* Clear the native-frame FIFO. */
    mdp_opna_fifo_reset(&sess->fifo);

    /* Clear resampler state (none in the fixed-cadence case; the FIFO is the
     * audio staging area). master_clock is already 0 from calloc. IRQ state is
     * deasserted by the core reset (o_irq_pull starts 0). */

    *out_session = sess;
    return MDP_OPNA_OK;
}

int mdp_opna_reset_chip(mdp_opna_session *session)
{
    if (!session)
        return MDP_OPNA_ERR_INVALID_ARGUMENT;

    OpnaLle *ctx = &session->lle;

    /*
     * Run the existing validated 576/576/576 chip-reset helper on the live
     * core. The helper (opna_lle_reset_core) blanks the core, runs the triple
     * reset, and re-zeroes serial + ADPCM bus latch state — but it also clears
     * the 256 KiB external ADPCM RAM via opna_lle_adpcm_reset. Per the ABI,
     * chip reset must PRESERVE external ADPCM RAM, so we retain a transient
     * copy and restore it afterwards. This performs no heap allocation (the
     * copy lives on the stack) and does not touch the configured rate.
     *
     * We call opna_lle_reset_core directly (not the higher-level
     * opna_lle_reset) so we do not re-apply the Furnace default register init
     * or re-clear RAM through a second path; the chip is left in its raw
     * post-reset pin state with ic re-asserted, exactly as the validated
     * helper specifies.
     */
    uint8_t saved_ram[MDP_OPNA_ADPCM_RAM_BYTES];
    memcpy(saved_ram, ctx->adpcm.mem, sizeof(saved_ram));

    opna_lle_reset_core(&ctx->core, &ctx->serial, &ctx->adpcm,
                        &ctx->master_clock, NULL);

    /* Preserve the external ADPCM RAM across the reset. */
    memcpy(ctx->adpcm.mem, saved_ram, sizeof(saved_ram));

    /* Reset the scheduler (drop any pending writes, clean reg_pool). */
    opna_lle_queue_reset(&ctx->writes);
    for (int i = 0; i < 512; i++)
        ctx->reg_pool[i] = 0;

    /* Reset serial partial-frame state (already re-zeroed by the helper, but
     * keep it explicit), empty the native-frame FIFO, and reset the master
     * clock to zero (the helper advanced it by 1728). */
    ctx->master_clock = 0;
    mdp_opna_fifo_reset(&session->fifo);

    /* Reset IRQ observation state. The core reset re-zeroed o_irq_pull via
     * memset; the FIFO/serial transients are cleared above. */

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

    /* Status comes from the live LLE core. We reuse the validated observation
     * helpers for the timer status bits (transistor-level core state, the same
     * fields a real status read consumes) and read the chip's live flag
     * registers (busy, EOS, Brdy, Zero, ADPCM-start) directly — none of these
     * are shadow, cached, or MDSound state; all come straight from the LLE
     * core. */
    fmopna_t *chip = &session->lle.core;
    uint8_t status = 0;
    if (chip->busy_cnt_en[1])
        status |= 0x80;
    if (opna_lle_obs_status_timer_a(&session->lle))
        status |= 0x01;
    if (opna_lle_obs_status_timer_b(&session->lle))
        status |= 0x02;
    if (bank == 1) {
        if (chip->status_eos)
            status |= 0x04;
        if (chip->status_brdy)
            status |= 0x08;
        if (chip->status_zero)
            status |= 0x10;
        if (chip->ad_start_l[0])
            status |= 0x20;
    }
    *out_value = status;
    return MDP_OPNA_OK;
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
/* ABI: drain                                                            */
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

    uint64_t clock_before = session->lle.master_clock;

    int produced = 0;
    mdp_opna_timed_frame frame;
    while (produced < requested_frames &&
           mdp_opna_fifo_pop(&session->fifo, &frame)) {
        interleaved_stereo[2 * produced] = frame.left;
        interleaved_stereo[2 * produced + 1] = frame.right;
        produced++;
    }

    *out_drained_frames = produced;

    /* Drain is a pure FIFO consume: master clock must be unchanged. */
    if (session->lle.master_clock != clock_before)
        return MDP_OPNA_ERR_INTERNAL;

    return MDP_OPNA_OK;
}
