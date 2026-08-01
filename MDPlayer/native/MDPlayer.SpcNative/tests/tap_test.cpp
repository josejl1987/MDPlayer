/*
 * tap_test.cpp — PR 6 native voice/echo tap-capture test (spec §5.3, §8.2, §8.4).
 *
 * Renders the same synthetic SPC twice through the C ABI:
 *
 *   1. Baseline: mdp_spc_open with voice/echo PCM disabled (taps off).
 *   2. Taps:     mdp_spc_open with enable_voice_pcm = enable_echo_pcm = 1,
 *                passing 8 mono per-voice buffers and a stereo echo buffer.
 *
 * The synthetic SPC's SPC700 program keys on voices 0 and 1 via $F2/$F3 (a
 * CPU-driven KON) at frame 0 and then hangs; the voices loop a BRR square
 * wave. EON is 0 and the echo volumes are 0, so:
 *   - voice taps 0 and 1 must be non-silent,
 *   - voice taps 2..7 must stay all-zero (never keyed),
 *   - the echo tap must stay all-zero (no echo send, evoll/evolr = 0).
 *
 * The CRITICAL PR 6 guarantee: the master rendered with taps armed must be
 * byte-identical to the master rendered without taps. Tap capture is a pure
 * write to caller memory; it never modifies the DSP computation.
 *
 * Registered with CTest as "mdplayer_spc_tap".
 */
#include "mdplayer_spc.h"

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
 * sample directory (DIR). KON starts at 0 so the key-on is a real mid-playback
 * transition. EON stays 0 and the echo volumes stay 0 so the echo return is
 * silent (spec §8.4 test fixture). */
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
     * hang: bra hang   2F FE */
    size_t pc = kRamOffset + 0x0200;
    spc[pc + 0] = 0x8F; spc[pc + 1] = 0x4C; spc[pc + 2] = 0xF2;
    spc[pc + 3] = 0x8F; spc[pc + 4] = 0x03; spc[pc + 5] = 0xF3;
    spc[pc + 6] = 0x2F; spc[pc + 7] = 0xFE;

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

    /* DSP registers: linear layout (GME reads the file block directly). */
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
    dsp[0x2C] = 0x00; /* evoll: 0 -> echo return silent */
    dsp[0x3C] = 0x00; /* evolr: 0 -> echo return silent */
    dsp[0x4C] = 0x00; /* kon: 0 -> CPU-driven key-on */
    dsp[0x4D] = 0x00; /* eon: 0 -> no voice feeds the echo buffer */
    dsp[0x5D] = 0x03; /* dir  */
    dsp[0x6C] = 0x00; /* flg  */
    return spc;
}

} // namespace

int main()
{
    const std::vector<unsigned char> spc = make_synthetic_spc();
    const int frames = 4 * MDP_SPC_DEFAULT_BLOCK_FRAMES; /* 4096 frames */
    const int block = MDP_SPC_DEFAULT_BLOCK_FRAMES;      /* 1024 frames */
    const int blocks = frames / block;

    std::vector<short> baseline((size_t)frames * 2, 0);
    std::vector<short> taps_master((size_t)frames * 2, 0);
    std::vector<short> voice[MDP_SPC_VOICE_COUNT];
    for (int c = 0; c < MDP_SPC_VOICE_COUNT; c++)
        voice[c].assign((size_t)block, 0);
    std::vector<short> echo((size_t)block * 2, 0);

    char error[256];
    mdp_spc_session* session = NULL;
    mdp_spc_audio_buffers audio;
    mdp_spc_render_result result;

    /* --- Baseline: taps disabled. --- */
    {
        mdp_spc_open_options opts;
        memset(&opts, 0, sizeof(opts));
        opts.event_capacity = 64;
        if (mdp_spc_open(spc.data(), spc.size(), &opts, &session,
                         error, sizeof(error)) != MDP_SPC_OK)
        {
            fprintf(stderr, "TAP FAIL: baseline open failed: %s\n", error);
            return 1;
        }
        memset(&audio, 0, sizeof(audio));
        for (int i = 0; i < blocks; i++)
        {
            audio.master_stereo = baseline.data() + (long)i * block * 2;
            if (mdp_spc_render(session, block, &audio, NULL, 0, &result) != MDP_SPC_OK)
            {
                fprintf(stderr, "TAP FAIL: baseline render failed\n");
                mdp_spc_close(session);
                return 1;
            }
        }
        mdp_spc_close(session);
        session = NULL;
    }

    /* --- Taps armed: voice + echo PCM requested. --- */
    {
        mdp_spc_open_options opts;
        memset(&opts, 0, sizeof(opts));
        opts.event_capacity = 64;
        opts.enable_voice_pcm = 1;
        opts.enable_echo_pcm = 1;
        if (mdp_spc_open(spc.data(), spc.size(), &opts, &session,
                         error, sizeof(error)) != MDP_SPC_OK)
        {
            fprintf(stderr, "TAP FAIL: taps open failed: %s\n", error);
            return 1;
        }
        memset(&audio, 0, sizeof(audio));
        audio.echo = echo.data();
        for (int c = 0; c < MDP_SPC_VOICE_COUNT; c++)
            audio.voice[c] = voice[c].data();
        for (int i = 0; i < blocks; i++)
        {
            audio.master_stereo = taps_master.data() + (long)i * block * 2;
            if (mdp_spc_render(session, block, &audio, NULL, 0, &result) != MDP_SPC_OK)
            {
                fprintf(stderr, "TAP FAIL: taps render failed\n");
                mdp_spc_close(session);
                return 1;
            }
        }
        mdp_spc_close(session);
        session = NULL;
    }

    int rc = 0;

    /* The per-block taps are written into the same per-block buffers every
     * block, so the buffers hold the last block's taps; the keyed voices keep
     * sustaining through the loop, which is all the assertions need. */

    if (memcmp(baseline.data(), taps_master.data(),
               (size_t)frames * 2 * sizeof(short)) != 0)
    {
        fprintf(stderr, "TAP FAIL: taps-enabled master differs from "
                        "taps-disabled master (must be bit-identical)\n");
        rc = 1;
    }

    bool v0_nonzero = false;
    bool v1_nonzero = false;
    for (size_t i = 0; i < voice[0].size(); i++)
    {
        v0_nonzero |= voice[0][i] != 0;
        v1_nonzero |= voice[1][i] != 0;
    }
    if (!v0_nonzero || !v1_nonzero)
    {
        fprintf(stderr, "TAP FAIL: expected voice taps 0/1 to be non-silent "
                        "(v0=%d v1=%d)\n", (int) v0_nonzero, (int) v1_nonzero);
        rc = 1;
    }

    for (int c = 2; c < MDP_SPC_VOICE_COUNT; c++)
    {
        for (size_t i = 0; i < voice[c].size(); i++)
        {
            if (voice[c][i] != 0)
            {
                fprintf(stderr, "TAP FAIL: voice %d must stay silent\n", c);
                rc = 1;
                break;
            }
        }
    }

    for (size_t i = 0; i < echo.size(); i++)
    {
        if (echo[i] != 0)
        {
            fprintf(stderr, "TAP FAIL: echo tap must stay zero (no EON, "
                            "echo volumes zero)\n");
            rc = 1;
            break;
        }
    }

    if (rc == 0)
    {
        printf("TAP OK: %d frames, master bit-identical, voices 0/1 "
               "non-silent, voices 2..7 and echo silent\n", frames);
    }
    return rc;
}
