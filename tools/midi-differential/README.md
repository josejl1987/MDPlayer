# MIDI tempo differential harness

This opt-in development tool compares the musical tempo reported by the MDPlayer
CLI with `librosa.beat.beat_track` and, when installed, Essentia's
`RhythmExtractor2013(method="multifeature")`.

It synthesizes a click-like onset envelope from a persisted symbolic timeline.
The result is an alarm for disagreement, not a golden tempo: 56/112 BPM is a
metrical equivalence class and still requires corpus review.

Example:

```sh
python3 tools/midi-differential/compare_tempo.py \
  --timeline "/path/to/song.visualization/timeline.json" \
  --mdplayer MDPlayer/src/MDPlayer.Fmp.Cli/bin/Debug/net8.0/mdplayer-render
```

The product and test projects do not depend on Python, librosa, or Essentia.
Missing reference packages are reported in JSON instead of being silently
treated as agreement.

Run the real-file export corpus with:

```sh
python3 tools/midi-differential/run_corpus.py \
  --manifest MDPlayer/tests/Corpus/midi/adversarial-manifest.json \
  --source-root /path/to/MDPlayer \
  --timeline-root /path/to/MDPlayer \
  --mdplayer MDPlayer/src/MDPlayer.Fmp.Cli/bin/Debug/net8.0/mdplayer-render
```

The runner validates that every successful result is an SMF, reports decoder
fallbacks explicitly, retains the exact capture with `midi --timeline-out`, and
independently checks serialized note pitch, note/rhythm/sample timing, channel
ownership, RPN state, and SMF ordering. It returns a nonzero status for export
or round-trip failures. Use `--require-reviewed` only after human sidecars have
filled the expected musical labels.
