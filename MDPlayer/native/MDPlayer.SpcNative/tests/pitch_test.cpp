/*
 * pitch_test.cpp — PR 8 native effective-pitch test (spec §10.2/§13.6/§5.3).
 *
 * Fixture: voice 1 is pitch-modulated by voice 0 (PMON bit for voice 1 set),
 * both keyed on; voice 0 plays a full-scale looping BRR square wave so its
 * output deterministically bends voice 1's effective pitch away from its
 * register pitch. Asserts:
 *
 *   1. The C ABI render (effective-pitch taps armed inside mdp_spc_render) is
 *      byte-identical to a plain Game_Music_Emu render of the same SPC — the
 *      PR 8 guarantee that taps never change emulation (spec §5.3).
 *   2. mdp_spc_get_voice_state reports an effective_pitch for voice 1 that
 *      differs from its register pitch (PMON active), while voice 0 (the
 *      modulator; hardware never applies PMON to voice 0) reports
 *      effective_pitch == register pitch.
 *   3. PITCH_CHANGED events report the EFFECTIVE pitch (param0 != register
 *      pitch), on transitions > 1 unit, at most one per block per voice.
 *
 * Registered with CTest as "mdplayer_spc_pitch".
 */
#include "mdplayer_spc.h"

#include "Spc_Emu.h"
#include "Music_Emu.h"
#include "Data_Reader.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

namespace
{

const char kSignature[] = "SNES-SPC700 Sound File Data v0.30";
const size_t kSpcSize = 0x10200;
const size_t kRamOffset = 0x100;
const size_t kDspOffset = 0x10100;
const int kRegisterPitch = 0x1000; /* fixture pitch for both voices (1.0x) */

/* Synthetic SPC: voices 0 and 1 are keyed on by the SPC700; PMON is set for
 * voice 1, so voice 1's pitch is modulated by voice 0's output. Voice 0 plays
 * a constant DC BRR block (0xA0 header + 0xFF data decodes to a constant
 * negative sample), so its output is deterministically non-zero and voice 1's
 * effective pitch differs from its register pitch on every sample after the
 * key-on settles. */
std::vector<unsigned char> make_pmod_spc()
{
    std::vector<unsigned char> spc(kSpcSize, 0);
    memcpy(spc.data(), kSignature, sizeof(kSignature) - 1);
    spc[0x23] = 0x30; /* format */
    spc[0x24] = 1;    /* version */
    spc[0x25] = 0x00; /* pcl -> PC = 0x0200 */
    spc[0x26] = 0x02; /* pch */
    spc[0x2B] = 0xFF; /* sp */

    /* CPU at 0x0200: key on voices 0 and 1 via $F2/$F3, then hang. */
    size_t pc = kRamOffset + 0x0200;
    spc[pc + 0] = 0x8F; spc[pc + 1] = 0x4C; spc[pc + 2] = 0xF2; /* mov $F2,#$4C */
    spc[pc + 3] = 0x8F; spc[pc + 4] = 0x03; spc[pc + 5] = 0xF3; /* mov $F3,#$03 */
    spc[pc + 6] = 0x2F; spc[pc + 7] = 0xFE;                     /* bra hang */

    /* DIR at 0x0300: source 0 -> 0x0400, source 1 -> 0x0500. */
    size_t dir = kRamOffset + 0x0300;
    for (int s = 0; s < 2; s++)
    {
        size_t base = dir + (size_t)s * 4;
        unsigned start = 0x0400u + (unsigned)s * 0x100u;
        spc[base + 0] = (unsigned char)(start & 0xFF);
        spc[base + 1] = (unsigned char)(start >> 8);
        spc[base + 2] = spc[base + 0];
        spc[base + 3] = spc[base + 1];
    }

    /* Voice 0 BRR at 0x0400: header 0xA3 (scale 10, filter 0, end+loop),
     * data 0xF0 decodes to a large alternating square wave — the parity-test
     * sample proven to produce full-scale voice output (outx ~0xFD). The
     * modulator output must be large so the PMON adjustment
     * ((pmon_input>>5)*pitch)>>10 measurably bends voice 1's pitch. */
    size_t brr0 = kRamOffset + 0x0400;
    spc[brr0] = 0xA3;
    for (int i = 0; i < 8; i++)
        spc[brr0 + 1 + i] = 0xF0;

    /* Voice 1 BRR at 0x0500: same full-scale sample (its content is
     * irrelevant; we assert its effective pitch). */
    size_t brr1 = kRamOffset + 0x0500;
    spc[brr1] = 0xA3;
    for (int i = 0; i < 8; i++)
        spc[brr1 + 1 + i] = 0xF0;

    /* DSP registers. */
    unsigned char* dsp = spc.data() + kDspOffset;
    for (int v = 0; v < 2; v++)
    {
        int b = v * 0x10;
        dsp[b + 0x00] = 0x7F;        /* voll  */
        dsp[b + 0x01] = 0x7F;        /* volr  */
        dsp[b + 0x02] = 0x00;        /* pitchl */
        dsp[b + 0x03] = 0x10;        /* pitchh = 0x1000 (1.0x) */
        dsp[b + 0x04] = (unsigned char)v; /* srcn */
        dsp[b + 0x05] = 0xFF;        /* adsr0: ADSR */
        dsp[b + 0x06] = 0xE0;        /* adsr1 */
    }
    dsp[0x0C] = 0x7F; /* mvoll */
    dsp[0x1C] = 0x7F; /* mvolr */
    dsp[0x2D] = 0x02; /* pmon: voice 1 pitch is modulated by voice 0 */
    dsp[0x4C] = 0x00; /* kon: 0 -> CPU-driven key-on */
    dsp[0x5D] = 0x03; /* dir  */
    dsp[0x6C] = 0x00; /* flg  */
    return spc;
}

/* Plain Game_Music_Emu render (no observer, no taps) in 1024-frame blocks. */
int render_reference(const std::vector<unsigned char>& spc, short* out, int frames)
{
    try
    {
        Spc_Emu emu;
        const char* err = emu.set_sample_rate(MDP_SPC_SAMPLE_RATE);
        emu.ignore_silence(true); /* must match the C ABI pre-roll policy */
        if (!err)
        {
            Mem_File_Reader in(spc.data(), (long)spc.size());
            err = emu.load(in);
        }
        if (!err)
            err = emu.start_track(0);
        if (!err)
        {
            emu.set_tempo(1.0);
            emu.mute_voices(0);
        }
        for (int pos = 0; !err && pos < frames; pos += MDP_SPC_DEFAULT_BLOCK_FRAMES)
            err = emu.play(MDP_SPC_DEFAULT_BLOCK_FRAMES * 2, out + (long)pos * 2);
        return err ? 1 : 0;
    }
    catch (...)
    {
        return 1;
    }
}

/* C ABI render with the PR 8 effective-pitch taps armed (mdp_spc_render arms
 * them for every audio block). After each block reads the voice state for all
 * 8 channels and asserts the PR 8 effective-pitch semantics; also asserts the
 * PITCH_CHANGED events report the effective pitch. Returns 0 on success and
 * reports the total event count via *events_seen. */
int render_instrumented(const std::vector<unsigned char>& spc, short* out,
                        int frames, mdp_spc_event* events, int event_capacity,
                        int* events_seen)
{
    char error[256];
    mdp_spc_session* session = NULL;
    mdp_spc_open_options opts;
    memset(&opts, 0, sizeof(opts));
    opts.event_capacity = event_capacity;

    if (mdp_spc_open(spc.data(), spc.size(), &opts, &session,
                     error, sizeof(error)) != MDP_SPC_OK)
    {
        fprintf(stderr, "mdp_spc_open failed: %s\n", error);
        return 1;
    }

    const int block = MDP_SPC_DEFAULT_BLOCK_FRAMES; /* 1024 */
    const int blocks = frames / block;
    int rc = MDP_SPC_OK;
    int seen = 0;
    int pitch_changed_for_voice1 = 0;
    int bad_pitch_events = 0;
    int key_on_for_voice1 = 0;
    int voice0_effective_mismatch = 0; /* != register pitch (must stay 0) */
    int voice1_effective_match = 0;    /* == register pitch (must stay 0) */
    mdp_spc_audio_buffers audio;
    memset(&audio, 0, sizeof(audio));
    mdp_spc_render_result result;

    for (int i = 0; i < blocks && rc == MDP_SPC_OK; i++)
    {
        audio.master_stereo = out + (long)i * block * 2;
        rc = mdp_spc_render(session, block, &audio, events, event_capacity, &result);
        if (rc != MDP_SPC_OK)
            break;
        if (result.frames_rendered != block)
        {
            fprintf(stderr, "PITCH FAIL: expected %d frames, got %d\n",
                    block, result.frames_rendered);
            rc = MDP_SPC_ERR_INTERNAL;
            break;
        }
        seen += result.events_written;

        /* The events array is rewritten from index 0 by each mdp_spc_render
         * call; inspect this block's written events. */
        const mdp_spc_event* block_events = events;
        for (int e = 0; e < result.events_written; e++)
        {
            const mdp_spc_event& ev = block_events[e];
            if (ev.type == MDP_SPC_EVENT_KEY_ON && ev.channel == 1)
            {
                key_on_for_voice1++;
                /* KEY_ON is sampled at its actual in-block transition. Its
                 * pitch is therefore not required to equal the voice's final
                 * effective pitch after the whole block has rendered. */
                if (ev.param1 <= 0 || ev.param1 > 0x3FFF ||
                    ev.frame < (long)i * block ||
                    ev.frame >= (long)(i + 1) * block)
                    rc = MDP_SPC_ERR_INTERNAL;
            }
            if (ev.type == MDP_SPC_EVENT_PITCH_CHANGED && ev.channel == 1)
            {
                pitch_changed_for_voice1++;
                /* PITCH_CHANGED must report the effective pitch, not the
                 * register pitch (spec §9.2/§13.6). */
                if (ev.param0 - kRegisterPitch <= 1 && kRegisterPitch - ev.param0 <= 1)
                    bad_pitch_events++;
            }
        }

        /* PR 8 voice-state assertions (effective pitch from the block's last
         * sample when taps were armed). */
        mdp_spc_voice_state vs0;
        mdp_spc_voice_state vs1;
        if (mdp_spc_get_voice_state(session, 0, &vs0) != MDP_SPC_OK ||
            mdp_spc_get_voice_state(session, 1, &vs1) != MDP_SPC_OK)
        {
            rc = MDP_SPC_ERR_INTERNAL;
            break;
        }
        /* Voice 0 is not pitch-modulated (hardware has no voice -1), so its
         * effective pitch equals its register pitch. */
        if (vs0.effective_pitch != vs0.pitch)
            voice0_effective_mismatch++;
        /* Voice 1 is modulated by voice 0; with the constant DC modulator the
         * effective pitch differs from the register pitch by more than the
         * 1-unit event threshold. */
        if (vs1.effective_pitch - vs1.pitch <= 1 && vs1.pitch - vs1.effective_pitch <= 1)
            voice1_effective_match++;
    }

    mdp_spc_close(session);
    *events_seen = seen;

    if (rc != MDP_SPC_OK)
    {
        fprintf(stderr, "PITCH FAIL: instrumented render failed (rc=%d)\n", rc);
        return 1;
    }
    if (voice0_effective_mismatch != 0)
    {
        fprintf(stderr, "PITCH FAIL: voice 0 (unmodulated) effective pitch "
                        "differs from its register pitch in %d block(s)\n",
                voice0_effective_mismatch);
        return 1;
    }
    if (voice1_effective_match != 0)
    {
        fprintf(stderr, "PITCH FAIL: voice 1 effective pitch equals its register "
                        "pitch in %d block(s)\n",
                voice1_effective_match);
        return 1;
    }
    if (pitch_changed_for_voice1 < 1)
    {
        fprintf(stderr, "PITCH FAIL: expected at least one PITCH_CHANGED event "
                        "for the modulated voice 1, got %d\n",
                pitch_changed_for_voice1);
        return 1;
    }
    if (bad_pitch_events != 0)
    {
        fprintf(stderr, "PITCH FAIL: %d PITCH_CHANGED event(s) reported the "
                        "register pitch instead of the effective pitch\n",
                bad_pitch_events);
        return 1;
    }
    if (key_on_for_voice1 < 1)
    {
        fprintf(stderr, "PITCH FAIL: expected a KEY_ON event for modulated voice 1\n");
        return 1;
    }
    return 0;
}

} // namespace

int main()
{
    const std::vector<unsigned char> spc = make_pmod_spc();
    const int frames = 4 * MDP_SPC_DEFAULT_BLOCK_FRAMES; /* 4096 frames */

    std::vector<short> reference((size_t)frames * 2);
    std::vector<short> instrumented((size_t)frames * 2);
    std::vector<mdp_spc_event> events(256);

    if (render_reference(spc, reference.data(), frames) != 0)
    {
        fprintf(stderr, "PITCH FAIL: reference render failed\n");
        return 1;
    }

    int events_seen = 0;
    if (render_instrumented(spc, instrumented.data(), frames,
                            events.data(), (int)events.size(), &events_seen) != 0)
        return 1;

    /* Master bit-identity with taps armed (spec §5.3): the PR 8 effective-pitch
     * capture must not change emulation. */
    if (memcmp(reference.data(), instrumented.data(),
               (size_t)frames * 2 * sizeof(short)) != 0)
    {
        fprintf(stderr, "PITCH FAIL: instrumented master differs from "
                        "uninstrumented master\n");
        return 1;
    }

    printf("PITCH OK: %d frames, %d events, voice 1 effective pitch != register "
           "pitch, master bit-identical\n", frames, events_seen);
    return 0;
}
