/*
 * spc_capture.h — PR 3 read-only S-DSP voice-state observer.
 *
 * Reads the 8 voices' effective state from a Spc_Emu through the build-time
 * patched upstream accessors (see patches/0001-voice-capture.patch and
 * UPSTREAM.md "PR 3 local modifications"). The event callback runs inside
 * Spc_Dsp::run(), but writes only non-emulation capture fields and the caller's
 * event buffer. Snapshot reads still happen after a render block, so the
 * master output is bit-identical to an uninstrumented render (spec §5.3).
 */
#ifndef MDPLAYER_SPC_CAPTURE_H
#define MDPLAYER_SPC_CAPTURE_H

#include "mdplayer_spc.h"

#include "Spc_Emu.h"
#include "Snes_Spc.h"
#include "Spc_Dsp.h"

namespace spc_capture
{

/* Internal snapshot of one voice's effective state (a superset of the public
 * ABI mdp_spc_voice_state, filled straight from the S-DSP via the accessors).
 * All fields are plain values of the emulation state — no derived timing. */
struct mdp_spc_voice_snapshot
{
    int     channel;
    int     active;
    int     volume_l;
    int     volume_r;
    int     pitch;
    int     source_number;
    int64_t sample_position;
    int     noise_enabled;
    int     envelope_mode;
    int     envelope_level;
    int     brr_address;
    int     kon_delay;
    int     pitch_mod_enabled;
    int     echo_send_enabled;
    /* PR 8: DSP-calculated effective pitch (register pitch + PMON adjustment,
     * spec §10.2/§13.6). Filled from the per-voice tap captured during the
     * render block (last sample of the block); falls back to the register
     * pitch when no tap result is available. */
    int     effective_pitch;
};

/* Per-render callback target used by the build-time Spc_Dsp event tap. The
 * DSP supplies an in-block sample offset; this adapter turns it into the
 * public absolute frame without observing block-boundary state. */
struct event_capture_context
{
    mdp_spc_event* events;
    int capacity;
    int written;
    int overflow;
    int64_t block_start;
};

void begin_event_capture(Spc_Dsp& dsp, event_capture_context* context);
void end_event_capture(Spc_Dsp& dsp);

/* Fills one voice's snapshot. Pure reads. */
void snapshot(Spc_Dsp& dsp, const uint8_t* ram, int channel, mdp_spc_voice_snapshot* out);

/* Fills all 8 voices. Pure reads. effective_pitch falls back to the register
 * pitch (no tap results). */
void snapshot_all(Spc_Emu& emu, mdp_spc_voice_snapshot out[MDP_SPC_VOICE_COUNT]);

/* PR 8: like snapshot_all but overwrites effective_pitch from the per-voice
 * effective-pitch tap results captured during the block (the block's LAST
 * sample per voice, spec §13.6). This is retained for the public voice-state
 * snapshot; timeline pitch transitions use the in-block event callback.
 * effective_pitch may be NULL to fall back to the register pitch. */
void snapshot_all_pitched(Spc_Emu& emu, const int* effective_pitch,
                          mdp_spc_voice_snapshot out[MDP_SPC_VOICE_COUNT]);

/* Active-voice bitmask (for result->voice_flags). */
int active_mask(const mdp_spc_voice_snapshot* voices);

/* Legacy snapshot comparator retained for unit-level compatibility. Production
 * capture uses begin_event_capture/end_event_capture instead. */
int emit_events(const mdp_spc_voice_snapshot* prev,
                const mdp_spc_voice_snapshot* cur,
                int64_t frame,
                mdp_spc_event* events, int capacity, int* overflow);

/* Copies a snapshot into the public ABI struct. */
void fill_abi(const mdp_spc_voice_snapshot* in, mdp_spc_voice_state* out);

} // namespace spc_capture

#endif /* MDPLAYER_SPC_CAPTURE_H */
