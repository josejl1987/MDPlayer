/*
 * spc_taps.cpp — PR 6 tap-capture wiring implementation.
 *
 * Drives the build-time-patched Spc_Dsp::set_voice_taps / set_echo_tap /
 * clear_taps (see patches/0002-voice-echo-capture.patch). The tap
 * destinations are only valid while a render block is in flight: the managed
 * caller owns the buffers and the DSP writes to them in lockstep with the
 * master render. No allocation, no per-sample callbacks (§9.1).
 */
#include "spc_taps.h"

#include "Spc_Emu.h"

namespace spc_taps
{

void enable(Spc_Emu& emu, const TapBuffers& taps)
{
    int16_t* voice[8];
    for (int i = 0; i < 8; i++)
        voice[i] = taps.voice ? taps.voice[i] : NULL;
    emu.dsp_ref().set_voice_taps(voice, taps.stride);
    emu.dsp_ref().set_echo_tap(taps.echo);
}

void disable(Spc_Emu& emu)
{
    emu.dsp_ref().clear_taps();
}

} // namespace spc_taps
