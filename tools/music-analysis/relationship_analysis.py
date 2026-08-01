from __future__ import annotations

from bisect import bisect_left, bisect_right


SUPPORTED_INTERVALS = (0, 12, -12, 24, -24)


def _name(interval):
    if interval == 0:
        return "unison"
    if abs(interval) == 12:
        return "octave-doubling"
    return "two-octave-doubling"


def _match(source, target, interval, onset_tolerance):
    if abs(target["startSample"] - source["startSample"]) > onset_tolerance:
        return False
    if abs((float(target["pitch"]) - float(source["pitch"])) - interval) > .35:
        return False
    overlap = max(0, min(source["endSample"], target["endSample"])
                  - max(source["startSample"], target["startSample"]))
    return overlap > 0


def analyze_relationships(notes, sample_rate, detail="standard"):
    if detail != "full":
        return []
    minimum_duration = max(1, int(round(float(sample_rate) * .060)))
    onset_tolerance = max(1, int(round(float(sample_rate) * .040)))
    grouped = getattr(notes, "by_channel", None)
    channels = sorted(grouped if grouped is not None else {n["channelId"] for n in notes})
    result = []
    for source_id_index, source_id in enumerate(channels):
        source_notes = sorted(
            [n for n in (grouped[source_id] if grouped is not None else notes) if n["channelId"] == source_id
             and n.get("theoryPitched", True)
             and n.get("pitchClass", -1) in range(12)
             and n["endSample"] - n["startSample"] >= minimum_duration],
            key=lambda n: (n["startSample"], n.get("id", "")),
        )
        for target_id in channels[source_id_index + 1:]:
            target_notes = sorted(
                [n for n in (grouped[target_id] if grouped is not None else notes) if n["channelId"] == target_id
                 and n.get("theoryPitched", True)
                 and n.get("pitchClass", -1) in range(12)
                 and n["endSample"] - n["startSample"] >= minimum_duration],
                key=lambda n: (n["startSample"], n.get("id", "")),
            )
            if len(source_notes) < 4 or not target_notes:
                continue
            source_span = max(n["endSample"] for n in source_notes) - min(n["startSample"] for n in source_notes)
            if source_span < sample_rate:
                continue
            source_duration = sum(n["endSample"] - n["startSample"] for n in source_notes)
            onset_index = {}
            for target_index, target in enumerate(target_notes):
                onset_index.setdefault(target["startSample"], []).append((target_index, target))
            onset_keys = sorted(onset_index)
            for interval in SUPPORTED_INTERVALS:
                used_targets = set()
                pairs = []
                for source in source_notes:
                    candidates = [
                        (abs(target["startSample"] - source["startSample"]), target_index, target)
                        for onset in onset_keys[bisect_left(onset_keys, source["startSample"] - onset_tolerance):
                                                bisect_right(onset_keys, source["startSample"] + onset_tolerance)]
                        for target_index, target in onset_index.get(onset, ())
                        if target_index not in used_targets
                        and abs((float(target["pitch"]) - float(source["pitch"])) - interval) <= .35
                        and max(0, min(source["endSample"], target["endSample"]) - max(source["startSample"], target["startSample"])) > 0
                    ]
                    if not candidates:
                        continue
                    _, target_index, target = min(candidates, key=lambda item: (item[0], item[1]))
                    used_targets.add(target_index)
                    overlap = max(0, min(source["endSample"], target["endSample"])
                                  - max(source["startSample"], target["startSample"]))
                    pairs.append((source, target, overlap))
                if len(pairs) < 4:
                    continue
                note_coverage = len(pairs) / len(source_notes)
                duration_coverage = sum(pair[2] for pair in pairs) / max(1, source_duration)
                coverage = min(note_coverage, duration_coverage)
                if coverage < .70:
                    continue
                result.append({
                    "sourceChannelId": source_id,
                    "targetChannelId": target_id,
                    "relationship": _name(interval),
                    "interval": interval,
                    "coverage": round(coverage, 6),
                    "confidence": {
                        "score": round(coverage, 6),
                        "certainty": "strong",
                        "method": "onset-pitch-duration-match",
                        "evidence": [
                            f"matchedNotes={len(pairs)}",
                            f"noteCoverage={note_coverage:.3f}",
                            f"durationCoverage={duration_coverage:.3f}",
                            "onsetTolerance=40ms",
                            "pitchTolerance=35cents",
                        ],
                    },
                })
                break
    return sorted(result, key=lambda item: (
        item["sourceChannelId"], item["targetChannelId"], item["interval"]
    ))
