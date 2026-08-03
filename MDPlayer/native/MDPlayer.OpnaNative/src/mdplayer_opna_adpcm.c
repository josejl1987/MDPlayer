/*
 * YM2608-LLE ADPCM-B external memory bus adapter.
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
 * Furnace logic (verbatim, `thanks nukeykt`):
 *
 *   const int newAddr=(fm_lle.o_dm&255)|(fm_lle.o_a8<<8);
 *   if (cas && !fm_lle.o_cas) { adMemAddr&=~0x3fe00; adMemAddr|=newAddr<<9; }
 *   if (ras && !fm_lle.o_ras) { adMemAddr&=~0x1ff;   adMemAddr|=newAddr; }
 *   if (memConfig&1) {
 *     if (fm_lle.o_romcs==0) { input.dm=mem[adMemAddr&0x3ffff]; input.dt0=input.dm&1; }
 *   } else {
 *     if (fm_lle.o_mden==1)  { input.dm=mem[adMemAddr&0x3ffff]; input.dt0=input.dm&1; }
 *   }
 *   cas=o_cas; ras=o_ras;
 *
 * The 0x3ffff mask preserves the 256 KiB backing-buffer size. `mem_config`
 * selects ROM vs RAM enable semantics exactly as Furnace's `memConfig`.
 */
void opna_lle_adpcm_clock(OpnaLleAdpcmBus *adpcm, fmopna_t *chip, int mem_config)
{
    const int new_addr = (chip->o_dm & 255) | (chip->o_a8 << 8);

    if (adpcm->cas && !chip->o_cas) {
        adpcm->ad_mem_addr &= ~0x3fe00;
        adpcm->ad_mem_addr |= new_addr << 9;
    }
    if (adpcm->ras && !chip->o_ras) {
        adpcm->ad_mem_addr &= ~0x1ff;
        adpcm->ad_mem_addr |= new_addr;
    }

    if (mem_config & 1) {
        if (chip->o_romcs == 0) {
            chip->input.dm = adpcm->mem[adpcm->ad_mem_addr & 0x3ffff];
            chip->input.dt0 = chip->input.dm & 1;
        }
    } else {
        if (chip->o_mden == 1) {
            chip->input.dm = adpcm->mem[adpcm->ad_mem_addr & 0x3ffff];
            chip->input.dt0 = chip->input.dm & 1;
        }
    }

    adpcm->cas = chip->o_cas;
    adpcm->ras = chip->o_ras;
}

/* Test-only loader: copy into the 256 KiB backing DRAM. Bounds-checked so a
 * bad fixture fails loudly instead of corrupting memory. Production render never
 * calls this. */
void opna_lle_adpcm_load(OpnaLleAdpcmBus *adpcm, uint32_t offset,
                         const uint8_t *data, uint32_t len)
{
    if ((uint64_t)offset + len > MDP_OPNA_ADPCM_RAM_BYTES) {
        memset(adpcm->mem, 0, sizeof(adpcm->mem));   /* fail-fast, deterministic */
        return;
    }
    if (len > 0)
        memcpy(adpcm->mem + offset, data, len);
}
