/*
 * Fixed-cadence resampler for the MDPlayer YM2608-LLE session ABI.
 *
 * Private wrapper around the vendored standalone SpeexDSP resampler compiled
 * in fixed-point processing mode (FIXED_POINT, OUTSIDE_SPEEX,
 * RANDOM_PREFIX=mdp_opna_speex) at SPEEX_RESAMPLER_QUALITY_MAX. See
 * third_party/speexdsp/UPSTREAM.md for the pinned release / hashes.
 *
 * The stream is interleaved int16 stereo. The resampler is one per session,
 * owned here, created at open, reset (speex_resampler_reset_mem) on chip
 * reset, and destroyed at close. No SpeexDSP state is (re)allocated during
 * audio draining; drain only drives the already-built instance.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "mdplayer_opna_resampler.h"

#include "speex_resampler.h"

#include <stdint.h>
#include <stdlib.h>

/* The wrapper owns exactly one SpeexResamplerState per OPNA session. */
struct mdp_opna_resampler {
    SpeexResamplerState *state;
    uint32_t rate;
    int input_latency;
    int output_latency;
};

/* Exact fractional (in-rate/out-rate) ratio pairs, exactly as the spec
 * passes them to speex_resampler_init_frac(2, ratio_num, ratio_den, 55467,
 * out_rate, ...). The native source rate is 7,987,200/144 = 166400/3 Hz and
 * the 55467 Hz skin rate is metadata; SpeexDSP treats ratio_num/ratio_den as
 * the input advance per output and reduces it internally, so
 * ratio_num/ratio_den = in_rate/out_rate:
 *
 *   44100 Hz:  1664/1323  (output = 55467 * 1323/1664)
 *   48000 Hz:    52/45
 *   96000 Hz:    26/45
 */
static int ratio_for_rate(uint32_t out_rate, uint32_t *num, uint32_t *den)
{
    switch (out_rate) {
        case 44100u: *num = 1664u; *den = 1323u; return 0;
        case 48000u: *num =   52u; *den =   45u; return 0;
        case 96000u: *num =   26u; *den =   45u; return 0;
        default: return -1;
    }
}

mdp_opna_resampler *mdp_opna_resampler_create(uint32_t output_rate_hz)
{
    uint32_t num, den;
    if (ratio_for_rate(output_rate_hz, &num, &den) != 0)
        return NULL;

    mdp_opna_resampler *r =
        (mdp_opna_resampler *)calloc(1, sizeof(*r));
    if (!r)
        return NULL;

    /* One SpeexResamplerState per session, stereo (channel count 2). The
     * rounded 55467 Hz input skin is metadata; the fractional ratio drives
     * the filter timing. Quality is fixed at the maximum. */
    int err = 0;
    r->state = speex_resampler_init_frac(2, num, den, 55467,
                                         output_rate_hz,
                                         SPEEX_RESAMPLER_QUALITY_MAX,
                                         &err);
    if (!r->state) {
        free(r);
        return NULL;
    }

    r->rate = output_rate_hz;
    r->input_latency = speex_resampler_get_input_latency(r->state);
    r->output_latency = speex_resampler_get_output_latency(r->state);
    return r;
}

void mdp_opna_resampler_destroy(mdp_opna_resampler *r)
{
    if (!r)
        return;
    if (r->state)
        speex_resampler_destroy(r->state);
    free(r);
}

void mdp_opna_resampler_reset(mdp_opna_resampler *r)
{
    if (!r || !r->state)
        return;

    /* Upstream SpeexDSP 1.2.1 reset_mem() clears only a contiguous
     * nb_channels*(filt_len-1) span of st->mem, but the filter history is
     * laid out per channel in mem_alloc_size blocks, so every channel after
     * the first keeps stale samples (audible bleed after a chip reset).
     * Recreating the state is the exact definition of "state cleared": same
     * ratio/config, fresh zeroed filter memory on every channel. The vendored
     * resample.c stays byte-identical to the pinned upstream file. */
    uint32_t num, den;
    if (ratio_for_rate(r->rate, &num, &den) != 0)
        return;
    int err = 0;
    SpeexResamplerState *fresh =
        speex_resampler_init_frac(2, num, den, 55467, r->rate,
                                  SPEEX_RESAMPLER_QUALITY_MAX, &err);
    if (!fresh)
        return;                 /* keep the old state on allocation failure */
    speex_resampler_destroy(r->state);
    r->state = fresh;
    r->input_latency = speex_resampler_get_input_latency(r->state);
    r->output_latency = speex_resampler_get_output_latency(r->state);
}

uint32_t mdp_opna_resampler_rate(const mdp_opna_resampler *r)
{
    return r ? r->rate : 0;
}

int mdp_opna_resampler_input_latency(const mdp_opna_resampler *r)
{
    return r ? r->input_latency : 0;
}

int mdp_opna_resampler_output_latency(const mdp_opna_resampler *r)
{
    return r ? r->output_latency : 0;
}

int mdp_opna_resampler_process(mdp_opna_resampler *r,
                               const int16_t *in,
                               int *in_frames,
                               int16_t *out,
                               int *out_frames)
{
    if (!r || !r->state || !in_frames || !out || !out_frames)
        return -1;   /* internal contract violation */

    spx_uint32_t ilen = (spx_uint32_t)*in_frames;
    spx_uint32_t olen = (spx_uint32_t)*out_frames;

    /* Interleaved int16 stereo process. in_len/out_len are per-channel frame
     * counts and are updated to the numbers actually consumed / produced. */
    int rc = speex_resampler_process_interleaved_int(
        r->state, in, &ilen, out, &olen);

    *in_frames = (int)ilen;
    *out_frames = (int)olen;
    return rc;
}