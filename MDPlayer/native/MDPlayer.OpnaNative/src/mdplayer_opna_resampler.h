/*
 * Fixed-cadence resampler for the MDPlayer YM2608-LLE session ABI.
 *
 * A thin private wrapper around the vendored standalone SpeexDSP resampler
 * (see third_party/speexdsp/UPSTREAM.md). It converts the fixed 144-clock
 * native frame rate (7,987,200/144 Hz) to the session's output rate using
 * the exact fractional ratios handed to speex_resampler_init_frac:
 *
 *   44,100 Hz  ratio 1664/1323 of source (in/out)
 *   48,000 Hz  ratio   52/45   of source (in/out)
 *   96,000 Hz  ratio   26/45   of source (in/out)
 *
 * SpeexDSP is compiled in fixed-point processing mode at
 * SPEEX_RESAMPLER_QUALITY_MAX. The ratio runs the filter timing exactly; the
 * rounded in/out skin rates (55467 / output rate) are metadata only.
 *
 * All SpeexDSP details are private to mdplayer_opna_resampler.c. This header
 * exposes only opaque create/destroy/reset/process/latency operations — no
 * SpeexDSP type or quality constant crosses into the public ABI.
 *
 * The session owns exactly one instance, created at open, reset (history
 * cleared) on chip reset, and destroyed at close. No SpeexDSP state is
 * allocated during audio draining, and drain never advances the chip clock.
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef MDP_OPNA_RESAMPLER_H
#define MDP_OPNA_RESAMPLER_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mdp_opna_resampler mdp_opna_resampler;

/* Native (input) source frames per master clock. */
#define MDP_OPNA_NATIVE_FRAME_CLOCKS 144

/*
 * Create a fixed-rate resampler for the given output rate (44100/48000/96000).
 * Returns NULL on unsupported rate or allocation failure. One instance per
 * session, created at open. No allocation occurs per audio frame thereafter.
 */
mdp_opna_resampler *mdp_opna_resampler_create(uint32_t output_rate_hz);

/* Destroy a resampler and release its fixed allocations. Accepts NULL. */
void mdp_opna_resampler_destroy(mdp_opna_resampler *r);

/*
 * Reset buffer/history/phase without allocation (speex_resampler_reset_mem).
 * Used on chip reset: clears the resampler's internal filter history so a new
 * unrelated stream is processed cleanly.
 */
void mdp_opna_resampler_reset(mdp_opna_resampler *r);

/* Output rate the resampler was created with. */
uint32_t mdp_opna_resampler_rate(const mdp_opna_resampler *r);

/*
 * Fixed filter latency introduced by the resampler, measured in input samples
 * and output samples respectively. Deterministic for a fixed
 * quality/rate/build; recorded in internal diagnostics.
 */
int mdp_opna_resampler_input_latency(const mdp_opna_resampler *r);
int mdp_opna_resampler_output_latency(const mdp_opna_resampler *r);

/*
 * Resample a bounded block of interleaved native stereo frames (`in`) into
 * `out`. Both lengths are per-channel frame counts.
 *
 * On entry *in_frames is the number of input frames supplied and *out_frames
 * is the capacity of `out` (in frames). On return *in_frames is set to the
 * number of input frames actually consumed and *out_frames to the number of
 * output frames actually produced. Unconsumed input tail is retained by the
 * resampler for the next call. `in` and `out` must not overlap.
 *
 * Returns 0 on success, non-zero on error (see RESAMPLER_ERR_*).
 */
int mdp_opna_resampler_process(mdp_opna_resampler *r,
                               const int16_t *in,
                               int *in_frames,
                               int16_t *out,
                               int *out_frames);

#ifdef __cplusplus
}
#endif

#endif /* MDP_OPNA_RESAMPLER_H */