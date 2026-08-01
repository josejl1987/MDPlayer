/*
 * spc_capture.cpp — PR 3 read-only S-DSP voice-state observer (see header).
 *
 * All state is read through the build-time patched accessors:
 *   Spc_Emu::apu_ref() -> Snes_Spc&   (for smp_ram(), the 64K shared RAM)
 *   Spc_Emu::dsp_ref() -> Spc_Dsp&    (for voices() and regs())
 * No function here writes to the emulator or changes execution order, which is
 * what keeps the master output bit-identical to an uninstrumented render
 * (spec §5.3).
 */
#include "spc_capture.h"

#include "upstream/blargg_endian.h"

#include <stdlib.h>

namespace
{

const int kChannelStride = 0x10; /* voice register block size in the DSP */

inline void append_event(mdp_spc_event* events, int capacity, int* written,
                         int* overflow, int64_t frame, int type, int channel,
                         int param0, int param1)
{
    if (*written < capacity)
    {
        mdp_spc_event& e = events[*written];
        e.frame = frame;
        e.type = type;
        e.channel = channel;
        e.param0 = param0;
        e.param1 = param1;
        ++*written;
    }
    else
    {
        *overflow = 1;
    }
}

} // namespace

namespace spc_capture
{

void snapshot(Spc_Dsp& dsp, const uint8_t* ram, int channel,
              mdp_spc_voice_snapshot* out)
{
    const Spc_Dsp::voice_t& v = dsp.voices()[channel];
    const uint8_t* regs = dsp.regs();
    const uint8_t* vregs = regs + channel * kChannelStride;
    const int bit = 1 << channel;

    out->channel = channel;
    out->active = (v.enabled != 0 && v.env_mode != Spc_Dsp::env_release && v.env > 0)
                      ? 1 : 0;
    out->volume_l = vregs[Spc_Dsp::v_voll];
    out->volume_r = vregs[Spc_Dsp::v_volr];
    out->pitch = (int)(get_le16(vregs + Spc_Dsp::v_pitchl) & 0x3FFF);
    /* PR 8: default effective pitch to the register pitch; the per-block tap
     * result (snapshot_all_pitched) overwrites this when available. */
    out->effective_pitch = out->pitch;
    out->source_number = vregs[Spc_Dsp::v_srcn];
    out->noise_enabled = (regs[Spc_Dsp::r_non] & bit) ? 1 : 0;
    out->envelope_mode = (int)v.env_mode;
    out->envelope_level = v.env;
    out->brr_address = v.brr_addr;
    out->kon_delay = v.kon_delay;
    out->pitch_mod_enabled = (regs[Spc_Dsp::r_pmon] & bit) ? 1 : 0;
    out->echo_send_enabled = (regs[Spc_Dsp::r_eon] & bit) ? 1 : 0;

    /* Sample offset within the source: bytes decoded since the DIR start
     * address, converted to samples (16 samples per 9-byte BRR block, 2 per
     * data byte) plus the current 4-sample Gaussian group. Approximation; the
     * ABI documents sample_position as approximate. */
    out->sample_position = 0;
    if (ram != NULL && v.brr_addr >= 0)
    {
        const uint16_t dir_addr =
            (uint16_t)((unsigned)regs[Spc_Dsp::r_dir] * 0x100 +
                       (unsigned)out->source_number * 4);
        const uint16_t start = get_le16(ram + dir_addr); /* bytes 0-1 = sample start */
        const long bytes_into = (long)v.brr_addr + v.brr_offset - 1 - (long)start;
        if (bytes_into > 0)
        {
            long pos = (bytes_into / 9) * 16 + (long)(v.brr_offset - 1) * 2 +
                       (long)((v.interp_pos >> 12) - 4);
            if (pos > 0)
                out->sample_position = pos;
        }
    }
}

void snapshot_all(Spc_Emu& emu, mdp_spc_voice_snapshot out[MDP_SPC_VOICE_COUNT])
{
    const uint8_t* ram = emu.apu_ref().smp_ram();
    Spc_Dsp& dsp = emu.dsp_ref();
    for (int i = 0; i < MDP_SPC_VOICE_COUNT; i++)
        snapshot(dsp, ram, i, &out[i]);
}

void snapshot_all_pitched(Spc_Emu& emu, const int* effective_pitch,
                          mdp_spc_voice_snapshot out[MDP_SPC_VOICE_COUNT])
{
    snapshot_all(emu, out);
    if (effective_pitch)
        for (int i = 0; i < MDP_SPC_VOICE_COUNT; i++)
            out[i].effective_pitch = effective_pitch[i];
}

int active_mask(const mdp_spc_voice_snapshot* voices)
{
    int mask = 0;
    for (int i = 0; i < MDP_SPC_VOICE_COUNT; i++)
        if (voices[i].active)
            mask |= 1 << i;
    return mask;
}

int emit_events(const mdp_spc_voice_snapshot* prev,
                const mdp_spc_voice_snapshot* cur,
                int64_t frame,
                mdp_spc_event* events, int capacity, int* overflow)
{
    int written = 0;
    *overflow = 0;

    for (int ch = 0; ch < MDP_SPC_VOICE_COUNT; ch++)
    {
        const mdp_spc_voice_snapshot& p = prev[ch];
        const mdp_spc_voice_snapshot& c = cur[ch];

        if (!p.active && c.active)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_KEY_ON, ch, c.source_number, 0);

        if (p.envelope_mode != MDP_SPC_ENV_RELEASE &&
            c.envelope_mode == MDP_SPC_ENV_RELEASE)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_RELEASE_START, ch, c.envelope_level, 0);

        if (p.active && !c.active)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_VOICE_END, ch, c.envelope_level, 0);

        if (p.source_number != c.source_number)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_SOURCE_CHANGED, ch,
                         c.source_number, p.source_number);

        /* PR 8: PITCH_CHANGED uses the EFFECTIVE pitch (register pitch + PMON
         * adjustment, spec §9.2/§13.6): compare effective values and report the
         * effective pitch in param0, previous effective pitch in param1. At
         * most one PITCH_CHANGED is emitted per block per voice (the per-block
         * snapshot comparison below), so the managed decoder can thin the
         * point stream at the bounded §13.6 rate. */
        if (abs(c.effective_pitch - p.effective_pitch) > 1)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_PITCH_CHANGED, ch,
                         c.effective_pitch, p.effective_pitch);

        if (p.volume_l != c.volume_l || p.volume_r != c.volume_r)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_VOLUME_CHANGED, ch,
                         c.volume_l, c.volume_r);

        if (p.noise_enabled != c.noise_enabled)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_NOISE_CHANGED, ch, c.noise_enabled, 0);

        if (p.pitch_mod_enabled != c.pitch_mod_enabled)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_PITCH_MOD_CHANGED, ch,
                         c.pitch_mod_enabled, 0);

        if (p.echo_send_enabled != c.echo_send_enabled)
            append_event(events, capacity, &written, overflow, frame,
                         MDP_SPC_EVENT_ECHO_SEND_CHANGED, ch,
                         c.echo_send_enabled, 0);
    }

    return written;
}

void fill_abi(const mdp_spc_voice_snapshot* in, mdp_spc_voice_state* out)
{
    out->channel = in->channel;
    out->active = in->active;
    out->volume_l = in->volume_l;
    out->volume_r = in->volume_r;
    out->pitch = in->pitch;
    out->source_number = in->source_number;
    out->sample_position = in->sample_position;
    out->noise_enabled = in->noise_enabled;
    out->envelope_mode = in->envelope_mode;
    out->envelope_level = in->envelope_level;
    out->brr_address = in->brr_address;
    out->kon_delay = in->kon_delay;
    out->pitch_mod_enabled = in->pitch_mod_enabled;
    out->echo_send_enabled = in->echo_send_enabled;
    out->effective_pitch = in->effective_pitch;
}

} // namespace spc_capture
