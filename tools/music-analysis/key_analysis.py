"""Conservative global-key analysis for the symbolic worker.

The three music21 profile analysers contribute one primary winner each. Their
raw correlation coefficients remain evidence, never calibrated probabilities.
Alternative interpretations are retained separately and never become votes.
"""
from __future__ import annotations

import math
from statistics import median


PROFILE_METHODS = ("KrumhanslSchmuckler", "TemperleyKostkaPayne", "BellmanBudge")


def _pitched(notes):
    return [
        note for note in notes
        if note.get("theoryPitched", note.get("pitchClass", -1) in range(12))
        and note.get("pitchClass", -1) in range(12)
    ]


def _candidate_key(tonic, mode):
    return f"{tonic} {mode}"


def _correlation(key_object):
    value = getattr(key_object, "correlationCoefficient", 0.0)
    try:
        value = float(value or 0.0)
    except (TypeError, ValueError):
        value = 0.0
    return value if math.isfinite(value) else 0.0


def _whole_tone_pitch_classes(classes):
    values = set(classes)
    return values in ({0, 2, 4, 6, 8, 10}, {1, 3, 5, 7, 9, 11})


def _is_highly_chromatic(classes):
    if len(classes) >= 9 or _whole_tone_pitch_classes(classes):
        return True
    return len(classes) >= 8 and len(classes) > 0


def _confidence_score(median_correlation, votes, note_count, class_count, lead):
    """Return an evidence index, not a probability.

    The terms intentionally expose the evidence used by the certainty gate:
    correlation strength, profile agreement, note support, pitch-class support,
    and separation from the next primary-winner aggregate.
    """
    correlation_strength = max(0.0, min(1.0, (median_correlation - 0.50) / 0.50))
    agreement = min(1.0, votes / 3.0)
    note_support = min(1.0, note_count / 5.0)
    class_support = min(1.0, class_count / 3.0)
    lead_support = 1.0 if math.isinf(lead) else max(0.0, min(1.0, lead / 0.08))
    return round(max(0.0, min(
        1.0,
        0.35
        + 0.30 * correlation_strength
        + 0.20 * agreement
        + 0.10 * note_support
        + 0.05 * class_support
        + 0.10 * lead_support,
    )), 6)


def _candidate_object(music21, candidate, score):
    tonic, mode = candidate
    return {
        "tonicPitchClass": music21.pitch.Pitch(tonic).pitchClass,
        "tonic": tonic,
        "mode": mode,
        "confidence": score,
    }


def _score_for_notes(music21, notes):
    score = music21.stream.Score()
    part = music21.stream.Part(id="analysis-window")
    score.insert(0, part)
    for note in sorted(notes, key=lambda value: (value.get("offset", 0), value.get("id", ""))):
        try:
            item = music21.note.Note(round(float(note["pitch"])))
            item.offset = float(note.get("offset", 0.0))
            item.duration.quarterLength = max(.001, float(note.get("duration", .001)))
            part.insert(item)
        except Exception:
            continue
    return score


def analyze_key(score, notes, detail="standard"):
    import music21

    pitched = _pitched([
        note for note in notes
        if detail == "full" or note.get("channelKind") != "fm3-operator"
    ])
    classes = sorted({int(note["pitchClass"]) for note in pitched})
    note_count = len(pitched)
    class_count = len(classes)
    chromatic = _is_highly_chromatic(classes)

    if not pitched:
        return {
            "primary": None,
            "alternatives": [],
            "methods": [],
            "winnerVotes": {},
            "winnerCorrelations": {},
            "alternativeCorrelations": {},
            "confidence": {
                "score": 0,
                "certainty": "withheld",
                "method": "key-profile-consensus",
                "limitations": ["no pitched notes"],
            },
        }

    if score is None:
        score = _score_for_notes(music21, pitched)

    methods = []
    key_objects = []
    for name in PROFILE_METHODS:
        try:
            key_object = score.analyze(name)
            if key_object.tonic is None or not key_object.mode:
                continue
            correlation = _correlation(key_object)
            tonic = key_object.tonic.name
            mode = key_object.mode
            methods.append({
                "name": name,
                "result": str(key_object),
                "correlation": round(correlation, 6),
                "tonic": tonic,
                "mode": mode,
                "primary": True,
            })
            key_objects.append(key_object)
        except Exception:
            continue

    winner_correlations = {}
    winner_votes = {}
    candidates = {}
    for method in methods:
        candidate = (method["tonic"], method["mode"])
        label = _candidate_key(*candidate)
        winner_votes[label] = winner_votes.get(label, 0) + 1
        winner_correlations.setdefault(label, []).append(method["correlation"])
        candidates.setdefault(candidate, []).append(method["correlation"])

    alternative_correlations = {}
    for key_object in key_objects:
        for alternate in getattr(key_object, "alternateInterpretations", [])[:8]:
            if alternate.tonic is None or not alternate.mode:
                continue
            candidate = (alternate.tonic.name, alternate.mode)
            label = _candidate_key(*candidate)
            alternative_correlations.setdefault(label, []).append(round(_correlation(alternate), 6))

    if not candidates:
        return {
            "primary": None,
            "alternatives": [],
            "methods": methods,
            "winnerVotes": winner_votes,
            "winnerCorrelations": winner_correlations,
            "alternativeCorrelations": alternative_correlations,
            "confidence": {
                "score": 0,
                "certainty": "withheld",
                "method": "key-profile-consensus",
                "limitations": ["no profile returned a primary candidate"],
            },
        }

    aggregates = {
        candidate: float(median(values))
        for candidate, values in candidates.items()
    }
    ordered = sorted(
        candidates,
        key=lambda candidate: (
            -aggregates[candidate],
            -len(candidates[candidate]),
            candidate[0],
            candidate[1],
        ),
    )
    best = ordered[0]
    best_aggregate = aggregates[best]
    second_aggregate = aggregates[ordered[1]] if len(ordered) > 1 else None
    lead = float("inf") if second_aggregate is None else best_aggregate - second_aggregate
    agreement = len(candidates[best])
    score = _confidence_score(best_aggregate, agreement, note_count, class_count, lead)

    strong = (
        agreement >= 2
        and best_aggregate >= 0.65
        and lead >= 0.08
        and note_count >= 5
        and class_count >= 3
        and not chromatic
    )
    tentative = (
        best_aggregate >= 0.50
        and note_count >= 5
        and class_count >= 3
        and not chromatic
        and (len(ordered) == 1 or lead >= 0.08)
    )
    certainty = "strong" if strong else "tentative" if tentative else "withheld"

    limitations = []
    if note_count < 5:
        limitations.append("fewer than five pitched notes")
    if class_count < 3:
        limitations.append("fewer than three pitch classes")
    if chromatic:
        limitations.append("highly chromatic or whole-tone pitch-class material")
    if len(ordered) > 1 and lead < 0.08:
        limitations.append("primary profile winners disagree without a clear lead")
    if best_aggregate < 0.50:
        limitations.append("best raw profile correlation is below 0.50")

    confidence = {
        "score": score,
        "certainty": certainty,
        "method": "key-profile-consensus",
        "evidence": [
            f"{agreement} of {len(methods)} primary profile winners agree",
            f"median winning raw correlation {best_aggregate:.6f}",
            f"lead over next primary-winner aggregate {lead:.6f}" if not math.isinf(lead)
            else "no competing primary-winner aggregate",
            f"{note_count} pitched notes across {class_count} pitch classes",
        ],
        "limitations": limitations,
    }

    alternatives = []
    seen = {best}
    for candidate in ordered[1:]:
        alternatives.append(_candidate_object(
            music21,
            candidate,
            _confidence_score(
                aggregates[candidate],
                len(candidates[candidate]),
                note_count,
                class_count,
                aggregates[candidate] - best_aggregate,
            ),
        ))
        seen.add(candidate)
    for label, values in sorted(alternative_correlations.items(), key=lambda item: (-max(item[1]), item[0])):
        tonic, mode = label.rsplit(" ", 1)
        candidate = (tonic, mode)
        if candidate in seen or candidate == best:
            continue
        alternatives.append(_candidate_object(music21, candidate, round(max(values), 6)))
        seen.add(candidate)
    alternatives = alternatives[:8]

    return {
        "primary": _candidate_object(music21, best, score) if certainty != "withheld" else None,
        "alternatives": alternatives,
        "methods": methods,
        "winnerVotes": winner_votes,
        "winnerCorrelations": winner_correlations,
        "alternativeCorrelations": alternative_correlations,
        "confidence": confidence,
    }
