/*
 * parity_test.cpp — PR 3 native parity test (spec §5.3).
 *
 * Renders the same synthetic SPC twice:
 *
 *   1. Reference (uninstrumented): a plain Game_Music_Emu Spc_Emu render that
 *      never touches voice state.
 *   2. Instrumented: the MDPlayer C ABI (mdp_spc_render), whose PR 3 observer
 *      reads the S-DSP voice state after every block and fills capture events;
 *      mdp_spc_get_voice_state is also called for all 8 channels after every
 *      block.
 *
 * The synthetic SPC's SPC700 program delays briefly, then writes KON for
 * voices 0 and 1 via $F2/$F3 (a real key-on transition), then hangs. Both
 * renders disable GME's initial-silence pre-roll so the CPU's KON is
 * observable during block 0 instead of being consumed inside start_track.
 *
 * The two 32,000 Hz stereo int16 masters must be byte-identical. This is the
 * CRITICAL PR 3 guarantee: the observer only READS state, it never writes to
 * the emulator or changes execution order.
 *
 * Registered with CTest as "mdplayer_spc_parity".
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

/* Minimal SPC that produces sound: the SPC700 keys on voices 0 and 1 via
 * $F2/$F3 (KON) at frame 0, then hangs. A looping BRR square wave lives at
 * 64K RAM; the DSP register block provides per-voice volumes, ADSR and the
 * sample directory (DIR), but KON starts at 0 so the key-on is a real
 * mid-playback transition captured as KEY_ON events. */
std::vector<unsigned char> make_synthetic_spc()
{
    std::vector<unsigned char> spc(kSpcSize, 0);
    memcpy(spc.data(), kSignature, sizeof(kSignature) - 1);
    spc[0x23] = 0x30; /* format */
    spc[0x24] = 1;    /* version */
    spc[0x25] = 0x00; /* pcl -> PC = 0x0200 */
    spc[0x26] = 0x02; /* pch */
    spc[0x2B] = 0xFF; /* sp */

    /* CPU at 0x0200: key on voices 0 and 1 via $F2/$F3, then hang.
     *   mov $F2,#$4C   8F 4C F2  (DSP addr = KON)
     *   mov $F3,#$03   8F 03 F3  (KON = voices 0,1)
     * hang: bra hang   2F FE
     * GME's initial-silence pre-roll is disabled (see open), so start_track
     * does not run ahead of capture; the CPU's KON lands at frame 0 of block
     * 0 and is observed as a real KEY_ON transition from the inactive
     * baseline. */
    size_t pc = kRamOffset + 0x0200;
    spc[pc + 0] = 0x8F; spc[pc + 1] = 0x4C; spc[pc + 2] = 0xF2; /* mov $F2,#$4C */
    spc[pc + 3] = 0x8F; spc[pc + 4] = 0x03; spc[pc + 5] = 0xF3; /* mov $F3,#$03 */
    spc[pc + 6] = 0x2F; spc[pc + 7] = 0xFE;                     /* bra hang */

    /* DIR entry for source 0 at 0x0300: start = loop = 0x0400. */
    size_t dir = kRamOffset + 0x0300;
    spc[dir + 0] = 0x00;
    spc[dir + 1] = 0x04;
    spc[dir + 2] = 0x00;
    spc[dir + 3] = 0x04;

    /* BRR block at 0x0400: header 0xA3 (scale 10, filter 0, end+loop),
     * data 0xF0 -> alternating +max/0 samples (16 kHz square at 32 kHz). */
    size_t brr = kRamOffset + 0x0400;
    spc[brr] = 0xA3;
    for (int i = 0; i < 8; i++)
        spc[brr + 1 + i] = 0xF0;

    /* DSP registers: linear layout (GME reads the file block directly).
     * KON stays 0 so the key-on is CPU-driven. */
    unsigned char* dsp = spc.data() + kDspOffset;
    for (int v = 0; v < 2; v++)
    {
        int b = v * 0x10;
        dsp[b + 0x00] = 0x7F; /* voll  */
        dsp[b + 0x01] = 0x7F; /* volr  */
        dsp[b + 0x02] = 0x00; /* pitchl */
        dsp[b + 0x03] = 0x10; /* pitchh = 0x1000 (1.0x) */
        dsp[b + 0x04] = 0x00; /* srcn  */
        dsp[b + 0x05] = 0xFF; /* adsr0: ADSR */
        dsp[b + 0x06] = 0xE0; /* adsr1 */
    }
    dsp[0x0C] = 0x7F; /* mvoll */
    dsp[0x1C] = 0x7F; /* mvolr */
    dsp[0x4C] = 0x00; /* kon: 0 -> CPU-driven key-on */
    dsp[0x5D] = 0x03; /* dir  */
    dsp[0x6C] = 0x00; /* flg  */
    return spc;
}

/* Same setup sequence the C ABI uses (set_sample_rate -> ignore_silence ->
 * load -> start_track -> set_tempo -> mute_voices -> play), without any
 * voice-state observer. The reference renders in the same 1024-frame blocks
 * as the C ABI (GME's play() fills an internal buffer, so a single large
 * play() call differs from block-wise calls; comparing like-for-like keeps
 * the parity comparison exact). */
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

/* C ABI render with the PR 3 observer active: events are captured per block
 * and all 8 voice states are read after each block. Returns 0 on success and
 * reports the total number of capture events written via *events_seen. */
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
    int key_on_seen = 0;
    bool key_on_inside_block = false;
    mdp_spc_audio_buffers audio;
    memset(&audio, 0, sizeof(audio));
    audio.master_stereo = out;
    mdp_spc_render_result result;
    const short sentinel = (short)0x5A5A;
    mdp_spc_render_result invalid_result;
    if (mdp_spc_render(session, MDP_SPC_DEFAULT_BLOCK_FRAMES + 1,
                      &audio, events, event_capacity, &invalid_result)
        != MDP_SPC_ERR_INVALID_ARGUMENT)
    {
        fprintf(stderr, "PARITY FAIL: odd frame request was not rejected\n");
        mdp_spc_close(session);
        return 1;
    }

    for (int i = 0; i < blocks && rc == MDP_SPC_OK; i++)
    {
        audio.master_stereo = out + (long)i * block * 2;
        rc = mdp_spc_render(session, block, &audio, events, event_capacity, &result);
        if (rc != MDP_SPC_OK)
            break;
        if (result.frames_rendered != block)
        {
            fprintf(stderr, "PARITY FAIL: expected %d frames, got %d\n",
                    block, result.frames_rendered);
            rc = MDP_SPC_ERR_INTERNAL;
            break;
        }
        for (int sample = 0; sample < result.frames_rendered * 2; sample++)
        {
            if (audio.master_stereo[sample] == sentinel)
            {
                fprintf(stderr, "PARITY FAIL: sentinel remains in rendered "
                                "master block %d at sample %d\n", i, sample);
                rc = MDP_SPC_ERR_INTERNAL;
                break;
            }
        }
        seen += result.events_written;
        for (int e = 0; e < result.events_written; e++)
        {
            const mdp_spc_event& event = events[e];
            if (event.type != MDP_SPC_EVENT_KEY_ON)
                continue;
            key_on_seen++;
            if (event.frame < (int64_t)i * block ||
                event.frame >= (int64_t)(i + 1) * block)
            {
                fprintf(stderr, "PARITY FAIL: KEY_ON frame %lld escaped block %d\n",
                        (long long)event.frame, i);
                rc = MDP_SPC_ERR_INTERNAL;
                break;
            }
            if (event.frame != (int64_t)i * block)
                key_on_inside_block = true;
        }

        /* Exercise the PR 3 voice observer for all 8 channels after every block. */
        mdp_spc_voice_state vs;
        for (int c = 0; c < MDP_SPC_VOICE_COUNT; c++)
        {
            if (mdp_spc_get_voice_state(session, c, &vs) != MDP_SPC_OK)
            {
                rc = MDP_SPC_ERR_INTERNAL;
                break;
            }
        }
    }

    if (rc == MDP_SPC_OK && (key_on_seen < 2 || !key_on_inside_block))
    {
        fprintf(stderr, "PARITY FAIL: expected in-block KEY_ON offsets, got %d\n",
                key_on_seen);
        rc = MDP_SPC_ERR_INTERNAL;
    }

    mdp_spc_close(session);
    *events_seen = seen;
    return rc == MDP_SPC_OK ? 0 : 1;
}

} // namespace

int main()
{
    const std::vector<unsigned char> spc = make_synthetic_spc();
    const int frames = 4 * MDP_SPC_DEFAULT_BLOCK_FRAMES; /* 4096 frames */

    std::vector<short> reference((size_t)frames * 2);
    std::vector<short> instrumented((size_t)frames * 2, (short)0x5A5A);
    std::vector<mdp_spc_event> events(256);

    if (render_reference(spc, reference.data(), frames) != 0)
    {
        fprintf(stderr, "PARITY FAIL: reference render failed\n");
        return 1;
    }

    /* Regression guard: a non-NULL events buffer with per-call capacity 0
     * must produce zero event writes. A pinned 0-length managed array still
     * yields a valid pointer, so substituting any default capacity here
     * writes past the caller's buffer (heap corruption in the .NET host). */
    {
        mdp_spc_session* guard = NULL;
        char error[256];
        mdp_spc_open_options opts;
        memset(&opts, 0, sizeof(opts));
        opts.event_capacity = 64; /* session default must NOT be used */
        if (mdp_spc_open(spc.data(), spc.size(), &opts, &guard,
                         error, sizeof(error)) != MDP_SPC_OK)
        {
            fprintf(stderr, "PARITY FAIL: guard session open failed\n");
            return 1;
        }
        short guard_audio[MDP_SPC_DEFAULT_BLOCK_FRAMES * 2];
        mdp_spc_audio_buffers audio;
        memset(&audio, 0, sizeof(audio));
        audio.master_stereo = guard_audio;
        mdp_spc_render_result result;
        mdp_spc_event dummy;
        int guard_events = 0;
        for (int i = 0; i < 2; i++) /* two blocks so has_prev is set */
        {
            if (mdp_spc_render(guard, MDP_SPC_DEFAULT_BLOCK_FRAMES, &audio,
                               &dummy, 0, &result) != MDP_SPC_OK)
            {
                fprintf(stderr, "PARITY FAIL: guard render failed\n");
                mdp_spc_close(guard);
                return 1;
            }
            if (result.event_overflow != 0)
            {
                fprintf(stderr, "PARITY FAIL: capacity-0 render reported overflow\n");
                mdp_spc_close(guard);
                return 1;
            }
            guard_events += result.events_written;
        }
        mdp_spc_close(guard);
        if (guard_events != 0)
        {
            fprintf(stderr, "PARITY FAIL: capacity-0 render wrote %d events\n",
                    guard_events);
            return 1;
        }
    }

    int events_seen = 0;
    if (render_instrumented(spc, instrumented.data(), frames,
                            events.data(), (int)events.size(), &events_seen) != 0)
    {
        fprintf(stderr, "PARITY FAIL: instrumented render failed\n");
        return 1;
    }

    if (events_seen < 2)
    {
        fprintf(stderr, "PARITY FAIL: expected key-on events for 2 voices, got %d\n",
                events_seen);
        return 1;
    }
    if (memcmp(reference.data(), instrumented.data(),
               (size_t)frames * 2 * sizeof(short)) != 0)
    {
        fprintf(stderr, "PARITY FAIL: instrumented master differs from "
                        "uninstrumented master\n");
        return 1;
    }

    /* Regression guard for the stereo-count contract: Music_Emu::play takes
     * interleaved int16 samples, while mdp_spc_render takes stereo frames.
     * Each half of every ABI block must therefore contain rendered audio; a
     * mistaken frames-vs-samples call leaves the second half untouched. */
    for (int block = 0; block < frames; block += MDP_SPC_DEFAULT_BLOCK_FRAMES)
    {
        int non_silent = 0;
        int start = block + MDP_SPC_DEFAULT_BLOCK_FRAMES / 2;
        int end = block + MDP_SPC_DEFAULT_BLOCK_FRAMES;
        for (int frame = start; frame < end; frame++)
        {
            if (reference[(size_t)frame * 2] != 0
                || reference[(size_t)frame * 2 + 1] != 0)
                non_silent++;
        }
        if (non_silent < MDP_SPC_DEFAULT_BLOCK_FRAMES / 4)
        {
            fprintf(stderr, "PARITY FAIL: second half of block %d is unexpectedly silent\n",
                    block / MDP_SPC_DEFAULT_BLOCK_FRAMES);
            return 1;
        }
    }

    printf("PARITY OK: %d frames rendered, %d capture events, master "
           "bit-identical\n", frames, events_seen);
    return 0;
}
