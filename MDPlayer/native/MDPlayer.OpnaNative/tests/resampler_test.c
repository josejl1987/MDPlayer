/*
 * Fixed-cadence resampler behavior test (Prompt 5.1, Task D — SpeexDSP).
 *
 * Black-box behavior tests of the SpeexDSP-backed resampler wrapper. For each
 * supported output rate it checks:
 *
 *   - silence remains silence;
 *   - constant positive, constant negative, and low-level (+/-1) signals keep
 *     unity DC gain and never clip;
 *   - left/right channel isolation;
 *   - an impulse is processed successfully, produces deterministic nonzero
 *     output on the driven channel, leaves the untouched channel exactly zero,
 *     and never exceeds the int16 range;
 *   - input block-size independence and output block-size independence;
 *   - partial input consumption (small output capacity consumes only a prefix
 *     and later input completes the stream);
 *   - reset clears history;
 *   - drain never advances chip time;
 *   - unsupported cadence is rejected.
 *
 * SpeexDSP is vetted upstream; this project only verifies correct integration,
 * channel isolation and determinism, not FIR implementation details.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_resampler.h"
#include "../include/mdplayer_opna.h"

#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "  FAIL: %s (line %d)\n", msg, __LINE__); \
        failures++; \
    } \
} while (0)

static uint32_t lcg(uint32_t *s)
{
    *s = *s * 1664525u + 1013904223u;
    return *s;
}

static int16_t next_rand(uint32_t *s) { return (int16_t)(lcg(s) >> 16); }

/*
 * Generous output capacity that safely covers the highest supported
 * input->output ratio for a given input frame count. The test does not rely on
 * output-count mathematics, so a loose bound is enough.
 */
static size_t out_capacity(size_t input_frames)
{
    return input_frames * 2 + 4096;
}

/*
 * Feed `n` inputs in one block into a fresh resampler and drain in one block.
 * Returns frames written into `out` (capacity outcap). Reference path used to
 * compare against block / partial-consumption variants.
 */
static size_t run_one_block(uint32_t rate, const int16_t *lin, const int16_t *rin,
                            size_t n, int16_t *out, size_t outcap)
{
    mdp_opna_resampler *rs = mdp_opna_resampler_create(rate);
    if (!rs) return 0;
    int16_t *in = (int16_t *)malloc(n * 2 * sizeof(int16_t));
    if (!in) { mdp_opna_resampler_destroy(rs); return 0; }
    for (size_t i = 0; i < n; i++) { in[2*i] = lin[i]; in[2*i+1] = rin[i]; }

    int in_frames = (int)n;
    int out_frames = (int)outcap;
    int rc = mdp_opna_resampler_process(rs, in, &in_frames, out, &out_frames);
    size_t written = rc ? 0 : (size_t)out_frames;
    free(in);
    mdp_opna_resampler_destroy(rs);
    return written;
}

/*
 * Feed the whole input in `feed_block`-sized chunks, with at most `drain_cap`
 * output frames per call, accumulating into `out` (capacity outcap). Uses
 * SpeexDSP's prefix-consumption semantics: after each process call, *in_len
 * holds the frames CONSUMED and *out_len the frames PRODUCED, and the input
 * cursor advances by exactly the consumed amount (a small drain_cap therefore
 * forces a call to consume only a prefix, with the rest re-fed next call).
 * This exercises input block-size independence (chunked feeding), output
 * block-size independence (chunked draining), and partial consumption. The
 * reference path (run_one_block) performs the same transform in one call, so
 * both must produce byte-identical output.
 */
static size_t run_blocked(uint32_t rate, const int16_t *lin, const int16_t *rin,
                          size_t n, int feed_block, int drain_cap,
                          int16_t *out, size_t outcap)
{
    mdp_opna_resampler *rs = mdp_opna_resampler_create(rate);
    if (!rs) return 0;

    int16_t *full = (int16_t *)malloc(n * 2 * sizeof(int16_t));
    int16_t *tmp = (int16_t *)malloc(out_capacity(drain_cap) * 2 * sizeof(int16_t));
    if (!full || !tmp) { free(full); free(tmp); mdp_opna_resampler_destroy(rs); return 0; }
    for (size_t i = 0; i < n; i++) { full[2*i] = lin[i]; full[2*i+1] = rin[i]; }

    if (drain_cap < 1) drain_cap = 1;
    size_t written = 0;
    size_t fed = 0;                  /* input frames consumed by the resampler */
    int guard = 0;
    while (fed < n && written < outcap) {
        if (++guard > 1000000) break;          /* safety: must terminate */
        int asked = (int)((n - fed) < (size_t)feed_block ? (n - fed) : (size_t)feed_block);
        int out_frames = drain_cap;
        int rc = mdp_opna_resampler_process(rs, full + 2 * fed, &asked, tmp, &out_frames);
        if (rc != 0) break;
        fed += (size_t)asked;                  /* *in_len == frames consumed */
        if (out_frames > 0) {
            size_t copy = (size_t)out_frames;
            if (written + copy > outcap) copy = outcap - written;
            memcpy(out + 2 * written, tmp, copy * 2 * sizeof(int16_t));
            written += copy;
        }
        if (asked == 0 && out_frames == 0) break;  /* no progress: stop */
    }

    free(full); free(tmp);
    mdp_opna_resampler_destroy(rs);
    return written;
}

static void test_constant(uint32_t rate, int16_t val)
{
    const size_t n = 16000;
    int16_t *lin = (int16_t *)malloc(n * sizeof(int16_t));
    int16_t *rin = (int16_t *)malloc(n * sizeof(int16_t));
    size_t cap = out_capacity(n);
    int16_t *out = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    if (!lin || !rin || !out) { failures++; goto cleanup; }
    for (size_t i = 0; i < n; i++) { lin[i] = val; rin[i] = val; }

    size_t ow = run_one_block(rate, lin, rin, n, out, cap);

    /* Average output after startup latency + warm-up. Skipping a few hundred
     * output samples passes the input-latency warm-up for every ratio. Unity
     * DC gain: the average stays within +-0.0625% of the constant (the same
     * relative band that puts 16000 within 15990..16010, applied to all
     * magnitudes). */
    long sum = 0; size_t cnt = 0;
    int clipped = 0;
    const size_t skip = ow > 400 ? 400 : ow;
    for (size_t i = skip; i < ow; i++) {
        sum += out[2*i];
        cnt++;
        if (out[2*i] == INT16_MIN || out[2*i] == INT16_MAX)
            clipped++;
    }
    CHECK(ow >= 1600, "constant produced too few outputs");
    CHECK(clipped == 0, "constant input clipped");
    if (cnt) {
        double avg = (double)sum / (double)cnt;
        double lo, hi;
        if (val >= 0) { lo = val * 0.999375; hi = val * 1.000625; }
        else          { lo = val * 1.000625; hi = val * 0.999375; }
        CHECK(avg >= lo && avg <= hi, "constant average outside unity-gain band");
    }
cleanup:
    free(lin); free(rin); free(out);
}

static void test_silence_remains_silence(uint32_t rate)
{
    const size_t n = 2000;
    int16_t *lin = (int16_t *)calloc(n, sizeof(int16_t));
    int16_t *rin = (int16_t *)calloc(n, sizeof(int16_t));
    size_t cap = out_capacity(n);
    int16_t *out = (int16_t *)calloc(cap * 2, sizeof(int16_t));
    if (!lin || !rin || !out) { failures++; goto cleanup; }
    size_t ow = run_one_block(rate, lin, rin, n, out, cap);
    int any = 0;
    for (size_t i = 0; i < ow; i++)
        if (out[2*i] != 0 || out[2*i+1] != 0) { any = 1; break; }
    CHECK(!any, "silence did not remain silence");
cleanup:
    free(lin); free(rin); free(out);
}

static void test_impulse(uint32_t rate)
{
    const size_t n = 4096;
    int16_t *lin = (int16_t *)calloc(n, sizeof(int16_t));
    int16_t *rin = (int16_t *)calloc(n, sizeof(int16_t));
    lin[0] = 32767;                 /* impulse on the left channel only */
    size_t cap = out_capacity(n);
    int16_t *A = (int16_t *)calloc(cap * 2, sizeof(int16_t));
    int16_t *B = (int16_t *)calloc(cap * 2, sizeof(int16_t));
    if (!lin || !rin || !A || !B) { failures++; goto cleanup; }

    size_t wa = run_one_block(rate, lin, rin, n, A, cap);
    size_t wb = run_one_block(rate, lin, rin, n, B, cap);

    /* Processed successfully (nonzero output count). */
    CHECK(wa > 0 && wb > 0, "impulse produced no output");

    /* At least one left sample is nonzero; every right sample stays zero. */
    int l_any = 0, r_leak = 0, overflow = 0;
    for (size_t i = 0; i < wa; i++) {
        if (A[2*i] != 0) l_any = 1;
        if (A[2*i+1] != 0) r_leak++;
        if (A[2*i] == INT16_MIN || A[2*i] == INT16_MAX) overflow++;
    }
    CHECK(l_any, "impulse produced no nonzero left output");
    CHECK(r_leak == 0, "impulse leaked into the right channel");
    CHECK(overflow == 0, "impulse output out of int16 range");

    /* A second fresh resampler produces byte-identical output. */
    CHECK(wa == wb, "impulse: output-count mismatch across instances");
    CHECK(memcmp(A, B, wa * 2 * sizeof(int16_t)) == 0,
          "impulse: non-deterministic output across instances");
cleanup:
    free(lin); free(rin); free(A); free(B);
}

static void test_independent_stereo(uint32_t rate)
{
    const size_t n = 3000;
    int16_t *lin = (int16_t *)malloc(n * sizeof(int16_t));
    int16_t *rin = (int16_t *)calloc(n, sizeof(int16_t));
    size_t cap = out_capacity(n);
    int16_t *out = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    if (!lin || !rin || !out) { failures++; goto cleanup; }
    for (size_t i = 0; i < n; i++) lin[i] = 30000;
    size_t ow = run_one_block(rate, lin, rin, n, out, cap);
    int l_any = 0, r_max = 0;
    for (size_t i = 0; i < ow; i++) {
        if (out[2*i] != 0) l_any = 1;
        int a = out[2*i+1]; if (a < 0) a = -a;
        if (a > r_max) r_max = a;
    }
    CHECK(l_any, "L channel produced no output");
    CHECK(r_max <= 8, "R channel leaked L into it");
cleanup:
    free(lin); free(rin); free(out);
}

/* Feed/drain block sizes must not change the output stream. */
static void test_block_independence(uint32_t rate)
{
    const size_t n = 9000;
    int16_t *lin = (int16_t *)malloc(n * sizeof(int16_t));
    int16_t *rin = (int16_t *)malloc(n * sizeof(int16_t));
    uint32_t s = 99u;
    for (size_t i = 0; i < n; i++) { lin[i] = next_rand(&s); rin[i] = next_rand(&s); }
    size_t cap = out_capacity(n);
    int16_t *A = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    int16_t *B = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    if (!lin || !rin || !A || !B) { failures++; goto cleanup; }

    size_t wa = run_one_block(rate, lin, rin, n, A, cap);

    /* Input block sizes 1, 7, 64, 257 with a matching drain block. */
    static const int blocks[] = { 1, 7, 64, 257 };
    for (size_t bi = 0; bi < sizeof(blocks)/sizeof(blocks[0]); bi++) {
        int fb = blocks[bi], db = blocks[bi];
        size_t wb = run_blocked(rate, lin, rin, n, fb, db, B, cap);
        if (wa == 0) { CHECK(0, "single-block run produced no output"); break; }
        size_t cmplen = wa < wb ? wa : wb;
        if (cmplen == 0 || memcmp(A, B, cmplen * 2 * sizeof(int16_t)) != 0) {
            CHECK(0, "block-size independence: sample mismatch");
            break;
        }
    }

    /* Output drain block sizes 1, 7, 64, 257 (single large input block). */
    for (size_t bi = 0; bi < sizeof(blocks)/sizeof(blocks[0]); bi++) {
        int db = blocks[bi];
        size_t wb = run_blocked(rate, lin, rin, n, (int)n, db, B, cap);
        size_t cmplen = wa < wb ? wa : wb;
        if (cmplen == 0 || memcmp(A, B, cmplen * 2 * sizeof(int16_t)) != 0) {
            CHECK(0, "output block-size independence: sample mismatch");
            break;
        }
    }
cleanup:
    free(lin); free(rin); free(A); free(B);
}

/* Small output capacity must consume only a prefix; feeding the remaining
 * input and concatenating matches a single large call. */
static void test_partial_consumption(uint32_t rate)
{
    const size_t n = 8192;
    int16_t *lin = (int16_t *)malloc(n * sizeof(int16_t));
    int16_t *rin = (int16_t *)malloc(n * sizeof(int16_t));
    uint32_t s = 21u;
    for (size_t i = 0; i < n; i++) { lin[i] = next_rand(&s); rin[i] = next_rand(&s); }
    size_t cap = out_capacity(n);
    int16_t *A = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    int16_t *B = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    if (!lin || !rin || !A || !B) { failures++; goto cleanup; }

    /* Reference: one large processing call. */
    size_t wref = run_one_block(rate, lin, rin, n, A, cap);

    /* Partial: small output capacity forces only a prefix to be consumed. */
    int16_t *in = (int16_t *)malloc(n * 2 * sizeof(int16_t));
    if (!in) { failures++; goto cleanup; }
    for (size_t i = 0; i < n; i++) { in[2*i] = lin[i]; in[2*i+1] = rin[i]; }
    mdp_opna_resampler *rs = mdp_opna_resampler_create(rate);
    size_t written = 0;
    if (rs) {
        int consumed = 0;          /* total input frames consumed so far */
        int guard = 0;
        while (consumed < (int)n) {
            if (++guard > 1000000) break;      /* safety: must terminate */
            int in_frames = (int)n - consumed;
            int out_frames = 37;               /* deliberately small */
            int16_t *at = in + 2 * consumed;
            int rc = mdp_opna_resampler_process(rs, at, &in_frames,
                                                B + 2 * written, &out_frames);
            if (rc != 0) break;
            /* SpeexDSP consumed a prefix of the staged block; in_frames is the
             * number remaining in this call. Advance the cursor by the
             * consumed amount, and concatenate the produced output. */
            consumed += in_frames;
            written += (size_t)out_frames;
        }
        mdp_opna_resampler_destroy(rs);
    }
    CHECK(wref > 0, "reference run produced no output");
    CHECK(written > 0, "partial run produced no output");
    size_t cmplen = wref < written ? wref : written;
    CHECK(memcmp(A, B, cmplen * 2 * sizeof(int16_t)) == 0,
          "partial consumption: concatenated output differs from one block");
cleanup:
    free(lin); free(rin); free(A); free(B); free(in);
}

static void test_reset_clears_history(uint32_t rate)
{
    const size_t n = 2000;
    int16_t *lin = (int16_t *)malloc(n * sizeof(int16_t));
    int16_t *rin = (int16_t *)malloc(n * sizeof(int16_t));
    uint32_t s = 5u;
    for (size_t i = 0; i < n; i++) { lin[i] = next_rand(&s); rin[i] = next_rand(&s); }
    size_t cap = out_capacity(n);
    int16_t *A = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    int16_t *B = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    int16_t *C = (int16_t *)malloc(cap * 2 * sizeof(int16_t));
    if (!lin || !rin || !A || !B || !C) { failures++; goto cleanup; }

    size_t wa = run_one_block(rate, lin, rin, n, A, cap);

    /* One instance processes a different stream, resets, then processes the
     * reference stream. Output must match a fresh instance. */
    int16_t junk[2000 * 2]; s = 12345u;
    for (int j = 0; j < 2000 * 2; j++) junk[j] = next_rand(&s);   /* interleaved L/R */
    mdp_opna_resampler *rs = mdp_opna_resampler_create(rate);
    size_t wb;
    if (!rs) { failures++; goto cleanup; }
    {
        int jf = 2000; int of = (int)cap;    /* 2000 stereo frames */
        (void)mdp_opna_resampler_process(rs, junk, &jf, C, &of);
        mdp_opna_resampler_reset(rs);
        int inf = (int)n; int outf = (int)cap;
        int16_t *in = (int16_t *)malloc(n * 2 * sizeof(int16_t));
        for (size_t i = 0; i < n; i++) { in[2*i] = lin[i]; in[2*i+1] = rin[i]; }
        int rc2 = mdp_opna_resampler_process(rs, in, &inf, B, &outf);
        wb = rc2 ? 0 : (size_t)outf;
        free(in);
    }
    mdp_opna_resampler_destroy(rs);
    CHECK(wa == wb, "reset: output-count mismatch after reset");
    CHECK(memcmp(A, B, wa * 2 * sizeof(int16_t)) == 0,
          "reset: history not cleared (output differs from fresh)");
cleanup:
    free(lin); free(rin); free(A); free(B); free(C);
}

/* Session-level gate: draining audio never advances the OPNA master clock. */
static void test_drain_does_not_advance_time(void)
{
    mdp_opna_open_options opt = { 48000 };
    mdp_opna_session *s = NULL;
    char err[64];
    CHECK(mdp_opna_open(&opt, &s, err, sizeof(err)) == MDP_OPNA_OK,
          "open failed for drain-time test");
    if (!s) return;
    int rc = mdp_opna_advance_to(s, 1000);
    CHECK(rc == MDP_OPNA_OK, "advance failed");
    uint64_t before = mdp_opna_get_master_clock(s);
    int16_t buf[128];
    int drained = -1;
    rc = mdp_opna_drain_audio(s, buf, 32, &drained);
    CHECK(rc == MDP_OPNA_OK, "drain failed");
    CHECK(drained >= 0 && drained <= 32, "drain returned out-of-range count");
    CHECK(mdp_opna_get_master_clock(s) == before, "drain advanced master clock");
    mdp_opna_close(s);
}

/* Session-level gate: a cadence-changing prescaler write must reject. With
 * QW1 the write path rejects it up front; otherwise it is honored through
 * the bus and the cadence guard latches the error on advance/drain. */
static void test_unsupported_cadence_rejected(void)
{
    mdp_opna_open_options opt = { 48000 };
    mdp_opna_session *s = NULL;
    char err[64];
    CHECK(mdp_opna_open(&opt, &s, err, sizeof(err)) == MDP_OPNA_OK,
          "open failed for cadence test");
    if (!s) return;
    int rc = mdp_opna_write_register(s, 0, 0, 0x2e, 1);
#ifdef MDPLAYER_OPNA_QW1_FIXED_PRESCALER
    CHECK(rc == MDP_OPNA_ERR_UNSUPPORTED_CADENCE,
          "prescaler write not rejected at clock 0");
    rc = mdp_opna_advance_to(s, 4000);
    CHECK(rc == MDP_OPNA_ERR_UNSUPPORTED_CADENCE,
          "sticky cadence error not surfaced on advance");
#else
    CHECK(rc == MDP_OPNA_OK, "prescaler write rejected at clock 0");
    rc = mdp_opna_advance_to(s, 4000);
    if (rc == MDP_OPNA_OK) {
        int16_t buf[64];
        int drained = -1;
        rc = mdp_opna_drain_audio(s, buf, 32, &drained);
    }
    CHECK(rc == MDP_OPNA_ERR_UNSUPPORTED_CADENCE,
          "unsupported cadence was not rejected");
#endif
    mdp_opna_close(s);
}

static void test_rates(void)
{
    mdp_opna_resampler *r441 = mdp_opna_resampler_create(44100u);
    mdp_opna_resampler *r480 = mdp_opna_resampler_create(48000u);
    mdp_opna_resampler *r960 = mdp_opna_resampler_create(96000u);
    CHECK(r441 && r480 && r960, "create failed for supported rate");
    CHECK(mdp_opna_resampler_rate(r441) == 44100u, "rate 44100 wrong");
    CHECK(mdp_opna_resampler_rate(r480) == 48000u, "rate 48000 wrong");
    CHECK(mdp_opna_resampler_rate(r960) == 96000u, "rate 96000 wrong");
    CHECK(mdp_opna_resampler_input_latency(r441) > 0, "44100 input latency");
    CHECK(mdp_opna_resampler_output_latency(r441) >= 0, "44100 output latency");
    CHECK(mdp_opna_resampler_create(22050u) == NULL, "unsupported rate accepted");
    mdp_opna_resampler_destroy(r441);
    mdp_opna_resampler_destroy(r480);
    mdp_opna_resampler_destroy(r960);
    mdp_opna_resampler_destroy(NULL);
}

int main(void)
{
    static const uint32_t rates[] = { 44100u, 48000u, 96000u };
    test_rates();
    for (size_t i = 0; i < 3; i++) {
        uint32_t r = rates[i];
        printf("rate %u:\n", r);
        test_constant(r, 16000);      /* constant positive */
        test_constant(r, -16000);     /* constant negative */
        test_constant(r, 1);          /* low-level positive */
        test_constant(r, -1);         /* low-level negative */
        test_silence_remains_silence(r);
        test_impulse(r);
        test_independent_stereo(r);
        test_block_independence(r);
        test_partial_consumption(r);
        test_reset_clears_history(r);
    }
    test_drain_does_not_advance_time();
    test_unsupported_cadence_rejected();

    if (failures) {
        fprintf(stderr, "resampler_test: %d FAILURE(s)\n", failures);
        return 1;
    }
    printf("resampler_test: all resampler behavior checks passed\n");
    return 0;
}