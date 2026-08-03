/*
 * Upstream compile test for the vendored Furnace YM2608-LLE core.
 *
 * Proves that the pinned core (upstream/furnace-ym2608-lle/fmopna_2608.c /
 * fmopna_2608.h -> fmopna_impl.c / fmopna_impl.h) compiles and can be
 * initialized and clocked. This test does NOT produce audio; it only drives
 * the core's public reset/clock surface to confirm it links and runs a full
 * clock phase set without error.
 *
 * The vendored core is GPL-2.0-or-later (nukeykt). See UPSTREAM.md and the
 * LICENSES/ directory for the full licence text.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "fmopna_2608.h"

#include <stdio.h>
#include <string.h>

int main(void)
{
    /* The core is reset by zero-initialising the fmopna_t state (Furnace does
     * `memset(&fm_lle, 0, sizeof(fmopna_t))` before first clock). */
    fmopna_t chip;
    memset(&chip, 0, sizeof(chip));

    /* Drive a few full clock pairs (h=0, h=1) to exercise opcode phase 0 and
     * the prescaler. A handful of pairs is enough to prove init + clocking;
     * no audio output is produced here. */
    for (int i = 0; i < 8; i++) {
        FMOPNA_Clock(&chip, 0);
        FMOPNA_Clock(&chip, 1);
    }

    (void)chip; /* silence any unused-parameter warnings */

    puts("upstream compile test: OK");
    return 0;
}
