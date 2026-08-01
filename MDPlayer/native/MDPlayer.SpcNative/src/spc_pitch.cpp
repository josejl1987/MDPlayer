/*
 * spc_pitch.cpp — PR 8 effective-pitch capture taps (see header).
 *
 * begin_block/end_block use the build-time patched accessor
 * Spc_Dsp::set_effective_pitch_out() (patches/0003-effective-pitch.patch).
 * Arming a tap is a pure pointer store into the DSP's non-emulation state and
 * the DSP-side capture is a pure write to caller memory during run(); it never
 * changes emulation behaviour, so the master stays bit-identical to a no-tap
 * render (spec §5.3).
 */
#include "spc_pitch.h"

namespace spc_pitch
{

void begin_block( Spc_Emu& emu, taps_t* taps )
{
    taps->has_capture = false;
    for ( int v = 0; v < MDP_SPC_VOICE_COUNT; v++ )
    {
        taps->effective [v] = 0;
        emu.dsp_ref().set_effective_pitch_out( v, &taps->effective [v] );
    }
}

void end_block( Spc_Emu& emu, taps_t* taps )
{
    for ( int v = 0; v < MDP_SPC_VOICE_COUNT; v++ )
        emu.dsp_ref().set_effective_pitch_out( v, NULL );
    taps->has_capture = true;
}

} // namespace spc_pitch
