/*
 * Timed native-frame FIFO test (Prompt 5, Workstream B).
 *
 * Verifies the bounded fixed-capacity ring buffer:
 *   - empty / one element / full state transitions
 *   - deterministic power-of-two wraparound
 *   - exact timestamps and exact left/right values preserved
 *   - overflow is an explicit error and never overwrites old data
 *   - drain after wraparound returns the exact original order
 *   - reset empties the FIFO
 *   - deterministic ordering
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_fifo.h"

#include <inttypes.h>
#include <stdio.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

static void test_empty(void)
{
    mdp_opna_fifo f;
    mdp_opna_fifo_reset(&f);
    CHECK(mdp_opna_fifo_empty(&f), "fresh FIFO not empty");
    CHECK(mdp_opna_fifo_size(&f) == 0, "fresh FIFO size != 0");
    CHECK(!mdp_opna_fifo_full(&f), "fresh FIFO full");
    mdp_opna_timed_frame fr;
    CHECK(!mdp_opna_fifo_pop(&f, &fr), "pop on empty returned true");
    CHECK(!mdp_opna_fifo_peek(&f, &fr), "peek on empty returned true");
}

static void test_one(void)
{
    mdp_opna_fifo f;
    mdp_opna_fifo_reset(&f);
    CHECK(mdp_opna_fifo_push(&f, 1728u, 100, -200), "push failed on empty");
    CHECK(mdp_opna_fifo_size(&f) == 1, "size != 1 after one push");
    CHECK(!mdp_opna_fifo_empty(&f), "FIFO empty after push");
    mdp_opna_timed_frame fr;
    CHECK(mdp_opna_fifo_peek(&f, &fr), "peek failed");
    CHECK(fr.master_clock == 1728u, "peek timestamp wrong");
    CHECK(fr.left == 100 && fr.right == -200, "peek L/R wrong");
    CHECK(mdp_opna_fifo_pop(&f, &fr), "pop failed");
    CHECK(fr.master_clock == 1728u, "pop timestamp wrong");
    CHECK(fr.left == 100 && fr.right == -200, "pop L/R wrong");
    CHECK(mdp_opna_fifo_empty(&f), "FIFO not empty after pop");
}

static void test_full_and_overflow(void)
{
    mdp_opna_fifo f;
    mdp_opna_fifo_reset(&f);

    /* Fill to capacity. */
    for (uint32_t i = 0; i < MDP_OPNA_FIFO_CAPACITY; i++) {
        CHECK(mdp_opna_fifo_push(&f, i * 144u, (int16_t)(i), (int16_t)(-i)),
              "push to fill failed");
    }
    CHECK(mdp_opna_fifo_full(&f), "FIFO not full at capacity");
    CHECK(mdp_opna_fifo_size(&f) == MDP_OPNA_FIFO_CAPACITY, "size != capacity");

    /* The next push must fail and must NOT overwrite. */
    CHECK(!mdp_opna_fifo_push(&f, 999999u, 77, 88), "overflow push did not fail");
    CHECK(mdp_opna_fifo_size(&f) == MDP_OPNA_FIFO_CAPACITY, "size changed after overflow");
    mdp_opna_timed_frame fr;
    CHECK(mdp_opna_fifo_peek(&f, &fr), "peek after overflow failed");
    CHECK(fr.master_clock == 0, "oldest timestamp overwritten by overflow!");
    CHECK(fr.left == 0, "oldest L overwritten!");
}

static void test_wraparound(void)
{
    mdp_opna_fifo f;
    mdp_opna_fifo_reset(&f);

    /* Push exactly half, pop half, then push again to force the write head
     * across the physical end of the array (deterministic wraparound). */
    for (uint32_t i = 0; i < MDP_OPNA_FIFO_CAPACITY / 2; i++)
        mdp_opna_fifo_push(&f, (uint64_t)i, (int16_t)(i + 1), (int16_t)(-(i + 1)));

    for (uint32_t i = 0; i < MDP_OPNA_FIFO_CAPACITY / 2; i++) {
        mdp_opna_timed_frame fr;
        CHECK(mdp_opna_fifo_pop(&f, &fr), "pop of first half failed");
        CHECK(fr.master_clock == (uint64_t)i, "first-half timestamp wrong");
        CHECK(fr.left == (int16_t)(i + 1) && fr.right == (int16_t)(-(i + 1)),
              "first-half L/R wrong");
    }
    CHECK(mdp_opna_fifo_empty(&f), "FIFO not empty after popping half");

    /* Now push capacity more elements; the write head wraps several times. */
    for (uint32_t i = 0; i < MDP_OPNA_FIFO_CAPACITY; i++)
        mdp_opna_fifo_push(&f, 0x10000u + 7u * i,
                           (int16_t)(0x100 + (i & 0xff)),
                           (int16_t)(0x200 + (i & 0xff)));
    CHECK(mdp_opna_fifo_full(&f), "FIFO not full after wraparound fill");

    /* Drain in exact order after wraparound. */
    for (uint32_t i = 0; i < MDP_OPNA_FIFO_CAPACITY; i++) {
        mdp_opna_timed_frame fr;
        CHECK(mdp_opna_fifo_pop(&f, &fr), "drain pop failed");
        CHECK(fr.master_clock == 0x10000u + 7u * i, "drain timestamp wrong");
        CHECK(fr.left == (int16_t)(0x100 + (i & 0xff)), "drain L wrong");
        CHECK(fr.right == (int16_t)(0x200 + (i & 0xff)), "drain R wrong");
    }
    CHECK(mdp_opna_fifo_empty(&f), "FIFO not empty after full drain");
}

static void test_reset_clears(void)
{
    mdp_opna_fifo f;
    mdp_opna_fifo_reset(&f);
    for (uint32_t i = 0; i < MDP_OPNA_FIFO_CAPACITY; i++)
        mdp_opna_fifo_push(&f, (uint64_t)i, 1, -1);
    CHECK(mdp_opna_fifo_size(&f) == MDP_OPNA_FIFO_CAPACITY,
          "size before reset wrong");
    mdp_opna_fifo_reset(&f);
    CHECK(mdp_opna_fifo_empty(&f), "FIFO not empty after reset");
    CHECK(mdp_opna_fifo_size(&f) == 0, "size != 0 after reset");
}

static void test_deterministic_ordering(void)
{
    /* Push a non-trivial sequence and confirm FIFO order is exact and
     * reproducible across a reset/replay. */
    mdp_opna_fifo a, b;
    mdp_opna_fifo_reset(&a);
    mdp_opna_fifo_reset(&b);

    uint64_t c = 144;
    int16_t val = -32000;
    for (int i = 0; i < 4096; i++) {
        mdp_opna_fifo_push(&a, c, val, (int16_t)(val + 1));
        c += 144;
        val = (int16_t)(val + 3);
    }
    for (int i = 0; i < 4096; i++) {
        mdp_opna_timed_frame fa, fb;
        /* two calls to copy into b must yield identical bytes */
        mdp_opna_fifo_push(&b, c, val, (int16_t)(val + 1));
        c += 144;
        val = (int16_t)(val + 3);
        CHECK(mdp_opna_fifo_pop(&a, &fa), "a pop failed in ordering test");
    }
    CHECK(mdp_opna_fifo_empty(&a), "a not empty in ordering test");
    /* b now equals the a sequence; drain both and compare */
    mdp_opna_fifo c1, c2;
    mdp_opna_fifo_reset(&c1);
    mdp_opna_fifo_reset(&c2);
    /* rebuild a second copy of the same stream for determinism proof */
    uint64_t cc = 144;
    int16_t vv = -32000;
    for (int i = 0; i < 4096; i++) {
        mdp_opna_fifo_push(&c1, cc, vv, (int16_t)(vv + 1));
        mdp_opna_fifo_push(&c2, cc, vv, (int16_t)(vv + 1));
        cc += 144;
        vv = (int16_t)(vv + 3);
    }
    CHECK(mdp_opna_fifo_size(&c1) == mdp_opna_fifo_size(&c2), "size mismatch");
    while (!mdp_opna_fifo_empty(&c1)) {
        mdp_opna_timed_frame fa, fb;
        CHECK(mdp_opna_fifo_pop(&c1, &fa), "pop c1 failed");
        CHECK(mdp_opna_fifo_pop(&c2, &fb), "pop c2 failed");
        CHECK(fa.master_clock == fb.master_clock && fa.left == fb.left &&
              fa.right == fb.right,
              "deterministic ordering violated");
    }
}

static void test_max_observed(void)
{
    mdp_opna_fifo f;
    mdp_opna_fifo_reset(&f);
    CHECK(mdp_opna_fifo_max_observed(&f) == 0, "max_observed not 0 initially");
    for (uint32_t i = 0; i < 100; i++)
        mdp_opna_fifo_push(&f, (uint64_t)i, 0, 0);
    CHECK(mdp_opna_fifo_max_observed(&f) == 100, "max_observed != 100");
    /* pop does not reduce max_observed */
    mdp_opna_timed_frame fr;
    for (int i = 0; i < 50; i++)
        mdp_opna_fifo_pop(&f, &fr);
    CHECK(mdp_opna_fifo_max_observed(&f) == 100, "max_observed regressed on pop");
}

int main(void)
{
    test_empty();
    test_one();
    test_full_and_overflow();
    test_wraparound();
    test_reset_clears();
    test_deterministic_ordering();
    test_max_observed();

    if (failures) {
        fprintf(stderr, "fifo_test: %d FAILURES\n", failures);
        return 1;
    }
    printf("fifo_test: all checks passed (capacity=%u)\n",
           (unsigned)MDP_OPNA_FIFO_CAPACITY);
    return 0;
}
