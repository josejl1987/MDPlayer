/*
 * YM2608-LLE final mix (serial DAC + SSG analogue).
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
 * Furnace final mix for a completed stereo frame:
 *
 *   int accm1=(short)dacOut[1];   // left  (dacOut[1] -> outL)
 *   int accm2=(short)dacOut[0];   // right (dacOut[0] -> outR)
 *   int outL=((accm1*fmVol)>>8)+fm_lle.o_analog*ssgVol*42;
 *   int outR=((accm2*fmVol)>>8)+fm_lle.o_analog*ssgVol*42;
 *   (each clamped to int16)
 *
 * We keep the exact multiplier and volume convention so the adapter can be
 * compared directly with Furnace. opna_lle_mix_frame emits one stereo frame.
 */
void opna_lle_mix_frame(const fmopna_t *chip,
                        int16_t left_serial,
                        int16_t right_serial,
                        int fm_vol,
                        int ssg_vol,
                        int16_t *out_l,
                        int16_t *out_r)
{
    int accm1 = left_serial;   /* dacOut[1] */
    int accm2 = right_serial;  /* dacOut[0] */

    int out_l_i = ((accm1 * fm_vol) >> 8) + (int)(chip->o_analog * ssg_vol * OPNA_FURNACE_SSG_ANALOG_SCALE);
    int out_r_i = ((accm2 * fm_vol) >> 8) + (int)(chip->o_analog * ssg_vol * OPNA_FURNACE_SSG_ANALOG_SCALE);

    if (out_l_i < -32768) out_l_i = -32768;
    if (out_l_i > 32767) out_l_i = 32767;
    if (out_r_i < -32768) out_r_i = -32768;
    if (out_r_i > 32767) out_r_i = 32767;

    *out_l = (int16_t)out_l_i;
    *out_r = (int16_t)out_r_i;
}
