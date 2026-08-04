# Native audio: FMP YM2608 backend selection

This page documents the **user-facing** workflow for choosing how FMP-family
sources ("YM2608 audio") are rendered. It is part of the Prompt 10R work —
adding the `native-audio` backend selector to the Avalonia renderer while keeping
MDSound as the default.

> The low-level implementation details of the native OPNA backend (two-pass
> capture/replay, vendored YM2608-LLE synthesis, deterministic output) are kept
> in [`opna-lle-implementation-notes.md`](opna-lle-implementation-notes.md).
> The `native-lle`/live-IRC/status-driven concepts described there are
> **historical and non-production**; production uses trace-driven capture and
> replay.

---

## Backend selector

FMP-family inputs (`.ov*`, `.mpi`, `.ozi`, MVI/MZI, …) expose a **YM2608 audio**
selector in the render settings panel, next to the other FMP-specific controls
(SSG gain, SPC pitch).

Options:

| Label         | Serialized value | Meaning                                            |
|---------------|------------------|----------------------------------------------------|
| MDSound       | `mdsound`        | Existing trace-driven MDSound capture (**default**) |
| Native audio  | `native-audio`   | Trace-driven native YM2608 audio replay            |

- The selector is **FMP-only**: it is hidden for VGM, SPC, MIDI, S98, and other
  inputs.
- Enum identifiers are **not** shown; only the labels above appear.
- There is **no** automatic backend selection and **no** fallback — the chosen
  backend is used as-is. `native-audio` never silently falls back to MDSound.

## Default behavior

The default is **MDSound** in every situation:

- a new application session,
- old saved settings without a backend field,
- a new render job,
- a command-line invocation without the option.

Backend availability is **never** inferred from library presence, and existing
users are **never** auto-migrated to native audio.

## Persistence

The selected backend is persisted via the existing render-settings store as the
lowercase strings `mdsound`/`native-audio`.

- Missing / empty `/` old value: stays **MDSound**.
- **Invalid or retired** values (notably the retired `native-lle`): **rejected**
  at the CLI and ignored (kept at MDSound) when loading saved settings. They must
  never be silently promoted to native audio.

## Request ownership

There is exactly **one** authoritative backend value per render, carried on the
shared request (`Playback.OpnaBackend`). It flows:

```
backend selector → view model → render request → preview/export coordinator → FMP session
```

There are no separate preview/export/batch/audio-backend fields. Changing the
selector invalidates the current preview capture (because it changes the audio
capture surface) and cancels an in-flight render.

## Lazy native validation

The native library is **not** loaded or probed at application startup. Validation
only happens when a `native-audio` render/preview actually starts:

- **Missing library** → *"Native YM2608 audio is unavailable because the native
  runtime library could not be loaded."* (expected runtime location shown in
  details).
- **ABI mismatch** → *"The installed native YM2608 library is incompatible with
  this version of MDPlayer."* (expected/actual ABI versions shown).
- **Unsupported cadence** → *"This track changes the YM2608 prescaler, which the
  native-audio backend does not currently support."* (first offending register and
  position shown).

The full exception is preserved in the diagnostic log. A failed native render
**does not** switch the selector back to MDSound and **does not** start an
MDSound render.

## Capture cache

To avoid re-running the (expensive) control-capture pass when the user previews
the same FMP track more than once, the renderer keeps **one** completed capture
for the current track. It is reused between a native preview and a native export
of the same track when no capture-affecting setting changed, and is invalidated on
any track/loop/duration/fade/tail/bank/compatibility change. Failed and cancelled
captures are never cached.

## Cancellation

Cancellation works through the shared cancellation token across
availability-check, capture, native replay, PPZ8 replay, drain, WAV writing, and
video export. Cancelling capture never begins native replay; cancelling replay
never falls back. Incomplete temporary output is deleted.

## Progress

Native-audio render is presented as two phases with the existing progress UI:

1. **Analyzing FMP playback** — capture phase; uses the playback timeline / an
   indeterminate state when the termination length is unknown.
2. **Rendering native YM2608 audio** — replay phase; reports rendered frames
   against total captured frames (a known length).

## Command line

The request-driven `render`/`preview` command accepts:

```
--opna-backend mdsound|native-audio
```

Default: `mdsound`. `native-lle` (and any other value) is rejected.

## FAQ / troubleshooting

- **I see the selector but not "Native audio".** Native audio is only offered
  for FMP-family inputs, matching the existing FMP-specific controls.
- **I chose Native audio and got an error.** It is almost always one of the three
  focused messages above (missing library / ABI / cadence). The diagnostic log has
  the full exception. No time/audio is wasted on a fallback.
- **Native preview then export re-rendered from scratch.** You changed a
  capture-affecting setting between the two (duration, loop, fade/tail, banks, or
  the track), so the cache was invalidated by design.
