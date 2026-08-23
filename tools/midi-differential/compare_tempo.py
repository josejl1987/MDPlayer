#!/usr/bin/env python3
"""Compare MDPlayer symbolic tempo output with optional MIR references.

This is a development/corpus tool. It deliberately does not add Python, librosa,
or Essentia to MDPlayer's runtime or test dependencies. The reference trackers
consume a click-like onset envelope synthesized from the persisted symbolic
timeline, so a disagreement is an investigation signal rather than a golden
answer.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import tempfile
from pathlib import Path
from typing import Any


def load_timeline(path: Path) -> dict[str, Any]:
    with path.open(encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise ValueError("timeline root must be an object")
    return value


def onset_events(timeline: dict[str, Any]) -> list[tuple[int, float]]:
    events: list[tuple[int, float]] = []
    for note in timeline.get("notes", []):
        if not isinstance(note, dict) or note.get("isRetrigger", False):
            continue
        sample = note.get("startSample")
        if isinstance(sample, int):
            events.append((sample, 1.0))
    for hit in timeline.get("rhythm", []):
        if not isinstance(hit, dict):
            continue
        sample = hit.get("samplePosition")
        strength = hit.get("strength", 1.0)
        if isinstance(sample, int) and isinstance(strength, (int, float)):
            events.append((sample, max(0.1, float(strength)) * 1.4))
    return events


def onset_envelope(timeline: dict[str, Any], hop_length: int) -> tuple[Any, int]:
    import numpy as np

    sample_rate = int(timeline["sampleRate"])
    start = int(timeline.get("startSample", 0))
    end = max(start + 1, int(timeline.get("endSample", start + 1)))
    frame_count = max(1, math.ceil((end - start) / hop_length) + 1)
    envelope = np.zeros(frame_count, dtype=float)
    for sample, strength in onset_events(timeline):
        if sample < start or sample > end:
            continue
        frame = min(frame_count - 1, max(0, round((sample - start) / hop_length)))
        envelope[frame] = min(4.0, envelope[frame] + strength)
    return envelope, sample_rate


def librosa_tempo(timeline: dict[str, Any], hop_length: int) -> float | None:
    import librosa

    envelope, sample_rate = onset_envelope(timeline, hop_length)
    tempo, _ = librosa.beat.beat_track(
        onset_envelope=envelope,
        sr=sample_rate,
        hop_length=hop_length,
        start_bpm=120.0,
        tightness=100,
        trim=False,
    )
    values = getattr(tempo, "reshape", lambda *_: [tempo])(-1)
    if len(values) == 0 or not math.isfinite(float(values[0])):
        return None
    return float(values[0])


def essentia_tempo(timeline: dict[str, Any], hop_length: int) -> float | None:
    """Run Essentia's multifeature tracker when it is installed."""

    import numpy as np
    import essentia.standard as es

    envelope, sample_rate = onset_envelope(timeline, hop_length)
    click_length = max(1, round(sample_rate * 0.025))
    audio = np.zeros(len(envelope) * hop_length + click_length, dtype=np.float32)
    for frame, strength in enumerate(envelope):
        if strength <= 0:
            continue
        start = frame * hop_length
        audio[start : start + click_length] += float(min(1.0, strength / 2.0))
    bpm, _, _, _, _ = es.RhythmExtractor2013(method="multifeature")(audio)
    return float(bpm) if math.isfinite(float(bpm)) and float(bpm) > 0 else None


def mdplayer_tempo(
    mdplayer: Path,
    timeline: Path,
) -> tuple[float | None, str, bool | None]:
    with tempfile.TemporaryDirectory(prefix="mdplayer-midi-diff-") as directory:
        output = Path(directory) / "comparison.mid"
        completed = subprocess.run(
            [
                str(mdplayer),
                "midi",
                "dummy",
                "--timeline",
                str(timeline),
                "--output",
                str(output),
                "--musical-grid",
            ],
            capture_output=True,
            text=True,
            check=False,
        )
    text = completed.stdout + "\n" + completed.stderr
    match = re.search(r"tempo: ([0-9]+(?:\.[0-9]+)?) BPM", text)
    resolved = re.search(r"tempo-resolved: (True|False)", text)
    if completed.returncode != 0:
        return None, f"MDPlayer failed with exit {completed.returncode}: {text.strip()}", None
    if match is None:
        return None, "MDPlayer completed without a musical tempo line", None
    return (
        float(match.group(1)),
        "ok",
        resolved is not None and resolved.group(1) == "True",
    )


def dyadic_distance(left: float | None, right: float | None) -> float | None:
    if left is None or right is None or left <= 0 or right <= 0:
        return None
    return abs(math.log2(left / right))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeline", type=Path, required=True)
    parser.add_argument("--mdplayer", type=Path, required=True)
    parser.add_argument("--hop-length", type=int, default=512)
    args = parser.parse_args()
    if args.hop_length <= 0:
        parser.error("--hop-length must be positive")

    timeline = load_timeline(args.timeline)
    report: dict[str, Any] = {
        "timeline": str(args.timeline),
        "sampleRate": timeline.get("sampleRate"),
        "onsetCount": len(onset_events(timeline)),
        "hopLength": args.hop_length,
    }

    md_bpm, md_status, md_resolved = mdplayer_tempo(args.mdplayer, args.timeline)
    report["mdplayerBpm"] = md_bpm
    report["mdplayerStatus"] = md_status
    report["mdplayerTempoResolved"] = md_resolved

    try:
        report["librosaBpm"] = librosa_tempo(timeline, args.hop_length)
        report["librosaStatus"] = "ok"
    except ImportError as error:
        report["librosaBpm"] = None
        report["librosaStatus"] = f"unavailable: {error}"

    try:
        report["essentiaBpm"] = essentia_tempo(timeline, args.hop_length)
        report["essentiaStatus"] = "ok"
    except ImportError as error:
        report["essentiaBpm"] = None
        report["essentiaStatus"] = f"unavailable: {error}"

    references = [
        value
        for value in (report.get("librosaBpm"), report.get("essentiaBpm"))
        if isinstance(value, (int, float)) and value > 0
    ]
    report["dyadicDistance"] = {
        "librosa": dyadic_distance(md_bpm, report.get("librosaBpm")),
        "essentia": dyadic_distance(md_bpm, report.get("essentiaBpm")),
    }
    report["referenceAgreement"] = (
        len(references) >= 2
        and max(references) / min(references) < 2 ** 0.15
    )
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if md_bpm is not None else 2


if __name__ == "__main__":
    raise SystemExit(main())
