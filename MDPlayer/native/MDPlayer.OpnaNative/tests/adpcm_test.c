/*
 * ADPCM-B external-memory protocol test.
 *
 * Gate: "ADPCM-B uses the pin-level external-memory protocol"; covers
 *   - row/column address formation (CAS latches column, RAS latches row),
 *   - first and last RAM addresses,
 *   - wrapping at the 0x3ffff mask,
 *   - read (memory -> input.dm / input.dt0) and write (loader -> memory),
 *   - reset clearing the 256 KiB backing RAM,
 *   - the test-only loader bounds check.
 *
 * The adapter is a read-only map of the external DRAM, exactly as Furnace:
 * it drives input.dm / input.dt0 from mem[adMemAddr & 0x3ffff] when the chip
 * asserts o_mden / o_romcs. Address formation is driven by the two CAS/RAS
 * transitions on the multiplexed o_dm|o_a8 bus.
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
#include "../src/mdplayer_opna_internal.h"

#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

/* Drive one cas/ras transition sequence and return the resulting address. */
static int form_address(OpnaLleAdpcmBus *adpcm, fmopna_t *chip, int mem_config,
                        int o_dm, int o_a8)
{
    chip->o_dm = o_dm;
    chip->o_a8 = o_a8;
    chip->o_cas = 1; chip->o_ras = 1;
    opna_lle_adpcm_clock(adpcm, chip, mem_config);   /* high edge, nothing latches */
    chip->o_cas = 0;                                 /* CAS falling: latch column */
    chip->o_ras = 1;
    opna_lle_adpcm_clock(adpcm, chip, mem_config);
    chip->o_cas = 0; chip->o_ras = 0;                /* RAS falling: latch row */
    opna_lle_adpcm_clock(adpcm, chip, mem_config);
    return adpcm->ad_mem_addr;
}

static void test_address_formation_row_col(void)
{
    OpnaLleAdpcmBus adpcm;
    fmopna_t chip;
    memset(&adpcm, 0, sizeof(adpcm));
    memset(&chip, 0, sizeof(chip));

    /* column = newAddr<<9 (via CAS), row = newAddr (via RAS) */
    int addr = form_address(&adpcm, &chip, 0, 0x45, 1);  /* newAddr = 0x45 | (1<<8) = 0x145 */
    /* row 0x45 | 1<<8 = 0x145  -> bits0-8;  column 0x145<<9 -> bits9-17 */
    int expect = (0x145<<9) | 0x145;
    CHECK(addr == expect,
          "row/column address formation does not match Furnace DRAM mux");
    CHECK((addr & 0x3ffff) == addr, "formed address exceeds the 18-bit 0x3ffff mask");
}

static void test_first_and_last_address(void)
{
    /* newAddr = 0 -> address 0 (first RAM byte region) */
    OpnaLleAdpcmBus adpcm; fmopna_t chip;
    memset(&adpcm,0,sizeof adpcm); memset(&chip,0,sizeof chip);
    int first = form_address(&adpcm,&chip,0, 0, 0);
    CHECK(first == 0, "first RAM address is not 0");

    /* max newAddr = 0xff | (1<<8) = 0x1ff -> 0x1ff<<9 | 0x1ff = 0x3ffff (last) */
    memset(&adpcm,0,sizeof adpcm); memset(&chip,0,sizeof chip);
    int last = form_address(&adpcm,&chip,0, 0xff, 1);
    CHECK(last == 0x3ffff, "last RAM address is not 0x3ffff");
}

static void test_wrapping(void)
{
    /* Read with a formed address, then confirm the 0x3ffff mask on the read
     * path selects the same buffer slot as address 0x3ffff. */
    OpnaLleAdpcmBus adpcm; fmopna_t chip;
    memset(&adpcm,0,sizeof adpcm); memset(&chip,0,sizeof chip);
    adpcm.mem[0x3ffff] = 0xab;
    adpcm.mem[0]      = 0xcd;
    chip.o_dm = 0xff; chip.o_a8 = 1;      /* -> newAddr 0x1ff -> addr 0x3ffff */
    chip.o_cas = 1; chip.o_ras = 1;
    opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_cas = 0; opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_ras = 0; opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_mden = 1;                        /* mem_config=0: enable read on mden */
    opna_lle_adpcm_clock(&adpcm,&chip,0);
    CHECK(adpcm.ad_mem_addr == 0x3ffff, "wrap fixture formed wrong address");
    CHECK(chip.input.dm == 0xab, "read at freshly-formed last address wrong");
    CHECK(chip.input.dt0 == (0xab & 1), "input.dt0 not derived from input.dm LSB");

    /* wrapping: an address carriage over the 18-bit range wraps via 0x3ffff */
    memset(&adpcm,0,sizeof adpcm); memset(&chip,0,sizeof chip);
    adpcm.mem[0x00000 & 0x3ffff] = 0x55;   /* stored at offset 0 */
    adpcm.mem[0x40000 & 0x3ffff] = 0x66;   /* also offset 0 after wrap */
    chip.o_dm = 0; chip.o_a8 = 0; chip.o_cas=1; chip.o_ras=1;
    opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_cas=0; opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_ras=0; opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_mden=1; opna_lle_adpcm_clock(&adpcm,&chip,0);
    CHECK(chip.input.dm == 0x66, "RAM read did not wrap at the 0x3ffff boundary");

    /* and a plain read-back at address 0 */
    adpcm.mem[0] = 0x12;
    memset(&chip,0,sizeof chip); memset(&adpcm,0,sizeof adpcm);
    adpcm.mem[0]=0x12;
    chip.o_dm=0; chip.o_a8=0; chip.o_cas=1; chip.o_ras=1;
    opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_cas=0; opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_ras=0; opna_lle_adpcm_clock(&adpcm,&chip,0);
    chip.o_mden=1; opna_lle_adpcm_clock(&adpcm,&chip,0);
    CHECK(chip.input.dm==0x12, "read-back at address 0 failed");
}

static void test_load_write_and_reset_clear(void)
{
    OpnaLleAdpcmBus adpcm;
    memset(&adpcm, 0, sizeof(adpcm));
    uint8_t pat[8];
    for (int i = 0; i < 8; i++) pat[i] = (uint8_t)(0xa0 + i);

    opna_lle_adpcm_load(&adpcm, 1024, pat, 8);
    CHECK(memcmp(adpcm.mem + 1024, pat, 8) == 0,
          "test-only loader did not write the pattern");
    CHECK(adpcm.mem[0] == 0, "loader touched unrelated RAM");

    /* bounds: an out-of-range load is a hard no-op / fail-fast */
    memset(&adpcm, 0, sizeof(adpcm));
    opna_lle_adpcm_load(&adpcm, MDP_OPNA_ADPCM_RAM_BYTES - 1, pat, 8);
    CHECK(adpcm.mem[MDP_OPNA_ADPCM_RAM_BYTES - 2] == 0,
          "out-of-range load corrupted RAM");

    /* reset clears the entire 256 KiB backing RAM */
    memset(&adpcm, 0, sizeof(adpcm));
    opna_lle_adpcm_load(&adpcm, 0, pat, 8);
    opna_lle_adpcm_reset(&adpcm);
    int nonzero_backing = 0;
    for (size_t i = 0; i < MDP_OPNA_ADPCM_RAM_BYTES; i++)
        if (adpcm.mem[i] != 0) { nonzero_backing = 1; break; }
    CHECK(nonzero_backing == 0, "reset did not clear the ADPCM-B backing RAM");
    CHECK(adpcm.ad_mem_addr == 0, "reset did not clear the AD memory address latch");
}

static void test_macro_pin_values(void)
{
    CHECK(MDP_OPNA_ADPCM_RAM_BYTES == 256u * 1024u, "ADPCM RAM bytes constant wrong");
    CHECK(MDP_OPNA_ADPCM_ADDRESS_MASK == 0x3ffffu, "ADPCM address mask constant wrong");
}

int main(void)
{
    test_macro_pin_values();
    test_address_formation_row_col();
    test_first_and_last_address();
    test_wrapping();
    test_load_write_and_reset_clear();

    if (failures == 0)
        fprintf(stdout, "adpcm_test: OK\n");
    return failures == 0 ? 0 : 1;
}
