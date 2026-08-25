#!/usr/bin/env python3
"""Regenerates ellis-reference-fixtures.json from librosa 0.11.

Every expected beat-frame list is produced by calling
``librosa.beat.beat_track`` with a FIXED bpm (tightness 100) on a synthetic
sparse onset envelope. The C# test project replays these fixtures through
``EllisBeatTracker.TrackFixedTempo`` and demands frame-exact parity.

Usage::

    python3 generate-ellis-fixtures.py   # writes ellis-reference-fixtures.json

Requires: librosa==0.11.0 (the pinned oracle), numpy.
Deterministic: the fixture set below is static; no randomness is involved.
"""

import json
from pathlib import Path

import numpy as np
import librosa

SR = 100  # sample rate used by every fixture (frame rate == SR / hop)
HOP = 1

# Each entry: name, frame_count, bpm, [(frame, strength)] and an optional
# "why" describing the adversarial property the fixture exercises. Frames must
# be UNIQUE because the C# replay assigns (not accumulates) envelope values,
# mirroring this generator.
FIXTURES = [
    # --- legacy fixtures, regenerated from librosa ---
    ("periodic-120", 320, 120, [(3, 1.0), (53, 1.0), (103, 1.0), (153, 1.0),
                                (203, 1.0), (253, 1.0), (303, 1.0)],
     "steady metrical grid"),
    ("jittered-111", 360, 111, [(4, 1.0), (58, 1.0), (112, 1.0), (166, 1.0),
                                (220, 1.0), (274, 1.0), (328, 1.0),
                                (31, 0.25), (85, 0.25), (139, 0.25),
                                (193, 0.25), (247, 0.25), (301, 0.25),
                                (355, 0.25)],
     "weak inter-beat jitter against a strong grid"),
    ("sparse-60", 420, 60, [(7, 1.5), (107, 1.5), (207, 1.5), (307, 1.5),
                            (407, 1.5), (57, 0.3), (157, 0.3), (257, 0.3),
                            (357, 0.3)],
     "slow tempo with weak subdivisions"),
    ("leading-silence-120", 360, 120, [(40, 1.0), (90, 1.0), (140, 1.0),
                                       (190, 1.0), (240, 1.0), (290, 1.0),
                                       (340, 1.0)],
     "grid starting after leading silence"),
    ("weak-pickup-120", 320, 120, [(8, 0.05), (53, 1.0), (103, 1.0),
                                   (153, 1.0), (203, 1.0), (253, 1.0),
                                   (303, 1.0)],
     "very weak pickup before the grid"),
    ("missing-beat-120", 360, 120, [(3, 1.0), (53, 1.0), (153, 1.0),
                                    (203, 1.0), (253, 1.0), (303, 1.0)],
     "dropped onset mid-pattern"),
    ("syncopated-120", 320, 120, [(15, 0.8), (28, 0.35), (65, 0.8),
                                  (78, 0.35), (115, 0.8), (128, 0.35),
                                  (165, 0.8), (178, 0.35), (215, 0.8),
                                  (228, 0.35), (265, 0.8), (278, 0.35)],
     "off-beat syncopation against the grid"),
    ("jittered-sparse-90", 420, 90, [(7, 1.0), (75, 1.0), (142, 1.0),
                                     (209, 1.0), (277, 1.0), (344, 1.0),
                                     (411, 1.0)],
     "humanized inter-onset intervals"),
    # --- patch F2 additions ---
    ("isolated-single-onset-160", 445, 160, [(240, 1.0)],
     "REGRESSION: single isolated onset; librosa returns [202, 240, 278] "
     "while the previous C# implementation extended to the end of the buffer"),
    ("isolated-single-onset-120", 300, 120, [(150, 1.0)],
     "single isolated onset at slow tempo"),
    ("long-leading-silence-120", 440, 120, [(200, 1.0), (250, 1.0),
                                            (300, 1.0), (350, 1.0),
                                            (400, 1.0)],
     "more than one bar of silence before the first onset"),
    ("long-trailing-silence-120", 440, 120, [(40, 1.0), (90, 1.0),
                                             (140, 1.0), (190, 1.0),
                                             (240, 1.0)],
     "more than one bar of silence after the last onset"),
    ("long-lead-trail-silence-90", 500, 90, [(150, 1.0), (217, 1.0),
                                             (283, 1.0), (350, 1.0)],
     "silence on both sides of a short grid"),
    ("cumulative-plateau-120", 380, 120, [(50, 1.0), (51, 1.0), (100, 1.0),
                                          (101, 1.0), (150, 1.0), (151, 1.0),
                                          (200, 1.0), (201, 1.0), (250, 1.0),
                                          (251, 1.0), (300, 1.0)],
     "adjacent equal-strength onsets create equal-score plateaus that "
     "exercise localmax tie handling"),
    ("even-maxima-140", 400, 140, [(30, 1.2), (73, 1.0), (116, 1.2),
                                   (159, 1.0), (202, 1.2), (245, 1.0)],
     "alternating strengths produce an EVEN number of cumulative-score "
     "maxima, exercising the averaged even-count median"),
    ("all-zero-envelope-120", 200, 120, [],
     "silent envelope: normalization degenerates to zeros/tiny"),
    ("sparse-intro-outro-120", 420, 120, [(10, 0.08), (60, 0.12),
                                          (110, 1.0), (160, 1.0),
                                          (210, 1.0), (260, 1.0),
                                          (310, 1.0), (360, 0.1),
                                          (410, 0.06)],
     "weak intro and outro around a strong middle grid"),
]


def expected_frames(frame_count: int, bpm: float,
                    onsets) -> list[int]:
    envelope = np.zeros(frame_count)
    for frame, strength in onsets:
        assert envelope[frame] == 0.0, f"duplicate onset frame {frame}"
        envelope[frame] = strength
    _bpm, beats = librosa.beat.beat_track(
        onset_envelope=envelope, sr=SR, hop_length=HOP,
        bpm=bpm, tightness=100.0, trim=False)
    return [int(b) for b in beats]


def main() -> None:
    fixtures = []
    for name, frame_count, bpm, onsets, why in FIXTURES:
        frames = expected_frames(frame_count, bpm, onsets)
        fixtures.append({
            "name": name,
            "oracle": "librosa-0.11.0",
            "sampleRate": SR,
            "hopSamples": HOP,
            "frameCount": frame_count,
            "bpm": bpm,
            "onsets": [[f, s] for f, s in onsets],
            "expectedFrames": frames,
            "covers": why,
        })
        print(f"{name}: {frames}")

    document = {
        "reference":
            "librosa.beat.beat_track 0.11.0, bpm fixed, tightness 100, trim false",
        "generator": "generate-ellis-fixtures.py",
        "fixtures": fixtures,
    }
    out = Path(__file__).resolve().parent / "ellis-reference-fixtures.json"
    out.write_text(json.dumps(document, indent=2) + "\n")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
