# MIDI adversarial corpus

`adversarial-manifest.json` is the sidecar contract for semantic MIDI export
checks. The source files are repository fixtures copied into the test output by
the test project; the manifest does not duplicate binary media.

The `featuresToReview` labels are review prompts, not asserted facts. `pending`
means that the entry has not been reviewed and is rejected by
`--require-reviewed`. A reviewed entry may assert expected fields, or it may be
explicitly marked `unresolved` with a non-empty evidence note when all
corresponding `allowUnresolved*` gates are true. Null expectations intentionally
keep the harness from turning an unverified guess into a golden test.

Corpus checks must compare semantics: channel ownership, pitch reconstruction,
source-time round trips, meter/tempo resolution state, and explicit abstention.
They must not compare complete MIDI byte streams.

## Positive controls

Positive controls share one validation definition
(`TryParsePositiveControlExpectation` in `MidiAdversarialCorpusTests`): a
control must declare a complete reviewed truth — a tempo (exact BPM **or** an
octave/beat-grouping family), a meter, and a downbeat expectation. "A downbeat
exists" is not a positive control.

The contract supports two tempo modes:

- **Exact mode** (`expected.tempo.bpm`): the reviewed single BPM must equal the
  inferred segment tempo. Used only where the evidence is decisive (real,
  human-reviewed audio).
- **Family mode** (`expected.tempo.acceptedFamily`): the inferred tempo may land
  on any member of the reviewed octave/beat-subdivision family. Used where the
  accent evidence is symmetric — e.g. 40/60/120/240 are all musically real
  readings of the same kick/snare grid, so a single BPM is unreachable.

Meter and downbeat are each exact when the manifest pins the exact value
(`numerator`/`denominator`, `quarter`), and resolved-only (the field resolves,
but the exact reviewed value is withheld because it is boundary luck, not an
accent fit) when only `mustBeResolved: true` is set.

### Why the two synthetic controls are family-level

- `positive-control-120-4-4.json` is generated at 120 BPM in 4/4, but its accent
  grid (kick on the bar downbeat, snare on beats 2 & 4, hi-hat on every tatum)
  is **octave/beat-subdivision symmetric**: 40 (four-on-the-floor), 60
  (double-kick), 120 (as generated) and 240 are all defensible, and the tracker
  resolves to 40. Exact 120 is unreachable from this evidence, so the contract
  asserts the family `{40,60,120,240}` and withholds the exact downbeat phase
  (quarter 0 was boundary luck from the loop-restart marker, not an accent fit).
- `positive-control-90-6-8.json` is generated at 90 BPM in 6/8, but the nominal
  90 is unreachable: the tracker's hypothesis set is hop-quantized to 0.5-BPM
  bins, so its nearest compound reading is 91 in 6/8. Verified DBN output
  resolves the control to 60 BPM / 4/4 (with 91/6/8 and 182/6/8 as lower
  alternatives), so neither 90 nor 91/6/8 is the emitted winner. The contract
  asserts the family `{40,60,91,120,182}`, asserts a meter is resolved (the
  tracker reads 4/4 here, so the exact 6/8 grid is withheld), and asserts a
  downbeat is resolved without an exact phase.

Both controls still prove the pipeline **requires resolution** (`mustBeResolved:
true`) — the DBN resolves a grid — even though the synthetic evidence does not
pin a single exact tempo, meter, or downbeat phase. Abstention/negative controls
are untouched and must still abstain.

### Real-audio positive controls (BLOCKED on assets)

The corpus requires five REAL-music controls: two resolved 4/4 tracks, one
resolved 3/4 or 6/8 track, one true half/double-tempo ambiguous case, and one
true sparse/unresolved case (expected abstention). Integrity rule: these must be
REAL audio-derived fixtures reviewed by a human; synthesized stand-ins labeled
as real are forbidden, so none may be added until such audio exists.

Required manifest schema for a real-audio control once an asset is provided:

```json
{
  "source": "<track-name>.wav",
  "captureMode": "real-audio-positive-control",
  "controlId": "real-4-4-a",
  "featuresToReview": ["..."],
  "expected": {
    "tempo": { "bpm": 120.0, "mustBeAmbiguous": false, "mustBeResolved": true },
    "meter": { "numerator": 4, "denominator": 4, "mustBeResolved": true },
    "downbeat": { "mustBeResolved": true, "quarter": 0 }
  },
  "allowUnresolvedTempo": false,
  "allowUnresolvedMeter": false,
  "allowUnresolvedDownbeat": false,
  "reviewStatus": "reviewed",
  "reviewNotes": "<who verified the grid, against what evidence>"
}
```

For the half/double-ambiguous control, `expected.tempo.mustBeResolved` is false,
`mustBeAmbiguous` true, and the entry declares `"expectedAbstention": true`.
For the sparse/unresolved control the entry uses `reviewStatus: "unresolved"`
with evidence notes and all three `allowUnresolved*` gates true.

Required audio inputs:

- Lossless or uncompressed files (`wav` preferred; `flac` acceptable), 44.1 kHz
  or 48 kHz, mono or stereo.
- At least 30 seconds containing the reviewed musical section; no fades over
  the reviewed downbeat.
- A review note recording how tempo/meter/downbeat were independently
  confirmed (e.g., transcription, DAW grid alignment).
- File placed in `MDPlayer/tests/Corpus/midi/real-audio/` (or a tracked link)
  plus a `Content` entry in `MDPlayer.Fmp.Tests.csproj` copying it to
  `testfixtures/corpus/real-audio/`, and a `FixtureLinks` mapping.

`RealAudioPositiveControls_ArePresentReviewedAndExact` validates every such
entry through the shared positive-control definition and skips with the exact
list of still-missing required controls until all five exist.
