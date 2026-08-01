from __future__ import annotations

import hashlib


def _ratio(values):
    if not values:
        return ()
    base = min(value for value in values if value > 0) if any(value > 0 for value in values) else 1
    return tuple(round(value / base, 6) for value in values)


def _fingerprint(sequence, start, length):
    window = sequence[start:start + length]
    pitches = [float(note["pitch"]) for note in window]
    durations = [max(1, int(note["endSample"] - note["startSample"])) for note in window]
    gaps = [
        max(0, int(window[index + 1]["startSample"] - window[index]["endSample"]))
        for index in range(length - 1)
    ]
    intervals = tuple(round(pitches[index + 1] - pitches[index], 4) for index in range(length - 1))
    normalized_durations = _ratio(durations)
    normalized_gaps = _ratio(gaps) if any(gaps) else tuple(0 for _ in gaps)
    return intervals, normalized_durations, normalized_gaps


def _fingerprint_id(fingerprint):
    payload = repr(fingerprint).encode("utf-8")
    return "M" + hashlib.sha256(payload).hexdigest()[:10]


def _loop_periods(loops):
    boundaries = sorted({
        int(item.get("sample", item.get("startSample", 0)))
        for item in loops or []
        if item.get("sample", item.get("startSample", 0)) is not None
    })
    periods = {
        right - left for left, right in zip(boundaries, boundaries[1:]) if right > left
    }
    for item in loops or []:
        start = item.get("startSample")
        end = item.get("endSample")
        if start is not None and end is not None and end > start:
            periods.add(int(end - start))
    return boundaries, sorted(periods)


def _crosses_boundary(sequence, start, length, boundaries):
    first = sequence[start]["startSample"]
    last = sequence[start + length - 1]["endSample"]
    return any(first < boundary < last for boundary in boundaries)


def _is_trivial(fingerprint, sequence, start, length):
    intervals, _, _ = fingerprint
    pitches = [round(float(note["pitch"]) % 12, 4) for note in sequence[start:start + length]]
    if len(set(pitches)) <= 2:
        return True
    # A straight, stepwise scale fragment is common texture, not a distinctive
    # motif. Preserve larger patterns with leaps or direction changes.
    return bool(intervals) and all(0 < abs(value) <= 2 for value in intervals) and (
        all(value > 0 for value in intervals) or all(value < 0 for value in intervals)
    )


def analyze_motifs(notes, detail="standard", loops=None):
    if detail == "minimal":
        return []
    output = []
    grouped = getattr(notes, "by_channel", None)
    for channel_id in sorted(grouped if grouped is not None else {n["channelId"] for n in notes}):
        sequence = sorted(
            [n for n in (grouped[channel_id] if grouped is not None else notes) if n["channelId"] == channel_id
             and n.get("theoryPitched", True)
             and n.get("pitchClass", -1) in range(12)],
            key=lambda n: (n.get("startSample", 0), n.get("id", "")),
        )
        if len(sequence) < 3:
            continue
        boundaries, periods = _loop_periods(loops)
        positive_gaps = [
            max(0, sequence[index + 1]["startSample"] - sequence[index]["endSample"])
            for index in range(len(sequence) - 1)
        ]
        durations = [max(1, n["endSample"] - n["startSample"]) for n in sequence]
        phrase_gap = max(2 * max(durations), 4 * (sorted(positive_gaps)[len(positive_gaps) // 2]
                                                   if positive_gaps else 0))
        for index, gap in enumerate(positive_gaps):
            if gap > phrase_gap:
                boundaries.append(sequence[index + 1]["startSample"])
        boundaries = sorted(set(boundaries))

        for length in range(min(12, len(sequence)), 2, -1):
            buckets = {}
            fingerprints = {}
            for start in range(0, len(sequence) - length + 1):
                if _crosses_boundary(sequence, start, length, boundaries):
                    continue
                fingerprint = fingerprints.setdefault(start, _fingerprint(sequence, start, length))
                if _is_trivial(fingerprint, sequence, start, length):
                    continue
                buckets.setdefault(fingerprint, []).append(start)
            selected = []
            for fingerprint, starts in sorted(buckets.items(), key=lambda item: item[1][0]):
                if len(starts) < 2:
                    continue
                non_loop_pairs = []
                for left_index, left in enumerate(starts):
                    for right in starts[left_index + 1:]:
                        delta = sequence[right]["startSample"] - sequence[left]["startSample"]
                        loop_only = any(period > 0 and abs(delta - period) <= max(1, period // 100)
                                        for period in periods)
                        if not loop_only:
                            non_loop_pairs.append((left, right))
                if not non_loop_pairs:
                    continue
                selected.append((fingerprint, starts, non_loop_pairs[0]))
            if not selected:
                continue
            for fingerprint, starts, pair in selected:
                motif_id = _fingerprint_id(fingerprint)
                left, right = pair
                transposition = int(round(sequence[right]["pitch"] - sequence[left]["pitch"]))
                occurrences = [left, right]
                for occurrence in occurrences:
                    output.append({
                        "motifId": motif_id,
                        "channelId": channel_id,
                        "startSample": sequence[occurrence]["startSample"],
                        "endSample": sequence[occurrence + length - 1]["endSample"],
                        "transposition": 0 if occurrence == left else transposition,
                        "similarity": 1.0,
                    })
            # Longest exact matches are the useful definitions. Do not emit
            # every nested fingerprint from the same phrase.
            if output:
                break
        if output:
            break
    return sorted(output[:2000], key=lambda item: (item["startSample"], item["endSample"], item["motifId"]))
