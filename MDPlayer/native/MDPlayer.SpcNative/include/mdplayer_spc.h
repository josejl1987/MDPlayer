/*
 * mdplayer_spc.h — MDPlayer SPC native C ABI.
 *
 * A small, exception-free C ABI around the Game_Music_Emu SPC core
 * (see UPSTREAM.md for the pinned upstream commit and the PR 3 build-time
 * instrumentation patch). No exception may ever cross this boundary; every
 * entry point returns an int result code and writes diagnostics into a
 * caller-provided buffer.
 *
 * Audio: 32,000 Hz stereo int16, master = the unmodified S-DSP stereo result.
 */
#ifndef MDPLAYER_SPC_H
#define MDPLAYER_SPC_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* PR 3 bumped the API version: voice-state fields and capture events were
 * APPENDED to the PR 2 structs, so binaries built against PR 2 headers keep
 * their offsets (ABI-compatible extension). PR 8 appends one more voice-state
 * field (effective_pitch), again ABI-compatible. */
#define MDP_SPC_API_VERSION        3
#define MDP_SPC_SAMPLE_RATE        32000
#define MDP_SPC_VOICE_COUNT        8

/* Default render block, and the allowed range (spec §7). */
#define MDP_SPC_DEFAULT_BLOCK_FRAMES 1024
#define MDP_SPC_MIN_BLOCK_FRAMES   256
#define MDP_SPC_MAX_BLOCK_FRAMES   4096

/* Minimum .spc file: 0x100 header + 64 KiB RAM + 128 DSP registers. */
#define MDP_SPC_MIN_FILE_SIZE      0x10180
#define MDP_SPC_RAM_SIZE           0x10000
#define MDP_SPC_DSP_REGISTER_SIZE  0x80

/* Opaque session; created by mdp_spc_open, destroyed by mdp_spc_close. */
typedef struct mdp_spc_session mdp_spc_session;

/* Result codes. 0 == success, negative == failure. */
enum
{
    MDP_SPC_OK                 = 0,
    MDP_SPC_ERR_INVALID_ARGUMENT = -1,
    MDP_SPC_ERR_UNSUPPORTED_FORMAT = -2,
    MDP_SPC_ERR_OUT_OF_MEMORY  = -3,
    MDP_SPC_ERR_INTERNAL       = -4,
    MDP_SPC_ERR_NOT_IMPLEMENTED = -5,
    MDP_SPC_ERR_SESSION_CLOSED = -6
};

/* Per-block capture events (PR 3, spec §9.1). After every render block each
 * voice's effective state is compared with the previous block and transition
 * events are appended to the caller's buffer. No per-sample callbacks.
 * Events are ordered by channel (0..7), then by the order below. */
enum
{
    MDP_SPC_EVENT_KEY_ON            = 1, /* inactive -> active            param0 = source_number        */
    MDP_SPC_EVENT_RELEASE_START     = 2, /* envelope mode -> release      param0 = envelope_level      */
    MDP_SPC_EVENT_VOICE_END         = 3, /* active -> inactive            param0 = envelope_level      */
    MDP_SPC_EVENT_SOURCE_CHANGED    = 4, /* param0 = new source_number, param1 = old source_number      */
    MDP_SPC_EVENT_PITCH_CHANGED     = 5, /* effective-pitch delta > 1 unit (PR 8); param0 = new effective pitch, param1 = old */
    MDP_SPC_EVENT_VOLUME_CHANGED    = 6, /* param0 = volume_l, param1 = volume_r                       */
    MDP_SPC_EVENT_NOISE_CHANGED     = 7, /* param0 = noise_enabled                                    */
    MDP_SPC_EVENT_PITCH_MOD_CHANGED = 8, /* param0 = pitch_mod_enabled                                */
    MDP_SPC_EVENT_ECHO_SEND_CHANGED = 9  /* param0 = echo_send_enabled                                */
};

/* Envelope modes reported in mdp_spc_voice_state.envelope_mode (values match
 * the upstream S-DSP voice_t enum; see UPSTREAM.md "PR 3 local
 * modifications"). */
enum
{
    MDP_SPC_ENV_RELEASE = 0,
    MDP_SPC_ENV_ATTACK  = 1,
    MDP_SPC_ENV_DECAY   = 2,
    MDP_SPC_ENV_SUSTAIN = 3
};

typedef struct mdp_spc_open_options
{
    int event_capacity;   /* capture-event capacity per render block (0 = default 64; PR 3+) */
    int enable_voice_pcm; /* 1 = also fill per-voice PCM buffers (PR 6) */
    int enable_echo_pcm;  /* 1 = also fill echo-buffer PCM (PR 6) */
    int accurate_dsp;     /* 1 = accurate DSP mode (Snes_Spc is always accurate) */
} mdp_spc_open_options;

typedef struct mdp_spc_audio_buffers
{
    int16_t* master_stereo;                 /* interleaved L/R int16, frames * 2 samples */
    int16_t* voice[MDP_SPC_VOICE_COUNT];    /* per-voice mono PCM, frames samples each (PR 6) */
    int16_t* echo;                          /* echo-return PCM, stereo, frames * 2 samples (PR 6) */
} mdp_spc_audio_buffers;

/* Capture event (PR 3). frame = the sample-frame position (32 kHz) of the
 * block start in which the transition was first detected (per-block
 * granularity, spec §9.1). */
typedef struct mdp_spc_event
{
    int64_t frame;    /* sample-frame position (32 kHz) */
    int     type;     /* one of the MDP_SPC_EVENT_* constants */
    int     channel;  /* 0..7, or -1 for master */
    int     param0;   /* event-specific */
    int     param1;   /* event-specific */
} mdp_spc_event;

typedef struct mdp_spc_render_result
{
    int frames_rendered; /* stereo frames written to master_stereo this call */
    int events_written;  /* events appended this call (PR 3) */
    int is_end;          /* song-end detected (PR 3+; 0 in PR 2) */
    int voice_flags;     /* bitmask of active voices after this block (PR 3) */
    int event_overflow;  /* 1 if more events occurred than fit the buffer (PR 3) */
} mdp_spc_render_result;

typedef struct mdp_spc_voice_state
{
    /* PR 2 fields (offsets unchanged). */
    int     channel;        /* voice index 0..7 */
    int     active;         /* 1 = producing sound (enabled, not in release, env > 0) */
    int     volume_l;       /* 0..255 (DSP register v_voll) */
    int     volume_r;       /* 0..255 (DSP register v_volr) */
    int     pitch;          /* fixed-point pitch, 0..0x3FFF (v_pitchl/h) */
    int     source_number;  /* sample index (v_srcn) */
    int64_t sample_position;/* sample offset within the source (approx.) */
    int     noise_enabled;  /* 1 if the voice uses the noise channel (NON) */

    /* PR 3 appended fields (ABI-compatible). */
    int     envelope_mode;  /* MDP_SPC_ENV_* constant */
    int     envelope_level; /* raw internal envelope level 0..0x7FF (2047) */
    int     brr_address;    /* current BRR block address in 64K RAM, 0..0xFFFF */
    int     kon_delay;      /* KON setup phase counter 0..5 (0 = fully keyed on) */
    int     pitch_mod_enabled;  /* 1 if the voice is pitch-modulated (PMON) */
    int     echo_send_enabled;  /* 1 if the voice feeds the echo buffer (EON) */

    /* PR 8 appended field (ABI-compatible). */
    int     effective_pitch; /* DSP-calculated effective pitch (register pitch
                              * + PMON adjustment, spec §10.2/§13.6); equals
                              * pitch when pitch modulation is inactive or no
                              * block has been rendered yet */
} mdp_spc_voice_state;

/*
 * Opens an SPC session from an in-memory snapshot. Validates the SNES-SPC700
 * signature and the minimum size. No file I/O. The input bytes are copied for
 * the lifetime of the session.
 */
int mdp_spc_open(
    const uint8_t* data,
    size_t size,
    const mdp_spc_open_options* options, /* may be NULL for defaults */
    mdp_spc_session** out_session,
    char* error, size_t error_size);     /* optional diagnostics buffer */

/*
 * Renders up to requested_frames stereo frames (256..4096, default block
 * 1024) at 32,000 Hz into audio_buffers.master_stereo. events may be NULL.
 *
 * PR 3: after each rendered block the voice observer reads the S-DSP state
 * (read-only, see spc_capture.h) — it never writes to the emulator, so the
 * master output is bit-identical to an uninstrumented render (spec §5.3).
 * Voice-state transitions are appended to events (up to event_capacity);
 * result->events_written / result->event_overflow / result->voice_flags are
 * always filled.
 *
 * PR 6: when the session was opened with enable_voice_pcm/enable_echo_pcm,
 * the S-DSP additionally writes the §8.2 per-voice post-envelope samples into
 * audio_buffers.voice[i] (mono, frames samples each) and the §8.4 echo-return
 * signal into audio_buffers.echo (stereo, frames*2 samples). The taps are
 * pure writes to caller memory and never change the emulation, so the master
 * is bit-identical whether or not taps are requested (spec §5.3/§30.1).
 * voice[i] and echo may be NULL (per-channel disable); buffers are filled in
 * lockstep with the master render and must be sized for the requested block.
 */
int mdp_spc_render(
    mdp_spc_session* session,
    int requested_frames,
    mdp_spc_audio_buffers* audio_buffers,
    mdp_spc_event* events, int event_capacity,
    mdp_spc_render_result* result);

/* Copies the session RAM (PR 2: initial snapshot state). */
int mdp_spc_copy_ram(
    mdp_spc_session* session,
    uint8_t* out, size_t out_size);

/* Copies the DSP registers (PR 2: initial snapshot state). */
int mdp_spc_copy_dsp_registers(
    mdp_spc_session* session,
    uint8_t* out, size_t out_size);

/* Returns the current effective voice state (PR 3). Read-only; may be called
 * between renders. The state reflects the emulator as of the last render
 * block (or the loaded SPC before the first render). */
int mdp_spc_get_voice_state(
    mdp_spc_session* session,
    int channel,
    mdp_spc_voice_state* out);

/* Closes the session. NULL-safe. */
void mdp_spc_close(mdp_spc_session* session);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* MDPLAYER_SPC_H */
