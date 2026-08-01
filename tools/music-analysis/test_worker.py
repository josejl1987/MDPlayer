import json, sys
from pathlib import Path
import pytest
sys.path.insert(0, str(Path(__file__).parent))

music21 = pytest.importorskip("music21")
from mdplayer_music_analysis import run
from jsonschema import validate

def timeline():
    return {"schemaVersion":1,"sampleRate":1000,"startSample":0,"endSample":3000,"channels":[{"id":"a","kind":"fm","notes":[{"id":"a1","startSample":0,"endSample":1000,"structuralMidiPitch":60,"isPitched":True},{"id":"a2","startSample":1000,"endSample":2000,"structuralMidiPitch":64,"isPitched":True},{"id":"a3","startSample":2000,"endSample":3000,"structuralMidiPitch":67,"isPitched":True}]}]}

def test_deterministic_and_warning():
    a=run(timeline(),"standard"); b=run(timeline(),"standard")
    assert a == b
    assert any(w["code"] == "TIMING_UNAVAILABLE" for w in a["warnings"])
    assert len(a["global"]["pitch"]["pitchClassDistribution"]) == 12
    assert len(a["global"]["pitch"]["jSymbolic"]["pitchClassDistribution"]) == 12
    assert a["global"]["pitch"]["jSymbolic"]["melodicIntervalHistogram"]
    assert set(("minMidiPitch", "maxMidiPitch", "medianMidiPitch", "stepRatio", "repetitionRatio", "directedIntervals", "undirectedIntervals", "pitchClassDistribution")).issubset(a["channels"][0])

def test_minimal_omits_harmony_and_motifs():
    result=run(timeline(),"minimal")
    assert result["harmony"] == [] and result["motifs"] == []
    assert result["relationships"] == []
    assert result["pedalTones"] == []
    assert result["ostinatos"] == []
    assert result["boundaries"] == []


def test_standard_omits_all_unvalidated_candidate_categories():
    result = run(timeline(), "standard")
    for category in ("harmony", "motifs", "relationships", "pedalTones", "ostinatos", "boundaries"):
        assert result[category] == []


def test_key_calibration_asserts_primary_winner_votes_and_raw_correlations():
    def fixture(pitches, repeat=3):
        values = list(pitches) * repeat
        return {
            "schemaVersion": 1,
            "sampleRate": 1000,
            "startSample": 0,
            "endSample": len(values) * 250,
            "channels": [{"id": "a", "kind": "fm", "notes": [
                {"id": str(index), "startSample": index * 250,
                 "endSample": (index + 1) * 250,
                 "structuralMidiPitch": pitch, "isPitched": True}
                for index, pitch in enumerate(values)
            ]}],
        }

    cases = [
        ((60, 62, 64, 65, 67, 69, 71, 72, 67, 65, 64, 62), "C", "major"),
        ((69, 71, 72, 74, 76, 77, 79, 81, 76, 74, 72, 71), "A", "minor"),
        ((64, 67, 71, 76, 71, 67), "E", "minor"),
    ]
    for pitches, tonic, mode in cases:
        key = run(fixture(pitches), "standard")["global"]["key"]
        assert key["primary"]["tonic"] == tonic
        assert key["primary"]["mode"] == mode
        assert key["confidence"]["certainty"] == "strong"
        assert key["winnerVotes"][f"{tonic} {mode}"] >= 2
        assert len(key["winnerCorrelations"][f"{tonic} {mode}"]) >= 2
        assert all(-1 <= method["correlation"] <= 1 for method in key["methods"])
        assert key["alternatives"]


def test_key_calibration_withholds_ambiguous_chromatic_and_short_material():
    def fixture(pitches, repeat=1):
        values = list(pitches) * repeat
        return {
            "schemaVersion": 1, "sampleRate": 1000, "startSample": 0,
            "endSample": len(values) * 250,
            "channels": [{"id": "a", "kind": "fm", "notes": [
                {"id": str(index), "startSample": index * 250,
                 "endSample": (index + 1) * 250,
                 "structuralMidiPitch": pitch, "isPitched": True}
                for index, pitch in enumerate(values)
            ]}],
        }

    for data in (
        fixture((60, 62, 64, 66, 68, 70), 5),
        fixture((62, 64, 65, 67, 69, 71, 72, 74), 3),
        fixture(tuple(range(60, 72)), 3),
        fixture((60,), 1),
        fixture((60, 64), 1),
    ):
        confidence = run(data, "standard")["global"]["key"]["confidence"]
        assert confidence["certainty"] == "withheld"
        assert run(data, "standard")["global"]["key"]["primary"] is None


def test_fm3_operator_material_is_excluded_from_standard_key_analysis():
    data = timeline()
    data["endSample"] = 6000
    data["channels"] = [{"id": "op", "kind": "fm3-operator", "analysisWeight": .35,
                         "notes": [
                             {"id": str(i), "startSample": i * 1000,
                              "endSample": (i + 1) * 1000,
                              "structuralMidiPitch": pitch, "isPitched": True}
                             for i, pitch in enumerate((60, 62, 64, 65, 67, 69))]}]
    standard = run(data, "standard")["global"]["key"]
    full = run(data, "full")["global"]["key"]
    assert standard["primary"] is None
    assert full["primary"] is not None


def test_standard_withholds_harmony_and_full_marks_it_experimental():
    triad = timeline()
    triad["endSample"] = 1000
    triad["channels"][0]["notes"] = [
        {"id": "c", "startSample": 0, "endSample": 1000, "structuralMidiPitch": pitch,
         "isPitched": True}
        for pitch in (60, 64, 67)
    ]
    result = run(triad, "standard")
    schema = json.loads((Path(__file__).parent / "schemas" / "analysis-output.schema.json").read_text())
    validate(result, schema)
    assert result["harmony"] == []

    experimental = run(triad, "full")
    validate(experimental, schema)
    assert experimental["harmony"]
    assert all(item["status"] == "experimental" for item in experimental["harmony"])


def _harmony_fixture(pitches, end=1000, starts=None, durations=None):
    starts = starts or [0] * len(pitches)
    durations = durations or [end - start for start in starts]
    return {
        "schemaVersion": 1,
        "sampleRate": 1000,
        "startSample": 0,
        "endSample": end,
        "channels": [{"id": "a", "kind": "fm", "notes": [
            {"id": str(index), "startSample": start, "endSample": start + durations[index],
             "structuralMidiPitch": pitch, "isPitched": True}
            for index, (start, pitch) in enumerate(zip(starts, pitches))
        ]}],
    }


def test_harmony_evidence_accepts_supported_verticalities_and_rejects_clusters():
    accepted = {
        "C major": ((60, 64, 67), "C"),
        "C first inversion": ((64, 67, 72), "C"),
        "A minor": ((69, 72, 76), "Am"),
        "G7": ((67, 71, 74, 77), "G7"),
        "G7 incomplete": ((67, 71, 65), "G7(no5)"),
        "Csus2": ((60, 62, 67), "Csus2"),
    }
    for name, (pitches, symbol) in accepted.items():
        harmony = run(_harmony_fixture(pitches), "full")["harmony"]
        assert harmony, name
        assert harmony[0]["symbol"] == symbol, name
        assert harmony[0]["confidence"]["certainty"] == "strong", name
        assert any(item.startswith("purity=") for item in harmony[0]["confidence"]["evidence"])

    for pitches in ((60, 61, 62), (60, 62, 64, 65, 67), (60, 62, 64, 66, 68, 70)):
        assert run(_harmony_fixture(pitches), "full")["harmony"] == []


def test_harmony_evidence_does_not_turn_a_scale_fragment_into_a_sustained_chord():
    data = _harmony_fixture((60, 62, 64, 65, 67), end=1000,
                            starts=[0, 200, 400, 600, 800], durations=[200] * 5)
    assert run(data, "full")["harmony"] == []


def test_arpeggio_evidence_uses_the_same_normalized_window_units():
    pitches = (60, 64, 67) * 6
    data = _harmony_fixture(pitches, end=900,
                            starts=[index * 50 for index in range(len(pitches))],
                            durations=[40] * len(pitches))
    data["arpeggioEvidence"] = [{
        "channelId": "a", "startSample": 0, "endSample": 890,
        "pitchClasses": [0, 4, 7], "periodSamples": 150,
        "regularity": 1.0, "weight": 1.0,
    }]
    harmony = run(data, "full")["harmony"]
    assert harmony and any(item.startswith("arpeggioSupport=") and not item.endswith("0.000")
                           for item in harmony[0]["confidence"]["evidence"])


def test_harmony_loop_boundary_prevents_one_merged_segment():
    data = _harmony_fixture((60, 64, 67), end=2000)
    data["channels"][0]["notes"] = [
        {"id": str(index), "startSample": start, "endSample": end,
         "structuralMidiPitch": pitch, "isPitched": True}
        for index, (start, end, pitch) in enumerate((
            (0, 1000, 60), (0, 1000, 64), (0, 1000, 67),
            (1000, 2000, 60), (1000, 2000, 64), (1000, 2000, 67),
        ))
    ]
    data["timing"] = {"loops": [{"sample": 1000, "kind": "restart", "iteration": 1}]}
    harmony = run(data, "full")["harmony"]
    assert len(harmony) >= 2


def test_roman_numerals_require_strong_key_and_chord_and_remain_experimental():
    notes = [
        {"id": f"c{index}", "startSample": 0, "endSample": 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for index, pitch in enumerate((60, 64, 67))
    ]
    notes.extend(
        {"id": f"s{index}", "startSample": 1000 + index * 300,
         "endSample": 1000 + (index + 1) * 300, "structuralMidiPitch": pitch,
         "isPitched": True}
        for index, pitch in enumerate((60, 62, 64, 65, 67, 69, 71, 72, 67, 65, 64, 62))
    )
    data = {"schemaVersion": 1, "sampleRate": 1000, "startSample": 0,
            "endSample": 4600,
            "channels": [{"id": "a", "kind": "fm", "notes": notes}]}
    full = run(data, "full")["harmony"]
    assert full and full[0]["roman"] == "I"
    assert full[0]["romanConfidence"]["certainty"] == "strong"
    assert run(data, "standard")["harmony"] == []


def test_input_schema_accepts_non_tonal_channel_shape():
    from mdplayer_music_analysis import validate_input

    data = timeline()
    data.update({
        "startSample": 0,
        "endSample": 3000,
        "track": {"title": "fixture", "sourceFormat": "FMP", "sourcePathHint": "fixture.ovi"},
        "timing": {"timingMode": "seconds", "tempoEvents": [], "beats": [], "measures": [], "loops": []},
    })
    data["channels"].append({
        "id": "noise", "kind": "ssg-noise", "analysisWeight": 0,
        "notes": [{"id": "noise:0", "startSample": 0, "endSample": 1000,
                   "structuralMidiPitch": -1, "pitchClass": -1,
                   "isPitched": False, "isNoise": True}],
    })

    validate_input(data)


def test_transposed_motif_is_reported():
    pitches = (60, 63, 67, 72, 75, 79, 84, 87)
    data = timeline()
    data["endSample"] = 6000
    data["channels"][0]["notes"] = [
        {"id": str(index), "startSample": index * 1000, "endSample": (index + 1) * 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for index, pitch in enumerate(pitches)
    ]
    motifs = run(data, "full")["motifs"]
    assert motifs and motifs[0]["similarity"] == 1.0
    assert all(item["status"] == "experimental" for item in motifs)


def test_exact_motif_fingerprint_requires_pitch_and_rhythm_agreement():
    def data_for(first, second, second_durations=None):
        pitches = tuple(first) + tuple(second)
        durations = [500] * len(pitches)
        if second_durations:
            durations[len(first):] = second_durations
        start = 0
        notes = []
        for index, (pitch, duration) in enumerate(zip(pitches, durations)):
            notes.append({"id": str(index), "startSample": start,
                          "endSample": start + duration,
                          "structuralMidiPitch": pitch, "isPitched": True})
            start += duration
        result = timeline()
        result["endSample"] = start
        result["channels"][0]["notes"] = notes
        return result

    assert run(data_for((60, 63, 67), (64, 67, 71)), "full")["motifs"]
    assert run(data_for((60, 63, 67), (64, 67, 71), [400, 600, 500]), "full")["motifs"] == []
    assert run(data_for((60, 63, 67), (65, 66, 70)), "full")["motifs"] == []
    assert run(data_for((60, 62, 64), (65, 67, 69)), "full")["motifs"] == []
    assert run(data_for((60, 62), (60, 62,)), "full")["motifs"] == []

def test_full_pattern_candidates_are_experimental_and_deterministic():
    data = timeline()
    data["endSample"] = 8000
    pitches = (60, 62, 60, 62, 60, 62, 60, 62)
    data["channels"][0]["notes"] = [
        {"id": str(i), "startSample": i * 1000, "endSample": (i + 1) * 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for i, pitch in enumerate(pitches)
    ]
    first = run(data, "full")
    second = run(data, "full")
    assert first == second
    assert first["pedalTones"] == [] and first["ostinatos"]
    assert all(item["status"] == "experimental" for item in first["ostinatos"])


def test_pedal_requires_long_cross_harmony_support():
    data = timeline()
    data["endSample"] = 7000
    data["channels"] = [{"id": "pedal", "kind": "fm", "notes": [
        {"id": "p0", "startSample": 0, "endSample": 5000,
         "structuralMidiPitch": 60, "isPitched": True},
        {"id": "p1", "startSample": 5000, "endSample": 7000,
         "structuralMidiPitch": 60, "isPitched": True},
    ]}, {"id": "harmony", "kind": "fm", "notes": [
        {"id": f"h{i}", "startSample": start, "endSample": start + 2000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for i, (start, pitch) in enumerate(((0, 64), (2000, 65), (4000, 67), (6000, 69)))
    ]}]
    pedal = run(data, "full")["pedalTones"]
    assert pedal and pedal[0]["coverage"] >= .40

def test_full_relationship_reports_a_known_doubling_and_is_experimental():
    data = timeline()
    data["endSample"] = 4000
    data["channels"][0]["notes"] = [
        {"id": f"a{i}", "startSample": i * 1000, "endSample": (i + 1) * 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for i, pitch in enumerate((60, 64, 67, 72))
    ]
    data["channels"].append({"id": "b", "kind": "fm", "notes": [
        {"id": f"b{i}", "startSample": i * 1000, "endSample": (i + 1) * 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for i, pitch in enumerate((72, 76, 79, 84))
    ]})
    result = run(data, "full")
    assert result["relationships"]
    assert all(item["coverage"] >= .70 for item in result["relationships"])
    assert all(item["status"] == "experimental" for item in result["relationships"])


def test_short_notes_can_support_relationships_but_accidental_pairs_cannot():
    data = timeline()
    data["endSample"] = 2000
    data["channels"] = []
    for channel_id, offset in (("a", 0), ("b", 12)):
        data["channels"].append({"id": channel_id, "kind": "fm", "notes": [
            {"id": f"{channel_id}{i}", "startSample": i * 250,
             "endSample": i * 250 + 100, "structuralMidiPitch": 60 + i + offset,
             "isPitched": True}
            for i in range(8)
        ]})
    assert run(data, "full")["relationships"]

    negative = json.loads(json.dumps(data))
    negative["channels"][1]["notes"] = [
        {**note, "startSample": note["startSample"] + 100}
        for note in negative["channels"][1]["notes"]
    ]
    assert run(negative, "full")["relationships"] == []

def test_loop_duplicate_is_suppressed():
    data = timeline()
    data["endSample"] = 6000
    data["timing"] = {"loops": [{"startSample": 0, "endSample": 3000}]}
    data["channels"][0]["notes"] = [
        {"id": str(i), "startSample": i * 1000, "endSample": (i + 1) * 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for i, pitch in enumerate((60, 62, 64, 60, 62, 64))
    ]
    result = run(data, "full")
    assert result["motifs"] == []
    assert result["boundaries"][0]["sample"] == 0


def test_serialized_loop_markers_are_boundaries_and_suppress_recurrence():
    data = timeline()
    data["endSample"] = 6000
    data["timing"] = {"loops": [
        {"sample": 0, "kind": "start", "iteration": 0},
        {"sample": 3000, "kind": "restart", "iteration": 1},
    ]}
    data["channels"][0]["notes"] = [
        {"id": str(i), "startSample": i * 1000, "endSample": (i + 1) * 1000,
         "structuralMidiPitch": pitch, "isPitched": True}
        for i, pitch in enumerate((60, 62, 64, 60, 62, 64))
    ]
    result = run(data, "full")
    assert result["motifs"] == []
    assert [item["sample"] for item in result["boundaries"] if item["kind"].startswith("loop-")] == [0, 3000]


def test_global_key_is_exposed_as_a_region_and_seconds_mode_is_explicit():
    result = run(timeline(), "standard")
    assert result["timingMode"] == "seconds"
    # The three-note fixture is intentionally below the public key gate.
    assert result["keys"] == []


def test_validated_beats_enable_stable_local_key_regions():
    pitches = (60, 62, 64, 65, 67, 69, 71, 72) * 3
    data = {
        "schemaVersion": 1,
        "sampleRate": 1000,
        "startSample": 0,
        "endSample": 6000,
        "timing": {
            "timingMode": "beats",
            "beats": [{"sample": index * 250, "beat": index} for index in range(25)],
            "measures": [], "tempoEvents": [], "loops": [],
        },
        "channels": [{"id": "a", "kind": "fm", "notes": [
            {"id": str(index), "startSample": index * 250, "endSample": (index + 1) * 250,
             "structuralMidiPitch": pitch, "isPitched": True}
            for index, pitch in enumerate(pitches)
        ]}],
    }
    result = run(data, "standard")
    assert result["timingMode"] == "beats"
    assert len(result["keys"]) == 1
    assert result["keys"][0]["tonic"] == "C"
    assert result["keys"][0]["mode"] == "major"


def test_large_activity_gap_is_reported_as_tentative_boundary():
    data = timeline()
    data["endSample"] = 5000
    data["channels"][0]["notes"] = [
        {"id": "first", "startSample": 0, "endSample": 500,
         "structuralMidiPitch": 60, "isPitched": True},
        {"id": "second", "startSample": 1500, "endSample": 2000,
         "structuralMidiPitch": 64, "isPitched": True},
    ]
    boundaries = run(data, "full")["boundaries"]
    assert any(item["sample"] == 1500 and item["kind"] == "activity-gap"
               and item["confidence"]["certainty"] == "tentative" for item in boundaries)


def test_input_validation_rejects_invalid_ranges_and_duplicate_channels():
    from mdplayer_music_analysis import InputSchemaError, validate_input

    invalid = timeline()
    invalid.update({"track": {}, "timing": {}})
    invalid["endSample"] = 2000
    invalid["channels"][0]["notes"][0]["endSample"] = 3001
    with pytest.raises(InputSchemaError):
        validate_input(invalid)

    duplicate = timeline()
    duplicate.update({"track": {}, "timing": {}})
    duplicate["channels"].append({"id": "a", "kind": "fm", "notes": []})
    with pytest.raises(InputSchemaError):
        validate_input(duplicate)
