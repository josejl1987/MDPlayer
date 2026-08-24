# Derived Export Rules — `derived-export-rules.md`

Rules derived from baseline evidence. **These are phase-2 proposals** — they are
NOT implemented until the baseline gates close (spec §73). Each rule has:
ID · problem · evidence · affected tracks · reference behavior · MDPlayer
decision · counterexample · test required.

Source of evidence: `reports/reference-baseline.json` (same-input VGM, vgm2mid
vs MDPlayer @ `4bf5d7c5`). Audio/A-B and the SMPS semantic tier are pending and
**may revise these rules** before implementation (spec §73 order).

---

## RULE-ATTACK-001 — attack reconstruction is the highest-impact defect

- **Problem:** MDPlayer recovers far fewer voices/attacks than a register-level
  reference on the same VGM.
- **Evidence (baseline §5.1):** robotnik 994 vs 24236 notes (0.041×); smoking-head
  940 vs 16662 (0.056×); twilight-express 1140 vs 13311 (0.086×). Master-ninja
  0.355×, stranger 0.41×.
- **Affected:** YM2608/OPNA physical voices (all these fixtures are chip-music
  with busy FM); likely also PSG/noise.
- **Reference behavior:** `vgm2mid` keys NoteOn to YM **KeyOn/KeyOff** register
  transitions (`Modules/YM2612.bas` NoteOn_1/NoteOn_2); every hardware key
  transition is an attack. `smps2mid` keys NoteOn to the driver's note sequence.
- **MDPlayer decision (proposed):** prioritize hardware **key-transition
  reconstruction over frequency-merge**. A FNUM write is a bend while a voice is
  keyed; a KeyOn (or driver semantic note) is an attack. Where MDPlayer is
  collapsing distinct keyed voices into long held notes, the attack stage must
  not merge across KeyOff/KeyOn boundaries.
- **Counterexample:** a single sustained chord should remain one note, not N
  re-attacks from FNUM jitter — the fix is to source attacks from KeyOn, not
  from pitch movement.
- **Test required:** synthetic — N distinct KeyOn/KeyOff cycles on one YM channel
  must yield N notes, with jittered FNUM inside one KeyOn yielding 0 attacks and
  bent pitch (spec §63/§65 FM stable tone with register jitter).

## RULE-BEND-001 — bend spam must be compressed at the source

- **Problem:** MDPlayer serializes FNUM quantization/vibrato as dense pitch
  bends.
- **Evidence:** bendDensityRatio 22.77× (robotnik), 15.86× (smoking-head), 10.1×
  (twilight-express); MDPlayer BPN 6–7.5 vs reference 0.26–0.66. Even in the
  "good" fixtures BPN is ~2× reference.
- **Affected:** FM voices with read-modify-write FNUM streams.
- **Reference behavior:** `cdmd2mid` dedups identical bends (`lastPB`) and snaps
  PB range to a ladder, so stationary micro-jitter collapses; `tsd2mid` renders
  *parameterized* LFO vibrato, not raw f-num samples.
- **MDPlayer decision (proposed):** classify pitch movement: (a) **stationary
  residual/jitter** below a musical threshold → suppress or fold into one bend /
  RPN tuning, not a bend per sample; (b) **intentional vibrato** → bounded
  periodic bends (or CC1) with regularity detection; (c) **wide slides** → few
  bends (or portamento semantics). Never "frequency changed → PitchBend" for
  every write.
- **Counterexample:** a genuine wide intentional slide must still bend (not be
  suppressed) — keep the intentional-slide path (PitchNormalization's
  expressive transitions) but gate the noise path.
- **Test required:** one KeyOn + ±0.3-cent jitter for N samples → bounded event
  count, no fake attacks, ≤ k bends (spec §63 example fixture).

## RULE-TEMPO-001 — tempo must agree with an external reference before trust

- **Problem/baseline:** twilight-express MD Player 25 BPM vs vgm2mid 120;
  robotnik 225 vs 120; stranger 188.8 vs 120.
- **Reference:** vgm2mid emits a fixed source-derived tempo (120 here);
  `smps2mid` uses the driver's tempo (`GetMidiTempo`) — the octave/scale truth for
  triads. MDPlayer infers (SymbolicInference) independently.
- **Decision (proposed):** do not tune the scorer against itself; the *octave ×2/÷2
  correctness must be validated against a semantic reference in the available
  triads before accepting an inferred BPM (spec §60). Until a semantic reference
  exists for a song, treat MDPlayer's inferred tempo as provisional.
- **Test required:** tempo half/double case fixture; when the semantic reference
  is 112 BPM, an MDPlayer 56 BPM output is an octave error (spec §65 tempo
  half/double case).

## RULE-CORPUS-001 — the semantic SMPS tier is a data prerequisite, not optional

- **Problem:** no driver-aware reference yet, so octave/tempo/percussion-vs-DAC
  claims can't be verified (spec §37 triads).
- **Evidence:** smps-rips provides 68k/Z80/Pico/preSMPS + DefDrv/DefDrum/DAC.ini;
  smps2mid 0.4.3 source + binary present and mapped.
- **Decision:** before implementing RULE-ATTACK/BEND/TEMPO, run the smps2mid
  semantic corpus (≥12 songs, ≥3 families) and close the A/B listening gate; the
  MDPlayer vocal-chord (10161-notes master-ninja) and note-loss decisions may be
  revised by the resulting semantic policy comparison.
- **Test required:** semantic corpus generation is reproducible (scripted
  interaction), outputs hash-verified.

---

### Not yet evidenced (do NOT implement from intuition — spec §2/§50/§51)

- `RPN tuning` vs `constant residual bend` vs `change base note` for static
  detune — not decided; cdmd2mid uses residual bend, but RPN is not ruled out.
- vibrato as `CC1` vs `bounded bends` vs `discard` — smps2mid uses CC1, but the
  MDPlayer fixtures need measurement before choosing.
- portamento as `NoteOn target` vs `tie` vs `CC5/CC65` — undecided (waiting on
  tsd2mid/cdmd2mid slides against our actual slides).
- arpeggio as `bends` vs `multiple notes` — undecided.
- FM→GM percussion channel — left `opt-in`, not default, until the semantic
  corpus shows what the driver's drum maps do (spec §58).

These become individually scoped RULE-* entries only after the missing evidence
(baseline gates) exists.
