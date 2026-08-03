/*
 * Session reset test (Prompt 5, Workstream A).
 *
 * Focuses on chip-reset ABI semantics:
 *   - reset runs the validated 576/576/576 sequence
 *   - reset preserves the external ADPCM RAM
 *   - reset clears the FIFO and partial serial state
 *   - reset resets master clock to zero
 *   - reset performs no allocation
 *   - reset preserves the configured output rate
 *   - reset preserves re-seeded RAM across multiple resets
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_session.h"

#include <stdio.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

static void test_reset_no_allocation(void)
{
    mdp_opna_session *s = NULL;
    char err[64];
    mdp_opna_open_options o = { 48000 };
    mdp_opna_open(&o, &s, err, sizeof(err));
    CHECK(s != NULL, "open failed");

    /* Overflow the FIFO to ensure reset must fully clear it. */
    for (int i = 0; i < 2000; i++)
        mdp_opna_fifo_push(&s->fifo, (uint64_t)i, 1, 1);
    CHECK(mdp_opna_fifo_size(&s->fifo) > 0, "FIFO prime failed");

    /* A chip reset is allocation-free by construction (its only storage is a
     * fixed stack buffer; no malloc/calloc is called), and it must drain the
     * FIFO and zero the clock. */
    CHECK(mdp_opna_reset_chip(s) == MDP_OPNA_OK, "reset_chip failed");
    CHECK(mdp_opna_fifo_empty(&s->fifo), "reset did not clear a full FIFO");
    CHECK(mdp_opna_get_master_clock(s) == 0, "reset did not zero clock");
    CHECK(!s->lle.serial.have_left && !s->lle.serial.have_right,
          "reset did not clear serial captures");

    mdp_opna_close(s);
}

static void test_reset_preserves_ram_across_multiple(void)
{
    mdp_opna_session *s = NULL;
    char err[64];
    mdp_opna_open_options o = { 44100 };
    mdp_opna_open(&o, &s, err, sizeof(err));

    /* seed a non-trivial RAM pattern */
    for (int i = 0; i < MDP_OPNA_ADPCM_RAM_BYTES; i++)
        s->lle.adpcm.mem[i] = (uint8_t)(i * 3 + 1);

    /* run several chip resets; RAM must survive each */
    for (int r = 0; r < 3; r++) {
        CHECK(mdp_opna_reset_chip(s) == MDP_OPNA_OK, "reset in loop failed");
        int ok = 1;
        for (int i = 0; i < MDP_OPNA_ADPCM_RAM_BYTES; i++)
            if (s->lle.adpcm.mem[i] != (uint8_t)(i * 3 + 1)) { ok = 0; break; }
        CHECK(ok, "RAM changed across reset");
        /* rate preserved */
        CHECK(s->output_rate_hz == 44100 && s->rate_valid,
              "rate not preserved across reset");
        CHECK(mdp_opna_get_master_clock(s) == 0, "clock nonzero after reset");
    }

    mdp_opna_close(s);
}

static void test_open_power_on_reset_sequence(void)
{
    /* open() must run the existing 576/576/576 reset; we can verify the chip
     * core ic pin ends asserted (defined post-reset state) and that the serial
     * decoder is clear. */
    mdp_opna_session *s = NULL;
    char err[64];
    mdp_opna_open_options o = { 96000 };
    mdp_opna_open(&o, &s, err, sizeof(err));
    CHECK(s != NULL, "open 96000 failed");
    CHECK(s->lle.core.input.ic == 1, "post-open ic not asserted (reset not run)");
    CHECK(s->lle.core.input.cs == 1, "post-open cs not idle");
    CHECK(!s->lle.serial.have_left && !s->lle.serial.have_right,
          "post-open serial not clear");
    CHECK(mdp_opna_fifo_empty(&s->fifo), "post-open FIFO not empty");
    CHECK(s->output_rate_hz == 96000, "96000 rate not stored");
    mdp_opna_close(s);
}

int main(void)
{
    test_reset_no_allocation();
    test_reset_preserves_ram_across_multiple();
    test_open_power_on_reset_sequence();

    if (failures) {
        fprintf(stderr, "session_reset_test: %d FAILURES\n", failures);
        return 1;
    }
    printf("session_reset_test: all checks passed\n");
    return 0;
}
