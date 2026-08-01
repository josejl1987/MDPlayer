/*
 * event_capture_test.cpp — in-block S-DSP event capture regression coverage.
 *
 * The fixture schedules several KON/KOFF/source/pitch writes from the SPC700
 * CPU. The assertions intentionally inspect absolute event frames and the
 * voice tap, rather than only checking master-audio parity.
 */
#include "mdplayer_spc.h"

#include <stdio.h>
#include <string.h>
#include <vector>

namespace
{

const char kSignature[] = "SNES-SPC700 Sound File Data v0.30";
const size_t kSpcSize = 0x10200;
const size_t kRamOffset = 0x100;
const size_t kDspOffset = 0x10100;

void write_dsp(std::vector<unsigned char>& spc, size_t& pc, int address, int value)
{
    size_t ram = kRamOffset + pc;
    spc[ram++] = 0x8F; /* MOV dp,#imm */
    spc[ram++] = (unsigned char)address;
    spc[ram++] = 0xF2;
    spc[ram++] = 0x8F;
    spc[ram++] = (unsigned char)value;
    spc[ram++] = 0xF3;
    pc += 6;
}

void write_nops(std::vector<unsigned char>& spc, size_t& pc, int count)
{
    for (int i = 0; i < count; i++)
        spc[kRamOffset + pc++] = 0x00; /* NOP */
}

std::vector<unsigned char> make_fixture()
{
    std::vector<unsigned char> spc(kSpcSize, 0);
    memcpy(spc.data(), kSignature, sizeof(kSignature) - 1);
    spc[0x23] = 0x30;
    spc[0x24] = 1;
    spc[0x25] = 0x00;
    spc[0x26] = 0x02;
    spc[0x2B] = 0xFF;

    size_t pc = 0x0200;
    write_dsp(spc, pc, 0x4C, 0x01); /* KON voice 0: source 1 */
    write_nops(spc, pc, 800);       /* about 50 output frames */
    write_dsp(spc, pc, 0x5C, 0x01); /* KOFF voice 0 */
    write_nops(spc, pc, 800);
    write_dsp(spc, pc, 0x04, 0x00); /* SRCN voice 0: valid source 0 */
    write_dsp(spc, pc, 0x4C, 0x01); /* first retrigger */
    write_nops(spc, pc, 800);
    write_dsp(spc, pc, 0x5C, 0x01); /* KOFF voice 0 */
    write_nops(spc, pc, 800);
    write_dsp(spc, pc, 0x04, 0x01); /* restore source 1 for second retrigger */
    write_dsp(spc, pc, 0x02, 0x00); /* pitch = 0x1800 */
    write_dsp(spc, pc, 0x03, 0x18);
    write_dsp(spc, pc, 0x4C, 0x01); /* second retrigger */
    write_nops(spc, pc, 400);
    write_dsp(spc, pc, 0x02, 0x00); /* return pitch to 0x1000 */
    write_dsp(spc, pc, 0x03, 0x10);
    write_nops(spc, pc, 400);
    write_dsp(spc, pc, 0x5C, 0x01); /* final KOFF; release must remain audible */
    spc[kRamOffset + pc] = 0x2F;
    spc[kRamOffset + pc + 1] = 0xFE; /* hang */

    /* DIR entries for sources 0 and 1. */
    size_t dir = kRamOffset + 0x0300;
    for (int source = 0; source < 2; source++)
    {
        unsigned start = 0x0400u + (unsigned)source * 0x100u;
        spc[dir + source * 4 + 0] = (unsigned char)(start & 0xFF);
        spc[dir + source * 4 + 1] = (unsigned char)(start >> 8);
        spc[dir + source * 4 + 2] = (unsigned char)(start & 0xFF);
        spc[dir + source * 4 + 3] = (unsigned char)(start >> 8);
        size_t brr = kRamOffset + start;
        spc[brr] = 0xA3; /* end+loop, scale 10 */
        for (int i = 0; i < 8; i++)
            spc[brr + 1 + i] = 0xF0;
    }

    unsigned char* dsp = spc.data() + kDspOffset;
    dsp[0x00] = 0x7F; /* voice 0 voll */
    dsp[0x01] = 0x7F; /* voice 0 volr */
    dsp[0x02] = 0x00;
    dsp[0x03] = 0x10; /* 0x1000 */
    dsp[0x04] = 0x01; /* start on source 1 */
    dsp[0x05] = 0xFF; /* ADSR */
    dsp[0x06] = 0xE0;
    dsp[0x0C] = 0x7F;
    dsp[0x1C] = 0x7F;
    dsp[0x4C] = 0x00; /* KON is CPU-driven */
    dsp[0x5C] = 0x00; /* KOFF is CPU-driven */
    dsp[0x5D] = 0x03; /* DIR */
    dsp[0x6C] = 0x00;
    return spc;
}

} // namespace

int main()
{
    const std::vector<unsigned char> spc = make_fixture();
    const int frames = 2 * MDP_SPC_DEFAULT_BLOCK_FRAMES;
    const int block = MDP_SPC_DEFAULT_BLOCK_FRAMES;
    std::vector<short> master((size_t)frames * 2);
    std::vector<short> voice((size_t)frames);
    std::vector<mdp_spc_event> buffer(1024);
    std::vector<mdp_spc_event> captured;

    mdp_spc_open_options options;
    memset(&options, 0, sizeof(options));
    options.event_capacity = (int)buffer.size();
    options.enable_voice_pcm = 1;
    mdp_spc_session* session = NULL;
    char error[256];
    if (mdp_spc_open(spc.data(), spc.size(), &options, &session,
                     error, sizeof(error)) != MDP_SPC_OK)
    {
        fprintf(stderr, "EVENT FAIL: open failed: %s\n", error);
        return 1;
    }

    int rc = 0;
    for (int block_index = 0; block_index < 2; block_index++)
    {
        mdp_spc_audio_buffers audio;
        memset(&audio, 0, sizeof(audio));
        audio.master_stereo = master.data() + (long)block_index * block * 2;
        audio.voice[0] = voice.data() + (long)block_index * block;
        mdp_spc_render_result result;
        if (mdp_spc_render(session, block, &audio, buffer.data(),
                           (int)buffer.size(), &result) != MDP_SPC_OK)
        {
            fprintf(stderr, "EVENT FAIL: render failed\n");
            rc = 1;
            break;
        }
        if (result.event_overflow)
        {
            fprintf(stderr, "EVENT FAIL: event buffer overflowed\n");
            rc = 1;
            break;
        }
        captured.insert(captured.end(), buffer.begin(),
                        buffer.begin() + result.events_written);
    }
    mdp_spc_close(session);
    if (rc != 0)
        return rc;

    std::vector<int64_t> key_on_frames;
    std::vector<int64_t> release_frames;
    int source_zero_key_on = 0;
    int pitch_changes = 0;
    int64_t first_release = -1;
    int64_t voice_end = -1;
    for (const mdp_spc_event& event : captured)
    {
        if (event.channel != 0)
            continue;
        if (event.type == MDP_SPC_EVENT_KEY_ON)
        {
            key_on_frames.push_back(event.frame);
            if (event.param0 == 0)
                source_zero_key_on++;
        }
        else if (event.type == MDP_SPC_EVENT_RELEASE_START)
        {
            release_frames.push_back(event.frame);
            if (first_release < 0)
                first_release = event.frame;
        }
        else if (event.type == MDP_SPC_EVENT_VOICE_END && voice_end < 0)
            voice_end = event.frame;
        else if (event.type == MDP_SPC_EVENT_PITCH_CHANGED)
            pitch_changes++;
    }

    if (key_on_frames.size() < 3
        || key_on_frames[0] == key_on_frames[1]
        || key_on_frames[1] == key_on_frames[2]
        || source_zero_key_on != 1)
    {
        fprintf(stderr, "EVENT FAIL: expected three distinct KON events with one SRCN 0 (got %zu, zero=%d)\n",
                key_on_frames.size(), source_zero_key_on);
        return 1;
    }
    if (key_on_frames[0] / block != key_on_frames[1] / block
        || key_on_frames[1] / block != key_on_frames[2] / block)
    {
        fprintf(stderr, "EVENT FAIL: retriggers escaped the first render block\n");
        return 1;
    }
    if (release_frames.size() < 3 || first_release < 0 || voice_end <= first_release)
    {
        fprintf(stderr, "EVENT FAIL: release/end ordering was not captured\n");
        return 1;
    }
    bool release_audio = false;
    for (int64_t sample = first_release + 1;
         sample < voice_end && sample < frames; sample++)
        release_audio |= voice[(size_t)sample] != 0;
    if (!release_audio)
    {
        fprintf(stderr, "EVENT FAIL: no voice-tap audio survived RELEASE_START\n");
        return 1;
    }
    if (pitch_changes < 2)
    {
        fprintf(stderr, "EVENT FAIL: pitch change/return was collapsed (%d events)\n",
                pitch_changes);
        return 1;
    }

    printf("EVENT OK: %zu KON, %zu releases, %d pitch changes, release tail audible\n",
           key_on_frames.size(), release_frames.size(), pitch_changes);
    return 0;
}
