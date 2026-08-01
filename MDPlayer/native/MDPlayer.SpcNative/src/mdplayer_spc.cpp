/*
 * mdplayer_spc.cpp — implementation of the MDPlayer SPC C ABI.
 *
 * Wraps the vendored Game_Music_Emu SPC core (see UPSTREAM.md) behind a small
 * C ABI. No C++ exception ever crosses the boundary: every entry point is
 * wrapped in try/catch and reports an int result code plus a diagnostic
 * string.
 */
#include "mdplayer_spc.h"

#include "spc_capture.h"
#include "spc_taps.h"
#include "spc_pitch.h"

#include "Spc_Emu.h"
#include "Music_Emu.h"
#include "Data_Reader.h"

#include <new>
#include <stdlib.h>
#include <string.h>

namespace
{

const char kSpcSignature[] = "SNES-SPC700 Sound File Data v0.30";
const size_t kSpcSignatureLen = sizeof(kSpcSignature) - 1;
const size_t kRamOffset = 0x100;
const size_t kDspOffset = kRamOffset + MDP_SPC_RAM_SIZE; /* 0x10100 */

void set_error(char* error, size_t error_size, const char* message)
{
    if (!error || error_size == 0 || !message)
        return;
    size_t n = strlen(message);
    if (n > error_size - 1)
        n = error_size - 1;
    memcpy(error, message, n);
    error[n] = '\0';
}

} // namespace

struct mdp_spc_session
{
    Music_Emu* emu;
    int event_capacity;
    bool enable_voice_pcm;
    bool enable_echo_pcm;
    bool accurate_dsp;
    bool closed;
    uint8_t ram[MDP_SPC_RAM_SIZE];          /* initial snapshot state (PR 2) */
    uint8_t dsp[MDP_SPC_DSP_REGISTER_SIZE]; /* initial snapshot state (PR 2) */

    /* PR 3: previous-block voice snapshots for per-block event capture. */
    spc_capture::mdp_spc_voice_snapshot prev_voices[MDP_SPC_VOICE_COUNT];
    bool has_prev;
    int64_t total_frames; /* frames rendered so far (event frame base) */

    /* PR 8: per-block effective-pitch tap results (see spc_pitch.h). */
    spc_pitch::taps_t pitch_taps;

    mdp_spc_session()
        : emu(NULL), event_capacity(64), enable_voice_pcm(false),
          enable_echo_pcm(false), accurate_dsp(true), closed(false),
          has_prev(false), total_frames(0)
    {
        memset(ram, 0, sizeof(ram));
        memset(dsp, 0, sizeof(dsp));
        memset(prev_voices, 0, sizeof(prev_voices));
        memset(pitch_taps.effective, 0, sizeof(pitch_taps.effective));
        pitch_taps.has_capture = false;
    }

    ~mdp_spc_session()
    {
        delete emu;
        emu = NULL;
    }
};

/* The session always owns a Spc_Emu (never any other Music_Emu subtype). */
inline Spc_Emu& spc_emu(mdp_spc_session* session)
{
    return *static_cast<Spc_Emu*>(session->emu);
}

int mdp_spc_open(
    const uint8_t* data,
    size_t size,
    const mdp_spc_open_options* options,
    mdp_spc_session** out_session,
    char* error, size_t error_size)
{
    if (!data || !out_session)
        return MDP_SPC_ERR_INVALID_ARGUMENT;
    if (error && error_size)
        error[0] = '\0';

    if (size < MDP_SPC_MIN_FILE_SIZE
        || memcmp(data, kSpcSignature, kSpcSignatureLen) != 0)
    {
        set_error(error, error_size,
            "invalid SPC file: signature and/or minimum size (0x10180 bytes) mismatch");
        return MDP_SPC_ERR_UNSUPPORTED_FORMAT;
    }

    mdp_spc_session* session = new (std::nothrow) mdp_spc_session();
    if (!session)
    {
        set_error(error, error_size, "out of memory");
        return MDP_SPC_ERR_OUT_OF_MEMORY;
    }

    if (options)
    {
        session->event_capacity = options->event_capacity > 0 ? options->event_capacity : 64;
        session->enable_voice_pcm = options->enable_voice_pcm != 0;
        session->enable_echo_pcm = options->enable_echo_pcm != 0;
        session->accurate_dsp = options->accurate_dsp != 0;
    }

    /* Safe copy for session lifetime: header+RAM+DSP (the SPC snapshot layout). */
    memcpy(session->ram, data + kRamOffset, MDP_SPC_RAM_SIZE);
    memcpy(session->dsp, data + kDspOffset, MDP_SPC_DSP_REGISTER_SIZE);

    try
    {
        Spc_Emu* emu = new Spc_Emu();
        /* GME requires set_sample_rate() before loading a file. The official
         * gme_load_data path (Mem_File_Reader + load) populates the track
         * layout that Music_Emu::start_track_ needs; load_mem() alone skips
         * that and start_track() then asserts on an empty track table. */
        const char* err = emu->set_sample_rate(MDP_SPC_SAMPLE_RATE);
        /* Disable GME's initial-silence pre-roll BEFORE start_track: it runs
         * the SPC700 until audio appears, which would consume early KEY_ON /
         * state transitions before the first capture baseline. The timeline
         * starts at sample 0 (spec §12.5), so the pre-roll must not run ahead
         * of capture. */
        emu->ignore_silence(true);
        if (!err)
        {
            Mem_File_Reader in(data, (long)size);
            err = emu->load(in);
        }
        if (!err)
            err = emu->start_track(0);
        if (err)
        {
            set_error(error, error_size, err ? err : "SPC initialization failed");
            delete emu;
            delete session;
            return MDP_SPC_ERR_UNSUPPORTED_FORMAT;
        }
        emu->set_tempo(1.0);
        emu->mute_voices(0); /* bitmask: 0 = no voice muted */
        session->emu = emu;

        /* PR 3: capture the baseline voice state right after load/start so the
         * first render block can detect transitions (e.g. KEY_ON). Pure reads. */
        spc_capture::snapshot_all(*static_cast<Spc_Emu*>(emu), session->prev_voices);
        session->has_prev = true;
        session->total_frames = 0;
    }
    catch (std::bad_alloc&)
    {
        delete session;
        set_error(error, error_size, "out of memory");
        return MDP_SPC_ERR_OUT_OF_MEMORY;
    }
    catch (...)
    {
        delete session;
        set_error(error, error_size, "unexpected native failure during open");
        return MDP_SPC_ERR_INTERNAL;
    }

    *out_session = session;
    return MDP_SPC_OK;
}

int mdp_spc_render(
    mdp_spc_session* session,
    int requested_frames,
    mdp_spc_audio_buffers* audio_buffers,
    mdp_spc_event* events,
    int event_capacity,
    mdp_spc_render_result* result)
{
    if (!session || session->closed)
        return MDP_SPC_ERR_SESSION_CLOSED;
    if (!result
        || requested_frames < MDP_SPC_MIN_BLOCK_FRAMES
        || requested_frames > MDP_SPC_MAX_BLOCK_FRAMES)
        return MDP_SPC_ERR_INVALID_ARGUMENT;

    result->frames_rendered = 0;
    result->events_written = 0;
    result->is_end = 0;
    result->voice_flags = 0;
    result->event_overflow = 0;

    if (audio_buffers && audio_buffers->master_stereo)
    {
        try
        {
            /* PR 6: arm the per-voice/echo tap destinations before the block
             * render so the S-DSP writes the §8.2 post-envelope voice samples
             * and the §8.4 echo-return signal into the caller's buffers in
             * lockstep with the master. The taps are pure writes to caller
             * memory (see spc_taps.h); the emulation is never modified, so the
             * master is bit-identical whether or not taps are requested
             * (spec §5.3/§30.1). */
            bool taps_armed = false;
            if (session->enable_voice_pcm || session->enable_echo_pcm)
            {
                spc_taps::TapBuffers taps;
                memset(&taps, 0, sizeof(taps));
                taps.stride = 1;
                if (session->enable_voice_pcm)
                {
                    for (int i = 0; i < MDP_SPC_VOICE_COUNT; i++)
                        taps.voice[i] = audio_buffers->voice[i]; /* may be NULL */
                }
                if (session->enable_echo_pcm)
                    taps.echo = audio_buffers->echo; /* may be NULL */
                spc_taps::enable(spc_emu(session), taps);
                taps_armed = true;
            }

            /* PR 8: arm the effective-pitch taps for this block. Pure pointer
             * stores into the DSP's non-emulation state plus pure writes to
             * caller memory during play(); the DSP runs byte-identically
             * whether or not taps are set, so the master stays bit-identical
             * to a no-tap render (spec §5.3). */
            spc_pitch::begin_block(spc_emu(session), &session->pitch_taps);

            /* Music_Emu::play fills count stereo frames into master_stereo. */
            const char* err = session->emu->play(requested_frames, audio_buffers->master_stereo);

            spc_pitch::end_block(spc_emu(session), &session->pitch_taps);
            if (taps_armed)
                spc_taps::disable(spc_emu(session));

            if (err)
                return MDP_SPC_ERR_INTERNAL;
            result->frames_rendered = requested_frames;
        }
        catch (...)
        {
            /* Never leave tap destinations armed, even on a failed block. */
            if (session->enable_voice_pcm || session->enable_echo_pcm)
                spc_taps::disable(spc_emu(session));
            spc_pitch::end_block(spc_emu(session), &session->pitch_taps);
            return MDP_SPC_ERR_INTERNAL;
        }
    }

    /* PR 3 voice-state observation. Pure reads through the patched accessors
     * (see spc_capture.h); runs after every block regardless of whether an
     * events buffer was supplied, so the master is bit-identical to an
     * uninstrumented render (spec §5.3). */
    spc_capture::mdp_spc_voice_snapshot current[MDP_SPC_VOICE_COUNT];
    /* PR 8: report the block's LAST effective pitch per voice when a block was
     * rendered with taps armed, else fall back to the register pitch. */
    spc_capture::snapshot_all_pitched(spc_emu(session),
        session->pitch_taps.has_capture ? session->pitch_taps.effective : NULL,
        current);
    result->voice_flags = spc_capture::active_mask(current);

    if (result->frames_rendered > 0)
    {
        int overflow = 0;
        if (events && event_capacity > 0 && session->has_prev)
        {
            result->events_written = spc_capture::emit_events(
                session->prev_voices, current, session->total_frames,
                events, event_capacity, &overflow);
        }
        result->event_overflow = overflow;
        memcpy(session->prev_voices, current, sizeof(current));
        session->has_prev = true;
        session->total_frames += result->frames_rendered;
    }

    return MDP_SPC_OK;
}

int mdp_spc_copy_ram(
    mdp_spc_session* session,
    uint8_t* out,
    size_t out_size)
{
    if (!session || session->closed)
        return MDP_SPC_ERR_SESSION_CLOSED;
    if (!out || out_size < MDP_SPC_RAM_SIZE)
        return MDP_SPC_ERR_INVALID_ARGUMENT;
    memcpy(out, session->ram, MDP_SPC_RAM_SIZE);
    return MDP_SPC_OK;
}

int mdp_spc_copy_dsp_registers(
    mdp_spc_session* session,
    uint8_t* out,
    size_t out_size)
{
    if (!session || session->closed)
        return MDP_SPC_ERR_SESSION_CLOSED;
    if (!out || out_size < MDP_SPC_DSP_REGISTER_SIZE)
        return MDP_SPC_ERR_INVALID_ARGUMENT;
    memcpy(out, session->dsp, MDP_SPC_DSP_REGISTER_SIZE);
    return MDP_SPC_OK;
}

int mdp_spc_get_voice_state(
    mdp_spc_session* session,
    int channel,
    mdp_spc_voice_state* out)
{
    if (!session || session->closed)
        return MDP_SPC_ERR_SESSION_CLOSED;
    if (channel < 0 || channel >= MDP_SPC_VOICE_COUNT || !out)
        return MDP_SPC_ERR_INVALID_ARGUMENT;
    try
    {
        /* Read the current effective voice state straight from the S-DSP. The
         * emulator is not advanced, so this is safe between renders. */
        spc_capture::mdp_spc_voice_snapshot snap;
        spc_capture::snapshot(spc_emu(session).dsp_ref(),
                              spc_emu(session).apu_ref().smp_ram(),
                              channel, &snap);
        /* PR 8: report the effective pitch captured during the last render
         * block (taps armed), else the register-pitch fallback. */
        if (session->pitch_taps.has_capture)
            snap.effective_pitch = session->pitch_taps.effective[channel];
        spc_capture::fill_abi(&snap, out);
        return MDP_SPC_OK;
    }
    catch (...)
    {
        return MDP_SPC_ERR_INTERNAL;
    }
}

void mdp_spc_close(mdp_spc_session* session)
{
    if (!session)
        return;
    try
    {
        session->closed = true;
        delete session;
    }
    catch (...)
    {
        /* Never throw across the C ABI. */
    }
}
