# Converter Behavior Study — `converter-behavior.md`

Reference-driven study of how external MIDI converters decide what is a note, a
tie, a bend, vibrato, portamento, arpeggio, tempo, instrument, volume, pan and
percussion. This is a behavioral study documented per symbol; **no third-party
code is copied into MDPlayer**.

Sources reviewed (pinned in `tools.lock.json`):
- `ValleyBell/MidiConverters` @ `a73c7489` — `cdmd2mid.c`, `tsd2mid.c`, `fmp2mid.c`
- `smps2mid 0.4.3` source (VB6) — `smps2mid.bas`
- `vgm2mid 0.5` (Paul Jensen + Valley Bell) source (VB6) — `Modules/YM2612.bas`, `Modules/VGM.bas`

---

## 1. `cdmd2mid.c` (Falcom sound data on YM2608/OPN)

Purpose: converts the internal driver note stream (not raw register writes) to
MIDI. It therefore knows note semantics up front. `CDMD` = the driver's music
data format.

### State (per channel) — `CHN_INF` (struct, lines ~46–70)
```c
UINT16 pitchBase;  // pitch of the current MIDI note, 8.8 fixed point (for PB calc)
UINT16 curPitch;   // current note pitch (after applied pitch effects), 8.8
UINT16 lastPB;     // last written pitch bend (for dedup)
UINT8  pbRange;    // active pitch-bend range in semitones
```
Vertex evidence that ValleyBell keeps **explicit musical state per channel** —
exactly the model the MDPlayer baseline must compare against (spec §2, §49).

### What opens a NoteOn / closes a NoteOff
- A **note event** in the CDMD data opens the note; the converter writes a
  program change, sets `pitchBase = note << 8`, `curPitch = pitchBase`,
  `pitchOct = Note2Octave(note >> 8)`, then writes the pitch (line ~1354–1365).
- `WriteEvent(...,0x90, ci->lastNote, 0x00)` is emitted when a re-anchor
  restarts the current note (see below). NoteOff is emitted **only** by the
  driver's note-off / new-keydown model, not by every frequency write.
- **Frequency changes are pitch bends, not new notes** — unless they exceed the
  active bend range.

### Re-anchor policy — `WriteNotePitch` (defn line ~90, body ~1130)
`pitchDiff = notePitch - pitchBase`. If `absPDiff > (pbRange << 8)`:
```c
// stop note, move base, restart note
if (pitchDiff >= 0) newBase = (notePitch >> 8) + pbRange;  // slide up -> low end
else                newBase = ((notePitch + 0xFF) >> 8) - pbRange; // down -> high end
ci->pitchBase = newBase << 8;
restartNote = 1;
if (ci->lastNote != 0xFF) WriteEvent(..., 0x90, ci->lastNote, 0x00);  // NOTE OFF
```
So a **re-anchor is an accepted, driver-aware operation** (it corresponds to a
real >range slide). It emits `NoteOn 0x90 note 0` (an explicit off) then
re-anchors the base. This is the reference precedent for MDPlayer's
"synthetic reanchor" being *legal* when it maps a genuine wide slide (spec §54:
`bend-range exhausted` / `semantic note transition`), never for register noise.

### Pitch bend range — `EnsurePBRange` / `MinimizePBRange` (lines ~1040 / ~1108)
- `EnsurePBRange`: snaps the required range to a **fixed ladder**:
  `2, 12, 24, 48, 64, 96, 127` (and `MAX_PB_RANGE`). It only ever **grows** the
  range, never shrinks it arbitrarily.
- `MinimizePBRange`: called after a re-anchor to shrink the range back to the
  minimum that still fits the current diff (also snapped to the same ladder).
- Conclusion: **PB range values are always from a small standard ladder, held
  per channel, and treated as a channel-global state** (spec §55). Not free-form.

### Pitch-bend value — `WriteNotePitch`
```c
pbVal = (INT32)pitchDiff * 8192 / pbRange / 256;   // sign-symmetric-ish
if (pbVal < -0x2000) pbVal = -0x2000; else if (pbVal > +0x1FFF) pbVal = +0x1FFF;
midPB = 0x2000 + pbVal;
if (optional && midPB == ci->lastPB) return;         // dedup identical bends
```
- **Bend dedup**: consecutive identical bend values are suppressed (the
  `optional` flag governs it).
- `NotePitch2YMFreq` / `YMFreq2NotePitch` convert between the YM f-num table and
  8.8 note pitch (lines ~88–89), and `ApplyYMDeltaToPitch` applies a f-num delta
  (used for arpeggio/pitch steps).

### Arpeggio vs pitch slide vs portamento
- The CDMD driver distinguishes them and cdmd2mid keeps them separate:
  - **Arpeggio** (`pitchStep` stepping between `pitchBase` and `pitchEnd`, lines
    ~951–966) is emitted as successive `WriteNotePitch` calls — i.e. as
    *pitch bends on the same note*, not separate NoteOns.
  - **Portamento** uses `CalculatePortaTime(srcPitch,oct,dstPitch,delta)` →
    `PortaTime2Midi(...)` → the result goes into a MIDI **portamento time / CC**
    region (search `PortaTime2Midi`), separate from vibrato.
- So ValleyBell models **arpeggio, pitch slide (portamento glide) and vibrato as
  distinct effects** rather than one undifferentiated "frequency changed".

### Instrument
- `CreateInstrumentMap(romLen, romData, startOfs)` (line ~644) builds a driver
  instrument → MIDI program map from the ROM instrument table; a program change
  is emitted when the driver changes instrument (`ci->insIdx` -> program, lines
  ~1340). Not a GM guess — a direct driver mapping.

### Tempo
- `Tempo2Mid(tickFrames)` (INLINE ~102) converts the driver's tick/frame timing
  to microseconds per quarter. The driver's tempo word drives the MIDI Set
  Tempo. (spec §60: driver-aware = the source tempo, not an inferred one.)

### Volume / pan
- `OPNInsVol2DB(algo, tls, panMode)` converts OPN total-level to dB →
  `DB2Mid` → CC7 volume. `StereoMask2Pan(mask, &pan2Side)` → CC10. Volume is
  derived from the driver's volume per note, not from a fixed table alone.

---

## 2. `tsd2mid.c` (Telenet "System Designer"/TSx sound data on YM3812/Sound Board)

Purpose: sequence-aware converter (the TS driver music data) → MIDI. Holds the
richest explicit effect state of the three.

### State — `TRK_RAM` (struct, lines ~50–109)
```c
UINT8  curNote, lastNote;
UINT32 noteStartTick, noteOffTick;   // 0A/0B note length events
INT16  pbDetune;                     // 18/19 detune
UINT16 portaDurat; INT32 portaRange; // portamento duration + range
UINT8  vibDelay, vibCurDly, vibStrength, vibSpeed, vibType; INT16 vibPos;  // vibrato
UINT8  psldDelay, psldCurDly; INT16 psldDelta, psldFreq;                    // pitch slide
// bit4/5/6/7: portamento / vibrato / pitch slide / vib-during-porta enable
// bit8/9: tremolo / envelope
```
ValleyBell explicitly tracks **portamento, vibrato, pitch slide, tremolo and
envelope as separate per-track state** (spec §19, §52, §53). MDPlayer cannot
hope to match this from a raw register stream, but it defines the *target
semantics*: separate pitch effects, not a single "pitch moved".

### Note mutation — `ProcessTsdTrkFX` (line ~415)
- `pitchTransp = curNote - lastNote` — notes are defined by the driver, FK starts.
- Portamento: `portaOffset = portaRange * portaPos / portaDurat` interpolated
  linearly across the note; vibrato disabled during porta unless the flag bit 7 set.
- Vibrato: `VibratoLUT(type, idx)` → LFO table → an offset added to `noteFreq`;
  three precision modes (`-PreciseVib`, HIGH_PREC) that trade *strength* for
  resolution. Emitted as pitch bends (via `WritePitchBend`) — a dense, driver
  parametrized LFO, not raw f-num jitter.
- Pitch slide: `psldFreq += psldDelta` each step, also pitch bend.
- `NoteFrac2PitchBend(noteTransp, noteFrac)` (line ~124) converts a fractional
  semitone displacement to a signed bend value.

Conclusion: tsd2mid renders **vibrato/portamento/pitch-slide as pitch bends but
parameterized from driver LFO/duration/range state**, dedups and clamps bends to
±8191, and only the driver's actual new-key events start notes.

---

## 3. `fmp2mid.c` (FMP — PC-98 sound driver)

Purpose: FMP song format → MIDI. **Explicit, verifiable limitation** (spec §4):
```
printf("Note: Only MIDI-based FMP songs are supported. OPN(A) songs don't work.\n");  // line 183
```
So fmp2mid is NOT a reference for our FMP FM/OPNA (the OVI corpus). We study it
for tempo / loops / running-note policy only.

### Tempo — `PCTimer2MidTempo(period, baseClock)` / `YMTimerB2MidTempo(timerB, baseClock)` (lines ~1206/1213)
Converts FMP's PC98 timer / OPN Timer-B period + base clock into microseconds
per quarter. Timers map to tempo directly (source-time fidelity, spec §60).

### Loops / running notes / global events
- `PreparseFmp(...)` (line ~965) + `EVENT_LIST gblEvts` = a global event list for
  tempo/loop bookkeeping; `RUNNING_NOTES` and `BALANCE_TRACK_TIMES` macros
  (lines ~94–95) control running-note compression and per-track time balancing.
- `loopOfs/loopTick/loopTimes` (lines ~80–83) implement loop markers; the master
  track loops `NUM_LOOPS` times (lines ~603–615).

---

## 4. `smps2mid 0.4.3` (VB6) — SMPS driver sequences

Purpose: converts the *SMPS sequence* (Sonic/Ristar 68k & Z80 pre-SMPS) to MIDI.
This is the highest-fidelity semantic reference available for MD/SMS.

### Note / tie / off model (in `smps2mid.bas`)
- The SMPS bytecode drives notes directly (`note on` adds `CF_...`), and the
  converter maps driver notes to MIDI `NoteOn`/`NoteOff`. Ties are driver
  concepts (the driver holds a note on while a new note key comes), so
  `smps2mid` never synthesizes attacks from FNUM changes.
- `WriteEvent(Evt, Val1, Val2, ...)` (line ~3100) is the single event writer:
  `0x90` NoteOn / `0x80` NoteOff.

### Pitch effects
- `FreqMod2PitchBend(FrqDisplc, ChnType)` (line ~3335): converts a SMPS
  frequency displacement (cents/step) to a signed MIDI PB value around `0x2000`,
  chipped by channel type (OPN vs PSG differ in resolution).
  Used for: portamento (`CF_PORTAMENTO &H2A`, line 140), pitch-slide, detune.
- `Modulation2Mid(StpChange, StpCount, ChnType)` (line ~3360): converts the
  SMPS **modulation** command into a MIDI **CC1 (modulation)** value
  (`FreqRange = Abs(FreqMod2PitchBend(Change * StpCount,...)) / 4`). **vibrato →
  CC1**, not dense pitch bends (spec §51).

### Tempo — `GetMidiTempo` (line ~2584)
Uses the driver's own tempo mode: `TEMPOMODE_S12B` (preSMPS/Sonic1: 1-frame
delay per x frames) vs `TEMPOMODE_S3K`/`TEMPOMODE_S2` (x of 256 frames) → exact
BPM → microseconds per quarter. So smps2mid's BPM is the **driver's true BPM**,
making it the ground truth for tempo in the triads (spec §60 octave checks).

### Channels / transpose / PSG / noise / DAC-drums
- Driver channel count and type (FM/PSG) come from the SMPS config header;
  `CF_` flags gate what a channel can do. Instrument changes map to MIDI
  program changes via the driver instrument list (`CreateMappings`, line ~1091).
- PSG noise and FM/PSG drums are mapped from the driver's rhythm/drum
  definitions (drum maps live in the rip's `DefDrum.txt`/`DAC.ini`).

---

## 5. `vgm2mid 0.5` (YM2612 module) — register-level baseline

Purpose: turns a **register log** (VGM) into MIDI — the same abstraction level
as MDPlayer. Not ground truth, but the baseline MDPlayer must beat/approach.

### YM2612 per-channel model (`Modules/YM2612.bas`)
- Per channel: `NoteOn_1/NoteOn_2`, `Hz_1/Hz_2`, `Note_1/Note_2`,
  `MIDINote(5)`, `MIDIWheel(5)`, `MIDIVolume/MIDIPan/MIDIMod`.
- `DoNoteOn`/`MIDI_Event_Write MIDI_NOTE_OFF` — notes are opened/closed by the
  YM2612 **KeyOn/KeyOff** (the `FNum`/`Block` writes update the pitch *while the
  note is held*). This is the key reference point for spec §48:
  **a hardware key transition opens the note; an FNum write is a pitch bend**.
- `Case YM2612_DAC` — DAC is represented as a **drum note** (`MIDI_NOTE_ON,
  CHN_DAC, Data, &H7F`, lines ~211–214), i.e. each DAC sample byte → one GM
  percussion note, with `LastDACNote` held for the off. This is register-level
  approximation; smps2mid's DAC.ini identity is more precise (spec §59).
- Pitch wheel computed from Hz → `Note()` → `MIDIWheel`, i.e. bend = residual
  to nearest note; one bend per note model.

---

## 6. Behavior table

`UNKNOWN` marks anything not evidenced in the reviewed source (not guessed).

| Behavior         | vgm2mid | smps2mid | cdmd2mid | tsd2mid | MDPlayer (frozen baseline) |
| ---------------- | ------- | -------- | -------- | ------- | --------------------------- |
| fuente de NoteOn | YM KeyOn reg | driver NoteOn | driver note event | driver note event | hardware KeyOn (VoiceStateNormalization) — measured |
| fuente de NoteOff| YM KeyOff reg | driver NoteOff | driver note-off / re-anchor restart | driver NoteOff | hardware KeyOff — measured |
| ties             | n/a (KeyOn model) | driver ties kept | driver model | driver model | register-derived; measured |
| detune estático | residual bend | FreqMod2PitchBend | residual via PitchBend | pbDetune field | PitchNormalization resid / RPN — measured |
| vibrato          | n/a (raw freq→bend) | **CC1 modulation** | distinct effect | VibratoLUT → bends | dense bends (measured in baseline) |
| portamento       | n/a | FreqMod2PitchBend | PortaTime→CC | linear interpolated bends | measured |
| arpeggio         | n/a | driver | successive WriteNotePitch | n/a | measured |
| PB range         | (VGM fixed-ish) | n/a (unpitchbent) | ladder {12,24,48,64,96,127} grow/shrink | (fixed) | default 24 (frozen) |
| reanchor         | (per-note only) | n/a | **yes — range-exceeded** | n/a | PitchNormalization reanchors — measured |
| pitch dedup      | n/a | n/a | skip identical `lastPB` | (clamped) | measured |
| tempo            | BPM from VGM (source ticks) | **driver tempo** | driver tempo | driver tempo | **inferred** (SymbolicInference) — measured |
| Program Change   | n/a | driver instruments | CreateInstrumentMap | n/a | metadata-only / GM approx — measured |
| volume           | MIDIVolume per reg | driver volume→dB | OPNInsVol2DB→CC7 | driver | measured |
| pan              | MIDIPan | calc from L/R | StereoMask2Pan→CC10 | n/a | measured |
| percussion       | YM key→GM drum (basic) | driver drum maps | n/a | n/a | FM→GM channel-10 gate (frozen) |
| DAC/PCM          | each DAC byte→drum note | **DAC.ini/DefDrum identity** | n/a | n/a | measured |

Notes:
- `smps2mid` uses **CC1 for vibrato** and **driver tempo**, and maps instruments
  from the driver — the clearest "sequence-aware" policy set.
- `cdmd2mid` proves that even a driver-aware converter uses a small PB-range
  ladder and **re-anchors by stopping + restarting a note** when a wide slide
  exceeds the range — i.e. reanchors are normal in the reference world, and must
  be *explained* (wide source slide), never cavalier (register noise).
- `vgm2mid` is the direct register-level peer of MDPlayer: it keys notes to
  **hardware KeyOn/KeyOff** and bends to **FNum changes while held**, and treats
  the DAC as a drum stream.
