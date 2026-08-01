/*
 * spc_taps.h — PR 6 per-voice / echo tap-capture wiring.
 *
 * Arms and disarms the S-DSP capture destinations added by
 * patches/0002-voice-echo-capture.patch (applied to the build-tree upstream
 * copy only; the vendored upstream/ stays byte-identical). The hooks in the
 * DSP write post-envelope per-voice samples (spec §8.2) and the audible
 * echo-return signal (spec §8.4) straight into caller-owned memory during the
 * existing block render. They are pure writes: the emulation state is never
 * modified, so the master output is bit-identical whether or not taps are
 * armed (spec §5.3/§30.1).
 */
#ifndef MDPLAYER_SPC_TAPS_H
#define MDPLAYER_SPC_TAPS_H

#include <stdint.h>

class Spc_Emu;

namespace spc_taps
{

/* Caller-owned tap destinations for one render block.
 * voice[i] is a mono int16 buffer (one sample per DSP output pair, i.e. one
 * sample per 32 kHz frame); echo is a stereo interleaved int16 buffer (two
 * samples per pair). NULL entries are left disabled per channel. stride is
 * the sample step between consecutive voice writes (>= 1; 1 fills
 * contiguously). */
struct TapBuffers
{
    int16_t* voice[8];
    int16_t* echo;
    int stride;
};

/* Arms the S-DSP capture destinations before a render block. Pure writes to
 * caller memory — the emulation is bit-identical with or without taps. */
void enable(Spc_Emu& emu, const TapBuffers& taps);

/* Disarms all tap destinations after the block (returns to NULL/off). */
void disable(Spc_Emu& emu);

} // namespace spc_taps

#endif /* MDPLAYER_SPC_TAPS_H */
