#!/usr/bin/env python3
"""Generates the deterministic librosa differential fuzz corpus.

Creates ``librosa-differential-fuzz.json``: 10,000 pseudo-random sparse onset
envelopes generated from a FIXED seed together with the exact beat frames that
librosa 0.11 returns for each envelope. The xUnit replay
(``EllisLibrosaDifferentialFuzzTests``) rebuilds every envelope in C# and
requires ZERO beat-frame mismatches against the stored expectations — no
Python dependency at test runtime.

Usage::

    python3 generate-librosa-fuzz.py

Requires: librosa==0.11.0, numpy.
"""

import json
import random
from pathlib import Path

import numpy as np
import librosa

SEED = 20260825
CASE_COUNT = 10_000
SR = 100
HOP = 1


def main() -> None:
    rng = random.Random(SEED)
    cases = []
    for index in range(CASE_COUNT):
        frame_count = rng.randint(60, 600)
        bpm = rng.randint(60, 200)
        onset_count = rng.randint(0, 14)
        onset_frames = sorted(rng.sample(range(frame_count),
                                         min(onset_count, frame_count)))
        onsets = [[f, round(rng.uniform(0.05, 2.0), 3)] for f in onset_frames]
        # Every fifth case exercises the trim=True path (Hann-smoothed RMS).
        trim = index % 5 == 3

        envelope = np.zeros(frame_count)
        for frame, strength in onsets:
            envelope[frame] = strength
        _bpm_value, beats = librosa.beat.beat_track(
            onset_envelope=envelope, sr=SR, hop_length=HOP,
            bpm=bpm, tightness=100.0, trim=trim)

        cases.append({
            "frameCount": frame_count,
            "bpm": bpm,
            "trim": trim,
            "onsets": onsets,
            "expectedFrames": [int(b) for b in beats],
        })
        if (index + 1) % 1000 == 0:
            print(f"{index + 1}/{CASE_COUNT}")

    document = {
        "reference":
            "librosa.beat.beat_track 0.11.0, bpm fixed, tightness 100",
        "generator": "generate-librosa-fuzz.py",
        "seed": SEED,
        "caseCount": CASE_COUNT,
        "sampleRate": SR,
        "hopSamples": HOP,
        # Compact single-line arrays keep the file small; the replay test is
        # the only consumer.
        "cases": cases,
    }
    out = Path(__file__).resolve().parent / "librosa-differential-fuzz.json"
    out.write_text(json.dumps(document, separators=(",", ":")) + "\n")
    print(f"wrote {out} ({out.stat().st_size / 1024:.0f} KiB)")


if __name__ == "__main__":
    main()
