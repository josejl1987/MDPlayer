/*
 * YM2608-LLE reset sequencing.
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp (DivPlatformYM2608::reset)
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_internal.h"

void opna_lle_serial_reset(OpnaLleSerialDecoder *decoder)
{
    decoder->dac_value = 0;
    decoder->dac_output[0] = 0;
    decoder->dac_output[1] = 0;
    decoder->last_s = false;
    decoder->last_sh1 = false;
    decoder->last_sh2 = false;
    decoder->have_left = false;
    decoder->have_right = false;
}

void opna_lle_adpcm_reset(OpnaLleAdpcmBus *adpcm)
{
    /* Prompt-4 gate: "clear RAM on reset". This is the authoritative ADPCM
     * reset and clears the 256 KiB backing buffer plus the latch state. */
    memset(adpcm->mem, 0, sizeof(adpcm->mem));
    adpcm->ad_mem_addr = 0;
    adpcm->cas = 0;
    adpcm->ras = 0;
}

/* Chip-resolution of reset: re-zero only the ADPCM bus latch state
 * (ad_mem_addr, cas, ras) exactly as Furnace does after its reset loop. Does
 * NOT touch the external RAM contents, which are owned by the session adapter. */
static void opna_lle_adpcm_reset_latch(OpnaLleAdpcmBus *adpcm)
{
    adpcm->ad_mem_addr = 0;
    adpcm->cas = 0;
    adpcm->ras = 0;
}

/*
 * Furnace reset() clears the whole fmopna_t to zero, then applies the
 * initial pin state and runs the triple 576-pair sequence:
 *   - 576 pairs: ic asserted (pull the chip out to a defined start)
 *   - 576 pairs: ic released
 *   - 576 pairs: ic re-asserted (Furnace restores ic=1 after using it low)
 * After the clocking it re-zeroes the serial-decoder and ADPCM bus latch
 * state, exactly as Furnace does after the loop.
 */
void opna_lle_reset_core(fmopna_t *core,
                         OpnaLleSerialDecoder *decoder,
                         OpnaLleAdpcmBus *adpcm,
                         uint64_t *master_clock,
                         OpnaLleResetCounts *phase_pairs,
                         bool clear_external_adpcm_ram)
{
    memset(core, 0, sizeof(*core));

    /* Initial pin state (Furnace: input.cs=1, rd=0, wr=0, a0=0, a1=0,
     * data=0, ad=0, da=0, dm=0, test=1, dt0=0). */
    core->input.clk = 0;
    core->input.ic = 0;    /* will set below */
    core->input.cs = 1;
    core->input.wr = 0;
    core->input.rd = 0;
    core->input.a0 = 0;
    core->input.a1 = 0;
    core->input.data = 0;
    core->input.ad = 0;
    core->input.da = 0;
    core->input.dm = 0;
    core->input.test = 1;
    core->input.dt0 = 0;

    /* Phase 1: ic asserted. */
    core->input.ic = 1;
    for (size_t h = 0; h < 576; h++) {
        FMOPNA_Clock(core, 0);
        FMOPNA_Clock(core, 1);
        if (master_clock) (*master_clock)++;
    }
    if (phase_pairs) phase_pairs->phase1_pairs = 576;

    /* Phase 2: ic released. */
    core->input.ic = 0;
    for (size_t h = 0; h < 576; h++) {
        FMOPNA_Clock(core, 0);
        FMOPNA_Clock(core, 1);
        if (master_clock) (*master_clock)++;
    }
    if (phase_pairs) phase_pairs->phase2_pairs = 576;

    /* Phase 3: ic re-asserted. */
    core->input.ic = 1;
    for (size_t h = 0; h < 576; h++) {
        FMOPNA_Clock(core, 0);
        FMOPNA_Clock(core, 1);
        if (master_clock) (*master_clock)++;
    }
    if (phase_pairs) phase_pairs->phase3_pairs = 576;

    /* After the reset clocking, re-zero the serial decoder and ADPCM bus
     * latch state (Furnace does exactly this: dacVal=0; dacOut=0; lastSH...
     * cas=0; ras=0; adMemAddr=0). */
    opna_lle_serial_reset(decoder);
    if (clear_external_adpcm_ram)
        opna_lle_adpcm_reset(adpcm);       /* zero RAM + latch */
    else
        opna_lle_adpcm_reset_latch(adpcm); /* only the latch; RAM preserved */
}

void opna_lle_reset_chip_state(fmopna_t *core,
                               OpnaLleSerialDecoder *decoder,
                               OpnaLleAdpcmBus *adpcm,
                               uint64_t *master_clock,
                               OpnaLleResetCounts *phase_pairs)
{
    opna_lle_reset_core(core, decoder, adpcm, master_clock, phase_pairs,
                        false);
}

void opna_lle_clear_external_adpcm_ram(OpnaLleAdpcmBus *adpcm)
{
    opna_lle_adpcm_reset(adpcm);
}
