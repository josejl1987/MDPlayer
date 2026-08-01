/*
 * spc_pitch.h — PR 8 effective-pitch capture taps (spec §10.2/§13.6).
 *
 * The S-DSP's effective pitch (register pitch after the pitch-modulation
 * adjustment, §10.2) is computed inside Spc_Dsp::run() and normally discarded.
 * PR 8 adds an OPTIONAL, off-by-default build-time tap (patches/0003) that
 * stores it into a caller-provided int cell for each voice on every rendered
 * sample. This module arms and disarms those taps around a render block so the
 * observer can read the block's LAST effective pitch per voice.
 *
 * Taps are pure writes to caller memory: they never touch DSP state, so the
 * master output is bit-identical whether or not they are armed (spec §5.3).
 */
#ifndef MDPLAYER_SPC_PITCH_H
#define MDPLAYER_SPC_PITCH_H

#include "mdplayer_spc.h"

#include "Spc_Emu.h"

namespace spc_pitch
{

/* Per-block effective-pitch capture results (one int cell per voice). The DSP
 * overwrites each cell on every rendered sample, so after a block the cell
 * holds the effective pitch of the block's LAST sample (§13.6: the decoder
 * wants the current value). */
struct taps_t
{
    int  effective [MDP_SPC_VOICE_COUNT];
    bool has_capture; /* true after a block was rendered with taps armed */
};

/* Arms the taps for one render block: clears the capture cells and points
 * each voice's DSP tap at its cell. Call immediately before emu->play(); every
 * play() call must be paired with end_block(). */
void begin_block( Spc_Emu& emu, taps_t* taps );

/* Disarms the taps (every DSP tap back to NULL) and marks the capture as
 * valid. Call immediately after emu->play() (also from catch paths). */
void end_block( Spc_Emu& emu, taps_t* taps );

} // namespace spc_pitch

#endif /* MDPLAYER_SPC_PITCH_H */
