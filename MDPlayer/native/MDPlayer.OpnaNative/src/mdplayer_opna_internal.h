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
 * `fm_lle.o_analog*ssgVol*42`). Kept verbatim for parity; see mdplayer_opna_mix.c. */
#define OPNA_FURNACE_SSG_ANALOG_SCALE 42

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
 */
#define OPNA_ADPCM_B_SIZE 0x40000   /* 256 KiB */

typedef struct {
    uint8_t mem[OPNA_ADPCM_B_SIZE];
    int ad_mem_addr;
    int cas;   /* previous o_cas */
    int ras;   /* previous o_ras */
} OpnaLleAdpcmBus;

void opna_lle_adpcm_reset(OpnaLleAdpcmBus *adpcm);
/* called once per clock pair before FMOPNA_Clock second half */
void opna_lle_adpcm_clock(OpnaLleAdpcmBus *adpcm, fmopna_t *chip, int mem_config);

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
 */
void opna_lle_reset_core(fmopna_t *core,
                         OpnaLleSerialDecoder *decoder,
                         OpnaLleAdpcmBus *adpcm);

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
};

void opna_lle_reset(OpnaLle *ctx);

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

#ifdef __cplusplus
}
#endif

#endif /* MDP_OPNA_INTERNAL_H */
