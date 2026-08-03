/*
 * Internal shared state for the MDPlayer YM2608-LLE native adapter.
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
#ifndef MDP_OPNA_INTERNAL_H
#define MDP_OPNA_INTERNAL_H

#include "../upstream/furnace-ym2608-lle/fmopna_2608.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#ifdef __cplusplus
extern "C" {
#endif

/* --------------------------------------------------------------------- */
/* Pinned reference                            */
/* --------------------------------------------------------------------- */
#define OPNA_FURNACE_COMMIT "3bdfc824fb7d2e813852f6fcfa482d8ea999588a"

/* Furnace's fixed SSG analogue gain into the digital mix (ym2608.cpp:
 * `fm_lle.o_analog*ssgVol*42`). Kept verbatim for parity; see mdplayer_opna_mix.c.
 * `MDP_OPNA_FURNACE_SSG_ANALOG_SCALE` is the canonical Prompt-4 name;
 * `OPNA_FURNACE_SSG_ANALOG_SCALE` is kept as a short alias. */
#define MDP_OPNA_FURNACE_SSG_ANALOG_SCALE 42
#define OPNA_FURNACE_SSG_ANALOG_SCALE MDP_OPNA_FURNACE_SSG_ANALOG_SCALE

/* --------------------------------------------------------------------- */
/* Serial PCM decoder (adapted from Furnace `acquire_lle`)                */
/* --------------------------------------------------------------------- */

/*
 * The YM2608 funnels its final FM/PCM output through a serial DAC on
 * o_opo (16-bit), strobed by o_s and shifted by o_sh1/o_sh2 (one per
 * stereo channel). We capture each left/right 16-bit word on the falling
 * edge of its shift line and only emit a completed stereo frame once both
 * channels have been captured (Furnace waits for `have0 && have1`).
 */
typedef struct {
    uint32_t dac_value;   /* running 16-bit serial accumulator                */
    int16_t dac_output[2];/* [0]=left capture, [1]=right capture (h0/h1 order) */
    bool last_s;
    bool last_sh1;
    bool last_sh2;
    bool have_left;       /* right-of-scheme: captured on o_sh1 falling edge   */
    bool have_right;      /* captured on o_sh2 falling edge                    */
} OpnaLleSerialDecoder;

void opna_lle_serial_reset(OpnaLleSerialDecoder *decoder);

/*
 * Advance the serial decoder by one clock pair. Returns true only when a
 * complete stereo frame has become available in *left/*right. When true is
 * returned the decoder is immediately ready to accumulate the next frame.
 */
bool opna_lle_serial_clock(OpnaLleSerialDecoder *decoder,
                           const fmopna_t *chip,
                           int16_t *left,
                           int16_t *right);

/* --------------------------------------------------------------------- */
/* Queued register write (adapted from Furnace QueuedWrite)              */
/* --------------------------------------------------------------------- */

typedef struct {
    int address;     /* 0..0x1ff                                        */
    int value;       /* data byte                                       */
    bool addr_written; /* address phase done; data phase pending (addrOrVal) */
} OpnaLleQueuedWrite;

#define OPNA_LLE_WRITE_QUEUE_SIZE 2048

typedef struct {
    OpnaLleQueuedWrite w[OPNA_LLE_WRITE_QUEUE_SIZE];
    int head;
    int tail;
} OpnaLleWriteQueue;

void opna_lle_queue_reset(OpnaLleWriteQueue *q);
bool opna_lle_queue_push(OpnaLleWriteQueue *q, int address, int value);
bool opna_lle_queue_front(OpnaLleWriteQueue *q, OpnaLleQueuedWrite *out);
void opna_lle_queue_pop(OpnaLleWriteQueue *q);

typedef struct OpnaLle OpnaLle;

/* Bus state machine: drive the pins once per valid prescaler phase
 * (before the clock pair) and settle the busy wait (after it). */
void opna_lle_bus_drive(OpnaLle *ctx);
void opna_lle_bus_settle(OpnaLle *ctx);

/* Final mix of one completed serial frame, adapted from Furnace (SSG
 * o_analog folded into both channels with the fixed 42 scale). */
void opna_lle_mix_frame(const fmopna_t *chip,
                        int16_t left_serial,
                        int16_t right_serial,
                        int fm_vol,
                        int ssg_vol,
                        int16_t *out_l,
                        int16_t *out_r);

/* --------------------------------------------------------------------- */
/* ADPCM-B external DRAM adapter (adapted from Furnace)                  */
/* --------------------------------------------------------------------- */

/*
 * The ADPCM-B block addresses an external 256 KiB DRAM via the
 * multiplexed o_dm + o_a8 bus, latched by CAS (column portion) and RAS
 * (row portion). We hold a 256 KiB backing buffer and drive input.dm /
 * input.dt0 back into the core according to memConfig (o_romcs / o_mden).
 *
 * `MDP_OPNA_ADPCM_RAM_BYTES` / `MDP_OPNA_ADPCM_ADDRESS_MASK` are the canonical
 * Prompt-4 names (256 KiB, 18-bit address wrapped by 0x3ffff). `OPNA_ADPCM_B_SIZE`
 * is kept as a short alias for backwards source compatibility.
 */
#ifndef MDP_OPNA_ADPCM_RAM_BYTES
#define MDP_OPNA_ADPCM_RAM_BYTES (256u * 1024u)
#endif
#ifndef MDP_OPNA_ADPCM_ADDRESS_MASK
#define MDP_OPNA_ADPCM_ADDRESS_MASK 0x3ffffu
#endif
#define OPNA_ADPCM_B_SIZE MDP_OPNA_ADPCM_RAM_BYTES

typedef struct {
    uint8_t mem[MDP_OPNA_ADPCM_RAM_BYTES];
    int ad_mem_addr;
    int cas;   /* previous o_cas */
    int ras;   /* previous o_ras */
} OpnaLleAdpcmBus;

void opna_lle_adpcm_reset(OpnaLleAdpcmBus *adpcm);
/* called once per clock pair before FMOPNA_Clock second half */
void opna_lle_adpcm_clock(OpnaLleAdpcmBus *adpcm, fmopna_t *chip, int mem_config);
/* Test-only loader: copy `len` bytes into the external DRAM at `offset`
 * (0 <= offset+len <= MDP_OPNA_ADPCM_RAM_BYTES). Not used by the production
 * render path; provided so native tests can prime write/read/wrap fixtures. */
void opna_lle_adpcm_load(OpnaLleAdpcmBus *adpcm, uint32_t offset,
                         const uint8_t *data, uint32_t len);

/* --------------------------------------------------------------------- */
/* Full reset sequence (adapted from Furnace)                            */
/* --------------------------------------------------------------------- */

/*
 * Furnace performs, with the initial pin state held:
 *   576 full clock pairs with ic asserted,
 *   576 full clock pairs with ic released,
 *   576 full clock pairs after ic re-asserted (returning to reset state
 *     between); then it re-zeroes the serial/ADPCM bus state.
 * We implement exactly that triple of 576-pair phases.
 *
 * `phase_pairs` (optional, may be NULL) is filled with the number of complete
 * low/high pairs executed in each of the three phases, and `*master_clock` is
 * advanced by the total (1728). The running pair counter is the native
 * absolute time represented by OpnaLle.master_clock; it never decreases and
 * each increment executes one complete low/high pair.
 */
typedef struct {
    uint64_t phase1_pairs;   /* ic asserted       */
    uint64_t phase2_pairs;   /* ic deasserted     */
    uint64_t phase3_pairs;   /* ic re-asserted    */
} OpnaLleResetCounts;

/*
 * Run the validated 576/576/576 chip-reset sequence on the core.
 *
 * When `clear_external_adpcm_ram` is true the 256 KiB external ADPCM backing
 * buffer is also zeroed (power-on case). When false the external RAM is left
 * untouched (chip-reset case), so ownership of the RAM stays with the session
 * adapter rather than being copied transiently. The serial decoder and ADPCM
 * bus latch state (ad_mem_addr/cas/ras) are always re-zeroed; only the RAM
 * contents are optional. This remains the SINGLE implementation of the
 * 576/576/576 sequence.
 */
void opna_lle_reset_core(fmopna_t *core,
                         OpnaLleSerialDecoder *decoder,
                         OpnaLleAdpcmBus *adpcm,
                         uint64_t *master_clock,
                         OpnaLleResetCounts *phase_pairs,
                         bool clear_external_adpcm_ram);

/*
 * Chip-state reset that preserves external ADPCM RAM contents.
 * Thin wrapper around opna_lle_reset_core(..., clear_external_adpcm_ram=false).
 */
void opna_lle_reset_chip_state(fmopna_t *core,
                               OpnaLleSerialDecoder *decoder,
                               OpnaLleAdpcmBus *adpcm,
                               uint64_t *master_clock,
                               OpnaLleResetCounts *phase_pairs);

/*
 * Zero the entire 256 KiB external ADPCM backing buffer (and its latch state).
 * Power-on only; never used for a chip reset.
 */
void opna_lle_clear_external_adpcm_ram(OpnaLleAdpcmBus *adpcm);

/* --------------------------------------------------------------------- */
/* Top-level driver                                                      */
/* --------------------------------------------------------------------- */

struct OpnaLle {
    fmopna_t core;
    OpnaLleWriteQueue writes;
    OpnaLleSerialDecoder serial;
    OpnaLleAdpcmBus adpcm;

    int mem_config;   /* Furnace memConfig: bit0 chooses o_romcs vs o_mden */
    int fm_vol;       /* 0..512, Furnace fmVol default                      */
    int ssg_vol;      /* 0..512, Furnace ssgVol default (128)               */

    int reg_pool[512];

    int delay;        /* Furnace `delay`: 0 idle, 1 data-phase-wait, 2/3 wr */

    /*
     * Native absolute time in complete low/high master-clock pairs. It never
     * decreases: reset advances it by 1728 (3 x 576) and every render frame
     * advances it by the number of pairs needed to capture its serial frame.
     * No floating-point timeline arithmetic is used anywhere in the driver.
     */
    uint64_t master_clock;
};

/* Absolute native time in complete low/high master-clock pairs (monotonic). */
uint64_t opna_lle_master_clock(const OpnaLle *ctx);

void opna_lle_reset(OpnaLle *ctx, bool clear_external_adpcm_ram);

/* Schedule a register write so the bus state machine drives it on the next
 * valid prescaler phase. `value==0xffffffff`-style guard not required here. */
void opna_lle_write(OpnaLle *ctx, int address, int value);

/*
 * Emit `frames` stereo int16 output samples. Each frame runs the core until
 * a complete serial stereo pair has been captured, driving any pending writes
 * on valid prescaler phases, and produces the Furnace-mixed PCM (serial DAC +
 * o_analog*ssgVol*42 on both channels).
 */
void opna_lle_render(OpnaLle *ctx, int16_t *out_l, int16_t *out_r, size_t frames);

/* --------------------------------------------------------------------- */
/* Timer and IRQ observations (native tests only)                        */
/* --------------------------------------------------------------------- */

/*
 * Expose the core's internal status / timer / IRQ observations so native tests
 * can pin deterministic timer-overflow and IRQ-assertion positions without
 * synthesizing timer status externally. These read the transistor-level core's
 * live state (the same fields Furnace inspects); they are diagnostic only and
 * are never used as final PCM.
 */
int opna_lle_obs_status_timer_a(const OpnaLle *ctx);   /* status_timer_a  */
int opna_lle_obs_status_timer_b(const OpnaLle *ctx);   /* status_timer_b  */
int opna_lle_obs_irq_pull(const OpnaLle *ctx);         /* o_irq_pull      */

#ifdef __cplusplus
}
#endif

#endif /* MDP_OPNA_INTERNAL_H */
