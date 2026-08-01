from __future__ import annotations

import itertools
import math


WINDOW_SECONDS = .350
STEP_SECONDS = .100
STRONG_SCORE = .78
TENTATIVE_SCORE = .62
RECOGNIZED_CHORD_NAMES = {
    "major triad",
    "minor triad",
    "diminished triad",
    "augmented triad",
    "dominant-seventh chord",
    "dominant seventh chord",
    "incomplete dominant-seventh chord",
    "major seventh chord",
    "minor seventh chord",
    "diminished seventh chord",
    "half-diminished seventh chord",
    "quartal trichord",
}


def _sample_overlap(start, end, note_start, note_end):
    return max(0, min(end, note_end) - max(start, note_start))


def _stability(note, start, end):
    regions = note.get("pitchRegions", [])
    weighted = 0.0
    covered = 0
    for region in regions:
        overlap = _sample_overlap(
            start,
            end,
            int(region.get("startSample", 0)),
            int(region.get("endSample", 0)),
        )
        if overlap <= 0:
            continue
        weighted += overlap * max(0.0, min(1.0, float(region.get("stability", 1.0))))
        covered += overlap
    return weighted / covered if covered else 1.0


def _chord_description(pitch_classes):
    import music21

    if len(pitch_classes) < 3 or len(pitch_classes) > 6:
        return None
    try:
        pitches = [pitch_class + 60 for pitch_class in pitch_classes]
        chord = music21.chord.Chord(pitches)
        common_name = chord.commonName or ""
        if common_name not in RECOGNIZED_CHORD_NAMES:
            return None
        root = chord.root()
        if root is None or not common_name:
            return None
        root_class = int(root.pitchClass)
        bass_class = int(chord.bass().pitchClass) if chord.bass() is not None else pitch_classes[0]
        # Derive the printable symbol from a root-position spelling. The
        # actual sounding bass is measured separately, so a sorted pitch-class
        # set must not accidentally turn Am into Am/C.
        root_position = [root_class + 60]
        root_position.extend(root_class + 60 + ((value - root_class) % 12)
                             for value in pitch_classes if value != root_class)
        normalized = music21.chord.Chord(root_position)
        try:
            symbol = music21.harmony.chordSymbolFromChord(normalized).figure or ""
        except Exception:
            symbol = ""
        # music21 intentionally declines to print a symbol for some useful
        # incomplete chords. Keep the description conservative and explicit.
        if symbol in {"Chord Symbol Cannot Be Identified", ""}:
            if "incomplete dominant-seventh" in common_name:
                symbol = f"{music21.pitch.Pitch(root_class).name}7(no5)"
            else:
                return None
        inversion = int(chord.inversion()) if chord.inversion() is not None else 0
        return {
            "chord": common_name,
            "symbol": symbol,
            "root": root_class,
            "bass": bass_class,
            "inversion": inversion,
        }
    except Exception:
        return None


def _chordified_verticalities(score, sample_rate):
    """Return observed score verticalities on the authoritative sample axis."""
    if score is None:
        return []
    try:
        import music21

        if len(score.recurse().notes) > 4000:
            return []
        chordified = score.chordify()
        result = []
        for element in chordified.recurse().getElementsByClass(music21.chord.Chord):
            pitch_classes = sorted(set(int(value) for value in element.pitchClasses))
            start = int(round(float(element.offset) * sample_rate))
            end = start + int(round(float(element.duration.quarterLength) * sample_rate))
            if end > start and pitch_classes:
                result.append({"startSample": start, "endSample": end, "pitchClasses": pitch_classes})
        return result
    except Exception:
        return []


def _chordify_agreement(pitch_classes, start, end, verticalities):
    target = set(pitch_classes)
    return max(
        (
            min(1.0, _sample_overlap(start, end, item["startSample"], item["endSample"]) / max(1, end - start))
            for item in verticalities
            if set(item["pitchClasses"]) == target
        ),
        default=0.0,
    )


def _arpeggio_support(pitch_classes, start, end, evidence):
    target = set(pitch_classes)
    best = 0.0
    for item in evidence:
        overlap = _sample_overlap(start, end, item["startSample"], item["endSample"])
        if overlap <= 0:
            continue
        classes = set(int(value) for value in item.get("pitchClasses", []))
        if not classes:
            continue
        fit = len(target & classes) / len(target | classes)
        overlap_fraction = overlap / max(1, end - start)
        regularity = max(0.0, min(1.0, float(item.get("regularity", 0.0))))
        weight = max(0.0, min(1.0, float(item.get("weight", 0.0))))
        best = max(best, overlap_fraction * regularity * weight * fit)
    return best


def _roman_for(key, segment, sample_rate):
    primary = key.get("primary") if key else None
    key_confidence = key.get("confidence", {}) if key else {}
    if not primary or key_confidence.get("certainty") != "strong":
        return None
    if primary.get("mode") not in {"major", "minor"}:
        return None
    if segment["confidence"].get("certainty") != "strong":
        return None
    if segment["endSample"] - segment["startSample"] < int(.300 * sample_rate):
        return None
    try:
        import music21

        key_object = music21.key.Key(primary["tonic"], primary["mode"])
        chord = music21.chord.Chord([pitch_class + 60 for pitch_class in segment["pitchClasses"]])
        figure = music21.roman.romanNumeralFromChord(chord, key_object).figure
        if not figure or "None" in figure or "?" in figure:
            return None
        # Applied functions and altered figures require contextual evidence
        # that is not yet available in the seconds-only analyzer.
        if "/" in figure or "#" in figure or "b" in figure:
            return None
        return {
            "figure": figure,
            "confidence": {
                "score": round(min(
                    float(key_confidence.get("score", 0.0)),
                    float(segment["confidence"].get("score", 0.0)),
                ), 6),
                "certainty": "strong",
                "method": "key-conditioned-roman-numeral",
                "evidence": [
                    "strong global key",
                    "strong chord evidence",
                    "major/minor key context",
                ],
                "limitations": ["adjacent harmonic context is unavailable"]
                if segment.get("_contextLimited") else [],
            },
        }
    except Exception:
        return None


def _candidate_sets(pitch_support):
    supported = [pitch_class for pitch_class, weight in pitch_support.items() if weight >= .025]
    if len(supported) < 3:
        return []
    # Six pitch classes is the largest useful input for the current symbolic
    # chord descriptions. Trying subsets lets a passing tone lower purity
    # instead of forcing it into the chord label.
    return [tuple(values) for size in range(3, min(6, len(supported)) + 1)
            for values in itertools.combinations(sorted(supported), size)]


def _candidate(start, end, active, evidence, sample_rate, verticalities, previous_sets=()):
    window_length = max(1, end - start)
    weighted = {}
    total_weight = 0.0
    simultaneous_weight = 0.0
    sounding = []
    for note in active:
        pitch_class = note.get("pitchClass", -1)
        if not note.get("theoryPitched", True) or pitch_class not in range(12):
            continue
        note_start = int(note["startSample"])
        note_end = note_start + int(float(note.get("duration", 0.0)) * sample_rate)
        overlap = _sample_overlap(start, end, note_start, note_end)
        if overlap <= 0:
            continue
        coverage = overlap / window_length
        channel_weight = max(0.0, min(1.0, float(note.get("channelWeight", 1.0))))
        pitch_stability = _stability(note, start, end)
        doubling_adjustment = max(0.25, min(1.0, float(note.get("doublingAdjustment", 1.0))))
        weight = coverage * channel_weight * pitch_stability * doubling_adjustment
        weighted[pitch_class] = weighted.get(pitch_class, 0.0) + weight
        total_weight += weight
        simultaneous = sum(
            _sample_overlap(start, end, note_start, other_end)
            for other_start, other_end in [(int(n["startSample"]), int(n["endSample"])) for n in active
                                            if n is not note and n.get("pitchClass", -1) in range(12)]
        )
        if simultaneous > 0:
            simultaneous_weight += weight * min(1.0, simultaneous / window_length)
        sounding.append(note)

    if total_weight <= 0:
        return None
    candidates = []
    for pitch_classes in _candidate_sets(weighted):
        description = _chord_description(pitch_classes)
        if description is None:
            continue
        chord_weight = sum(weighted.get(value, 0.0) for value in pitch_classes)
        coverage = sum(weighted.get(value, 0.0) > .025 for value in pitch_classes) / len(pitch_classes)
        purity = chord_weight / total_weight
        chordify_agreement = _chordify_agreement(pitch_classes, start, end, verticalities)
        arpeggio_support = _arpeggio_support(pitch_classes, start, end, evidence)
        root_support = weighted.get(description["root"], 0.0) / max(chord_weight, 1e-9)
        bass_pitch_class = min(
            sounding,
            key=lambda note: (float(note.get("pitch", 0)), int(note.get("startSample", 0))),
        ).get("pitchClass", description["bass"]) if sounding else description["bass"]
        bass_support = 1.0 if bass_pitch_class in pitch_classes else 0.0
        temporal_stability = 1.0 if tuple(sorted(pitch_classes)) in previous_sets else 0.0
        score = (
            .30 * purity
            + .20 * coverage
            + .15 * temporal_stability
            + .10 * root_support
            + .10 * bass_support
            + .10 * chordify_agreement
            + .05 * arpeggio_support
        )
        unexplained = max(0.0, 1.0 - purity)
        if unexplained > .20:
            score -= .15 * unexplained
        if len(previous_sets) == 0:
            score -= .10
        if len({tuple(sorted(value)) for value in previous_sets}) > 2:
            score -= .05
        score = round(max(0.0, min(1.0, score)), 6)
        item = {
            "pitchClasses": list(pitch_classes),
            "description": description,
            "bassPitchClass": int(bass_pitch_class),
            "coverage": coverage,
            "purity": purity,
            "rootSupport": root_support,
            "bassSupport": bass_support,
            "temporalStability": temporal_stability,
            "chordifyAgreement": chordify_agreement,
            "arpeggioSupport": arpeggio_support,
            "simultaneousSupport": simultaneous_weight / max(total_weight, 1e-9),
            "score": score,
        }
        candidates.append(item)
    if not candidates:
        return None
    candidates.sort(key=lambda item: (-item["score"], -item["purity"], tuple(item["pitchClasses"])))
    best = candidates[0]

    description = best["description"]
    label = description["symbol"]
    evidence_text = [
        f"purity={best['purity']:.3f}",
        f"coverage={best['coverage']:.3f}",
        f"temporalStability={best['temporalStability']:.3f}",
        f"rootSupport={best['rootSupport']:.3f}",
        f"bassSupport={best['bassSupport']:.3f}",
        f"chordifyAgreement={best['chordifyAgreement']:.3f}",
        f"arpeggioSupport={best['arpeggioSupport']:.3f}",
    ]
    return {
        "startSample": max(start, min((n["startSample"] for n in sounding), default=start)),
        "endSample": min(end, max((n["startSample"] + int(n["duration"] * sample_rate) for n in sounding), default=end)),
        "pitchClasses": best["pitchClasses"],
        "bassPitchClass": best["bassPitchClass"],
        "chord": description["chord"],
        "symbol": label,
        "roman": None,
        "romanConfidence": None,
        "alternatives": list(dict.fromkeys(
            item["description"]["symbol"]
            for item in candidates[1:]
            if item["score"] >= best["score"] - .08
            and item["description"]["symbol"] != description["symbol"]
        ))[:3],
        "confidence": {
            "score": best["score"],
            "certainty": "withheld",
            "method": "normalized-harmony-evidence",
            "evidence": evidence_text,
            "limitations": [],
        },
        "_metrics": best,
    }


def _compatible(previous, current, step_samples, loop_samples=()):
    return (
        previous["symbol"] == current["symbol"]
        and previous["pitchClasses"] == current["pitchClasses"]
        and current["startSample"] <= previous["endSample"] + step_samples
        and not any(
            previous["endSample"] <= loop <= current["startSample"]
            or previous["startSample"] < loop < previous["endSample"]
            and current["startSample"] < loop < current["endSample"]
            for loop in loop_samples
        )
    )


def _merge(segments, step_samples, loop_samples=()):
    merged = []
    for segment in segments:
        if not merged or not _compatible(merged[-1], segment, step_samples, loop_samples):
            merged.append(segment)
            continue
        previous = merged[-1]
        previous["endSample"] = max(previous["endSample"], segment["endSample"])
        previous["confidence"]["score"] = round(max(
            previous["confidence"]["score"], segment["confidence"]["score"]
        ), 6)
        previous["_stableWindows"] = previous.get("_stableWindows", 1) + 1
        previous["_metrics"]["temporalStability"] = 1.0
    return merged


def _finalize(segments, step_samples, sample_rate, key):
    finalized = []
    for segment in segments:
        metrics = segment.pop("_metrics", {})
        stable_windows = segment.pop("_stableWindows", 1)
        duration = max(0, segment["endSample"] - segment["startSample"]) / max(1, sample_rate)
        score = float(segment["confidence"]["score"])
        if score >= STRONG_SCORE and metrics.get("purity", 0) >= .75 and duration >= .300 and stable_windows >= 2:
            certainty = "strong"
        elif score >= TENTATIVE_SCORE and duration >= .200:
            certainty = "tentative"
        else:
            certainty = "withheld"
        if certainty == "withheld":
            continue
        segment["confidence"]["certainty"] = certainty
        if certainty == "tentative":
            segment["confidence"]["limitations"].append("candidate did not pass the strong harmony gate")
        roman = _roman_for(key, segment, sample_rate)
        if roman is not None:
            segment["roman"] = roman["figure"]
            segment["romanConfidence"] = roman["confidence"]
        finalized.append(segment)
    return finalized


def analyze_harmony(
    score,
    notes,
    key,
    detail="standard",
    arpeggio_evidence=None,
    sample_rate=None,
    loops=None,
):
    if detail == "minimal" or not notes:
        return []
    theory_notes = [n for n in notes if n.get("theoryPitched", True)
                    and n.get("pitchClass", -1) in range(12)
                    and (detail == "full" or n.get("channelKind") != "fm3-operator")]
    if not theory_notes:
        return []
    if sample_rate is None:
        ratios = [
            (n["endSample"] - n["startSample"]) / n["duration"]
            for n in theory_notes if n.get("duration", 0) > 0
        ]
        sample_rate = max(1, int(round(sum(ratios) / len(ratios)))) if ratios else 1
    sample_rate = max(1, int(sample_rate))
    min_sample = min(n["startSample"] for n in theory_notes)
    max_sample = max(n["startSample"] + int(n["duration"] * sample_rate) for n in theory_notes)
    window_samples = max(1, int(round(WINDOW_SECONDS * sample_rate)))
    step_samples = max(1, int(round(STEP_SECONDS * sample_rate)))
    evidence = [
        item for item in (arpeggio_evidence or [])
        if item.get("endSample", 0) > item.get("startSample", 0)
    ]
    verticalities = _chordified_verticalities(score, sample_rate)
    segments = []
    ordered_notes = sorted(theory_notes, key=lambda item: (item["startSample"], item.get("id", "")))
    active_by_index = {}
    active_end_heap = []
    import heapq

    next_note = 0
    for start in range(min_sample, max_sample, step_samples):
        end = min(max_sample, start + window_samples)
        while next_note < len(ordered_notes) and ordered_notes[next_note]["startSample"] < end:
            note = ordered_notes[next_note]
            note_end = note["startSample"] + int(note["duration"] * sample_rate)
            active_by_index[next_note] = note
            heapq.heappush(active_end_heap, (note_end, next_note))
            next_note += 1
        while active_end_heap and active_end_heap[0][0] <= start:
            _, index = heapq.heappop(active_end_heap)
            active_by_index.pop(index, None)
        active = list(active_by_index.values())
        previous_sets = tuple(tuple(item["pitchClasses"]) for item in segments[-2:])
        candidate = _candidate(start, end, active, evidence, sample_rate, verticalities, previous_sets)
        if candidate is not None:
            segments.append(candidate)
    loop_samples = tuple(sorted({
        int(item.get("sample", item.get("startSample", -1)))
        for item in (loops or [])
        if int(item.get("sample", item.get("startSample", -1))) > min_sample
    }))
    merged = _merge(segments, step_samples, loop_samples)
    return sorted(
        _finalize(merged, step_samples, sample_rate, key),
        key=lambda item: (item["startSample"], item["endSample"], item["pitchClasses"]),
    )
