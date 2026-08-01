"""Conservative repeated-note and neutral-boundary candidates."""
from __future__ import annotations


def _pitched(notes):
    return [n for n in notes if n.get("theoryPitched", True)
            and n.get("pitchClass", -1) in range(12)]


def _union_length(intervals):
    total = 0
    current_start = current_end = None
    for start, end in sorted(intervals):
        if end <= start:
            continue
        if current_end is None:
            current_start, current_end = start, end
        elif start <= current_end:
            current_end = max(current_end, end)
        else:
            total += current_end - current_start
            current_start, current_end = start, end
    if current_end is not None:
        total += current_end - current_start
    return total


def _harmonic_signatures(notes, pitch_class, start, end):
    onsets = sorted({n["startSample"] for n in notes if start <= n["startSample"] < end})
    signatures = []
    for onset in onsets:
        active = {
            int(n["pitchClass"])
            for n in notes
            if n.get("theoryPitched", True)
            and n.get("pitchClass", -1) in range(12)
            and n["startSample"] <= onset < n["endSample"]
            and int(n["pitchClass"]) != pitch_class
        }
        if active and (not signatures or active != signatures[-1]):
            signatures.append(active)
    return signatures


def analyze_pedal_tones(notes, sample_rate):
    result = []
    all_pitched = _pitched(notes)
    for channel_id in sorted({n["channelId"] for n in notes}):
        sequence = sorted(
            _pitched([n for n in notes if n["channelId"] == channel_id]),
            key=lambda n: (n["startSample"], n.get("id", "")),
        )
        if len(sequence) < 2:
            continue
        by_pc = {}
        for note in sequence:
            by_pc.setdefault(int(note["pitchClass"]), []).append(note)
        for pitch_class, members in sorted(by_pc.items()):
            start = min(n["startSample"] for n in members)
            end = max(n["endSample"] for n in members)
            duration = end - start
            if duration < 2 * sample_rate:
                continue
            covered = _union_length((n["startSample"], n["endSample"]) for n in members)
            coverage = min(1.0, covered / max(1, duration))
            long_notes = [n for n in members if n["endSample"] - n["startSample"] >= 2 * sample_rate]
            if coverage < .40 or (len(members) < 2 and not long_notes):
                continue
            signatures = _harmonic_signatures(all_pitched, pitch_class, start, end)
            if len(signatures) < 3:
                continue
            score = min(.95, .55 + .25 * coverage + .05 * min(3, len(signatures) - 1))
            result.append({
                "channelId": channel_id,
                "pitchClass": pitch_class,
                "startSample": start,
                "endSample": end,
                "coverage": round(coverage, 6),
                "confidence": {
                    "score": round(score, 6),
                    "certainty": "strong" if score >= .78 else "tentative",
                    "method": "cross-harmony-pedal-evidence",
                    "evidence": [
                        f"harmonicChanges={len(signatures) - 1}",
                        f"coverage={coverage:.3f}",
                        f"attacks={len(members)}",
                    ],
                },
            })
    return sorted(result, key=lambda x: (x["startSample"], x["channelId"], x["pitchClass"]))


def _loop_info(loops):
    boundaries = sorted({
        int(item.get("sample", item.get("startSample", 0)))
        for item in loops or []
        if item.get("sample", item.get("startSample", 0)) is not None
    })
    periods = {
        right - left for left, right in zip(boundaries, boundaries[1:]) if right > left
    }
    for item in loops or []:
        start, end = item.get("startSample"), item.get("endSample")
        if start is not None and end is not None and end > start:
            periods.add(int(end - start))
    return boundaries, sorted(periods)


def _ostinato_fingerprint(sequence, start, length):
    window = sequence[start:start + length]
    durations = [max(1, n["endSample"] - n["startSample"]) for n in window]
    inter_onsets = [
        max(1, window[index + 1]["startSample"] - window[index]["startSample"])
        for index in range(length - 1)
    ]
    base_duration = min(durations)
    base_ioi = min(inter_onsets) if inter_onsets else 1
    return (
        tuple(int(n["pitchClass"]) for n in window),
        tuple(round(value / base_duration, 6) for value in durations),
        tuple(round(value / base_ioi, 6) for value in inter_onsets),
    )


def analyze_ostinatos(notes, detail="standard", loops=None, sample_rate=1):
    if detail == "minimal":
        return []
    result = []
    boundaries, periods = _loop_info(loops)
    for channel_id in sorted({n["channelId"] for n in notes}):
        sequence = sorted(
            _pitched([n for n in notes if n["channelId"] == channel_id]),
            key=lambda n: (n["startSample"], n.get("id", "")),
        )
        for length in range(2, min(16, len(sequence) // 3) + 1):
            buckets = {}
            for start in range(len(sequence) - length + 1):
                if any(sequence[start]["startSample"] < boundary < sequence[start + length - 1]["endSample"]
                       for boundary in boundaries):
                    continue
                fingerprint = _ostinato_fingerprint(sequence, start, length)
                if len(set(fingerprint[0])) <= 1:
                    continue
                buckets.setdefault(fingerprint, []).append(start)
            found = None
            for fingerprint, starts in sorted(buckets.items(), key=lambda item: item[1][0]):
                if len(starts) < 3:
                    continue
                non_loop = [
                    start for start in starts
                    if not any(
                        period > 0
                        and any(abs(sequence[start]["startSample"] - sequence[other]["startSample"] - period)
                                <= max(1, period // 100) for other in starts if other < start)
                        for period in periods
                    )
                ]
                if len(non_loop) < 3:
                    continue
                first = non_loop[0]
                last = non_loop[-1] + length - 1
                if sequence[last]["endSample"] - sequence[first]["startSample"] < 2 * sample_rate:
                    continue
                found = (fingerprint, non_loop, first, last)
                break
            if found:
                fingerprint, starts, first, last = found
                gaps = [sequence[starts[index + 1]]["startSample"] - sequence[starts[index]]["startSample"]
                        for index in range(len(starts) - 1)]
                average = sum(gaps) / len(gaps)
                regularity = max(0.0, min(1.0, 1 - (max(gaps) - min(gaps)) / max(1, average))) if gaps else 0
                result.append({
                    "channelId": channel_id,
                    "pitchClasses": list(fingerprint[0]),
                    "occurrences": len(starts),
                    "startSample": sequence[first]["startSample"],
                    "endSample": sequence[last]["endSample"],
                    "regularity": round(regularity, 6),
                    "confidence": {
                        "score": round(.70 + .20 * regularity, 6),
                        "certainty": "strong" if regularity >= .70 else "tentative",
                        "method": "exact-pitch-and-rhythm-cycle",
                        "evidence": [
                            f"occurrences={len(starts)}",
                            f"rhythmRegularity={regularity:.3f}",
                        ],
                    },
                })
                break
        if result:
            break
    return sorted(result, key=lambda x: (x["startSample"], x["channelId"], x["pitchClasses"]))


def analyze_boundaries(notes, sample_rate, loops=None):
    """Return only neutral activity, loop, and texture-change candidates."""
    result = []
    for loop in loops or []:
        sample = loop.get("sample", loop.get("startSample"))
        if sample is None:
            continue
        kind = str(loop.get("kind", "start")).lower()
        result.append({
            "sample": int(sample),
            "kind": "loop-restart" if kind in {"restart", "repeat"} else "loop-start",
            "confidence": {"score": 1.0, "certainty": "observed", "method": "fmp-loop-marker"},
        })

    active = sorted(
        (int(note.get("startSample", 0)), int(note.get("endSample", 0)))
        for note in notes
        if int(note.get("endSample", 0)) > int(note.get("startSample", 0))
    )
    if active:
        merged_end = active[0][1]
        gap_threshold = max(1, int(round(float(sample_rate) * .75)))
        loop_samples = {item["sample"] for item in result}
        for start, end in active[1:]:
            if start - merged_end >= gap_threshold and start not in loop_samples:
                result.append({
                    "sample": start,
                    "kind": "activity-gap",
                    "confidence": {
                        "score": .65,
                        "certainty": "tentative",
                        "method": "activity-gap",
                        "limitations": ["no beat map"],
                    },
                })
            merged_end = max(merged_end, end)

    # A large change in the set of active pitch classes is neutral evidence;
    # it is never named as intro, verse, chorus, bridge, or cadence.
    starts = sorted({int(note["startSample"]) for note in notes})
    for previous, current in zip(starts, starts[1:]):
        before = {int(n["pitchClass"]) for n in notes if n.get("theoryPitched", True)
                  and n.get("pitchClass", -1) in range(12)
                  and n["startSample"] <= previous < n["endSample"]}
        after = {int(n["pitchClass"]) for n in notes if n.get("theoryPitched", True)
                 and n.get("pitchClass", -1) in range(12)
                 and n["startSample"] <= current < n["endSample"]}
        union = before | after
        if union and (len(before) >= 2 or len(after) >= 2) and len(before ^ after) / len(union) >= .75:
            result.append({
                "sample": current,
                "kind": "large-texture-change",
                "confidence": {
                    "score": .60,
                    "certainty": "tentative",
                    "method": "active-pitch-set-change",
                    "limitations": ["no beat map"],
                },
            })
    return sorted(result, key=lambda item: (item["sample"], item["kind"]))
