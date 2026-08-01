def _library_channel_features(score, channel_id, interval_count):
    if score is None:
        return {}
    try:
        from music21.analysis.discrete import Ambitus, MelodicIntervalDiversity

        part = next((part for part in score.parts if part.id == channel_id), None)
        if part is None:
            return {}
        span = Ambitus().getPitchSpan(part)
        unique_intervals = len(
            MelodicIntervalDiversity().countMelodicIntervals(
                part, ignoreDirection=True
            )
        )
        return {
            "ambitus": span[1].midi - span[0].midi if span else None,
            "intervalDiversity": min(1.0, unique_intervals / interval_count) if interval_count else 0.0,
        }
    except Exception:
        return {}


def _j_symbolic_features(score):
    empty = {
        "pitchClassDistribution": [0.0] * 12,
        "melodicIntervalHistogram": [0.0] * 41,
        "noteDensity": 0.0,
        "averageNoteDuration": 0.0,
        "pitchVariety": 0.0,
        "pitchClassVariety": 0.0,
        "mostCommonPitchClassPrevalence": 0.0,
        "relativeStrengthOfTopPitchClasses": 0.0,
    }
    if score is None:
        return empty, []
    try:
        if len(score.recurse().notes) > 2000:
            return empty, ["jSymbolic extractors skipped above the 2000-note feature budget"]
    except Exception:
        pass
    try:
        from music21.features import jSymbolic

        extractors = {
            "pitchClassDistribution": jSymbolic.PitchClassDistributionFeature,
            "melodicIntervalHistogram": jSymbolic.MelodicIntervalHistogramFeature,
            "noteDensity": jSymbolic.NoteDensityFeature,
            "averageNoteDuration": jSymbolic.AverageNoteDurationFeature,
            "pitchVariety": jSymbolic.PitchVarietyFeature,
            "pitchClassVariety": jSymbolic.PitchClassVarietyFeature,
            "mostCommonPitchClassPrevalence": jSymbolic.MostCommonPitchClassPrevalenceFeature,
            "relativeStrengthOfTopPitchClasses": jSymbolic.RelativeStrengthOfTopPitchClassesFeature,
        }
        result = {}
        for name, extractor in extractors.items():
            vector = extractor(score).extract().vector
            result[name] = [float(value) for value in vector] if len(vector) != 1 else float(vector[0])
        return {**empty, **result}, []
    except Exception:
        return empty, ["jSymbolic extractors failed; basic feature fallbacks retained"]


def analyze_features(notes, score=None, detail="standard"):
    channels = []
    grouped = getattr(notes, "by_channel", None)
    for channel_id in sorted(grouped if grouped is not None else {n["channelId"] for n in notes}):
        channel_notes = list(grouped[channel_id]) if grouped is not None else sorted(
            [n for n in notes if n["channelId"] == channel_id],
            key=lambda n: (n.get("offset", 0), n.get("id", "")),
        )
        pitches = [n["pitch"] for n in channel_notes if n.get("theoryPitched", False)]
        if not pitches:
            continue
        theory = [
            n for n in channel_notes
            if n.get("theoryPitched", n.get("pitchClass", -1) in range(12))
            and n.get("pitchClass", -1) in range(12)
        ]
        intervals = [
            int(round(pitches[index] - pitches[index - 1]))
            for index in range(1, len(pitches))
        ]
        distribution = [
            sum(1 for n in theory if n["pitchClass"] == pitch_class) / len(theory)
            if theory else 0.0
            for pitch_class in range(12)
        ]
        steps = sum(abs(interval) <= 2 and interval != 0 for interval in intervals)
        repetitions = sum(interval == 0 for interval in intervals)
        library = _library_channel_features(score, channel_id, len(intervals))
        ambitus = library.get("ambitus")
        if ambitus is None:
            ambitus = max(pitches) - min(pitches)
        channels.append({
            "channelId": channel_id,
            "noteCount": len(channel_notes),
            "minMidiPitch": min(pitches),
            "maxMidiPitch": max(pitches),
            "ambitus": ambitus,
            "medianMidiPitch": sorted(pitches)[len(pitches) // 2],
            "stepRatio": steps / len(intervals) if intervals else 0.0,
            "repetitionRatio": repetitions / len(intervals) if intervals else 0.0,
            "directedIntervals": intervals,
            "undirectedIntervals": [abs(interval) for interval in intervals],
            "intervalDiversity": library.get(
                "intervalDiversity", len(set(intervals)) / len(intervals) if intervals else 0.0
            ),
            "pitchClassDistribution": distribution,
        })

    all_pitches = [n["pitch"] for n in notes if n.get("theoryPitched", False)]
    theory = [
        n for n in notes
        if n.get("theoryPitched", n.get("pitchClass", -1) in range(12))
        and n.get("pitchClass", -1) in range(12)
    ]
    weights = [max(0.0, float(n.get("channelWeight", 1.0))) for n in theory]
    total_weight = sum(weights)
    distribution = [
        sum(weight for n, weight in zip(theory, weights) if n["pitchClass"] == pitch_class) / total_weight
        if total_weight else 0.0
        for pitch_class in range(12)
    ]
    classes = [n["pitchClass"] for n in theory]
    start = min((n.get("offset", 0.0) for n in notes), default=0.0)
    end = max((n.get("offset", 0.0) + n.get("duration", 0.0) for n in notes), default=0.0)
    span = end - start
    j_symbolic, j_symbolic_warnings = _j_symbolic_features(score) if detail != "minimal" else (
        _j_symbolic_features(None)[0], []
    )
    return {
        "noteCount": len(notes),
        "minPitchClass": min(classes) if classes else -1,
        "maxPitchClass": max(classes) if classes else -1,
        "minMidiPitch": min(all_pitches) if all_pitches else 0.0,
        "maxMidiPitch": max(all_pitches) if all_pitches else 0.0,
        "ambitus": max(all_pitches) - min(all_pitches) if all_pitches else 0.0,
        "averageNoteDuration": sum(n.get("duration", 0.0) for n in notes) / len(notes) if notes else 0.0,
        "noteDensity": len(notes) / span if span > 0 else 0.0,
        "pitchClassDistribution": distribution,
        "jSymbolic": j_symbolic,
        "_warnings": j_symbolic_warnings,
        "channels": channels,
    }
