/*
 * YM2608-LLE serial PCM decoder.
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp (DivPlatformYM2608::acquire_lle)
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_internal.h"

/*
 * Furnace decode logic (verbatim semantics):
 *
 *   if (!fm_lle.o_s && lastS) {                      // falling o_s
 *     if (!fm_lle.o_sh1 && lastSH) {                 // falling o_sh1
 *       dacOut[0]=dacVal^0x8000; have0=true;         // capture left
 *     }
 *     if (!fm_lle.o_sh2 && lastSH2) {                // falling o_sh2
 *       dacOut[1]=dacVal^0x8000; have1=true;         // capture right
 *     }
 *     dacVal>>=1;                                    // shift accumulator
 *     dacVal|=(fm_lle.o_opo&1)<<15;                  // push new bit in
 *     lastSH=o_sh1; lastSH2=o_sh2;                   // re-latch sh edges
 *   }
 *   lastS=o_s;
 *
 * The shift happens on falling o_s; each channel's 16-bit word is captured on
 * the falling edge of its own shift signal. We emit a frame only once both
 * channels have been captured (Furnace waits for `have0 && have1`).
 */
bool opna_lle_serial_clock(OpnaLleSerialDecoder *d,
                           const fmopna_t *chip,
                           int16_t *left,
                           int16_t *right)
{
    bool have0 = d->have_left;
    bool have1 = d->have_right;

    if (!chip->o_s && d->last_s) {
        if (!chip->o_sh1 && d->last_sh1) {
            d->dac_output[0] = (int16_t)(d->dac_value ^ 0x8000u);
            have0 = true;
        }
        if (!chip->o_sh2 && d->last_sh2) {
            d->dac_output[1] = (int16_t)(d->dac_value ^ 0x8000u);
            have1 = true;
        }

        d->dac_value >>= 1;
        d->dac_value |= ((uint32_t)(chip->o_opo & 1)) << 15;

        d->last_sh1 = chip->o_sh1;
        d->last_sh2 = chip->o_sh2;
    }
    d->last_s = chip->o_s;

    d->have_left = have0;
    d->have_right = have1;

    if (have0 && have1) {
        /* Consume the completed frame: clear both captures so the decoder is
         * immediately ready to accumulate the next stereo frame. */
        d->have_left = false;
        d->have_right = false;
        *left = d->dac_output[1];
        *right = d->dac_output[0];
        return true;
    }
    return false;
}
