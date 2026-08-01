from __future__ import annotations


def _sample_at_beat(beats, target):
    if not beats:
        return None
    if target <= beats[0]["beat"]:
        return beats[0]["sample"]
    for left, right in zip(beats, beats[1:]):
        if left["beat"] <= target <= right["beat"]:
            span = right["beat"] - left["beat"]
            fraction = (target - left["beat"]) / span if span else 0
            return int(round(left["sample"] + fraction * (right["sample"] - left["sample"])))
    return beats[-1]["sample"]


def analyze_local_keys(notes, timing, detail="standard"):
    if detail == "minimal" or timing.get("timingMode") != "beats":
        return []
    beats = sorted(
        [{"sample": int(item.get("sample", item.get("samplePosition", 0))),
          "beat": float(item.get("beat", item.get("beatIndex", 0)))}
         for item in timing.get("beats", [])],
        key=lambda item: (item["beat"], item["sample"]),
    )
    if len(beats) < 17:
        return []
    from key_analysis import analyze_key

    observations = []
    for start in beats:
        start_beat = start["beat"]
        end_beat = start_beat + 8
        start_sample = _sample_at_beat(beats, start_beat)
        end_sample = _sample_at_beat(beats, end_beat)
        if start_sample is None or end_sample is None or end_sample <= start_sample:
            continue
        if hasattr(notes, "notes_in_window"):
            window = notes.notes_in_window(start_sample, end_sample)
            window = [
                note for note in window
                if note.get("theoryPitched", True)
                and note.get("pitchClass", -1) in range(12)
            ]
        else:
            window = [
                note for note in notes
                if note.get("theoryPitched", True)
                and note.get("pitchClass", -1) in range(12)
                and note["startSample"] < end_sample
                and note["endSample"] > start_sample
            ]
        if not window:
            continue
        result = analyze_key(None, window, detail)
        primary = result.get("primary")
        certainty = result.get("confidence", {}).get("certainty")
        if primary is None or certainty not in {"strong", "tentative"}:
            observations.append(None)
            continue
        observations.append({
            "startSample": start_sample,
            "endSample": end_sample,
            "startBeat": start_beat,
            "endBeat": end_beat,
            "label": (primary["tonic"], primary["mode"]),
            "confidence": result["confidence"],
        })

    regions = []
    index = 0
    while index < len(observations):
        current = observations[index]
        if current is None:
            index += 1
            continue
        end = index + 1
        while end < len(observations) and observations[end] is not None \
                and observations[end]["label"] == current["label"]:
            end += 1
        run = [item for item in observations[index:end] if item is not None]
        if len(run) >= 2 and run[-1]["endBeat"] - run[0]["startBeat"] >= 4:
            regions.append({
                "startSample": run[0]["startSample"],
                "endSample": run[-1]["endSample"],
                "tonic": run[0]["label"][0],
                "mode": run[0]["label"][1],
                "confidence": max(run, key=lambda item: item["confidence"]["score"])["confidence"],
                "startBeat": run[0]["startBeat"],
                "endBeat": run[-1]["endBeat"],
            })
        index = end

    # A one-beat step can produce overlapping candidate runs. Keep only a
    # region that wins for two windows and suppress short oscillations.
    stable = []
    for region in regions:
        if stable and region["tonic"] == stable[-1]["tonic"] and region["mode"] == stable[-1]["mode"]:
            stable[-1]["endSample"] = max(stable[-1]["endSample"], region["endSample"])
            stable[-1]["endBeat"] = max(stable[-1]["endBeat"], region["endBeat"])
            if region["confidence"]["score"] > stable[-1]["confidence"]["score"]:
                stable[-1]["confidence"] = region["confidence"]
            continue
        if stable:
            previous = stable[-1]
            if region["confidence"]["score"] <= previous["confidence"]["score"] + .05:
                continue
        stable.append(region)
    for region in stable:
        region.pop("startBeat", None)
        region.pop("endBeat", None)
    return stable
