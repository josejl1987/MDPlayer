#!/usr/bin/env python3
"""Offline, deterministic symbolic analysis worker for MDPlayer timelines."""
from __future__ import annotations
import argparse, hashlib, json, math, sys, time
from pathlib import Path
from score_builder import build_score
from key_analysis import analyze_key
from local_key_analysis import analyze_local_keys
from feature_analysis import analyze_features
from harmony_analysis import analyze_harmony
from motif_analysis import analyze_motifs
from pattern_analysis import analyze_boundaries, analyze_pedal_tones, analyze_ostinatos
from relationship_analysis import analyze_relationships

VERSION = "1.1.0"
WORKER_COMPATIBILITY = "1.1.0"
REQUIRED_MUSIC21_VERSION = "10.5.0"

class InputSchemaError(Exception):
    pass

class OutputSchemaError(Exception):
    pass

def canonical(value):
    if isinstance(value, float):
        if not math.isfinite(value): raise ValueError("non-finite number")
        return round(value, 6)
    if isinstance(value, dict): return {k: canonical(value[k]) for k in sorted(value)}
    if isinstance(value, list): return [canonical(x) for x in value]
    return value

def digest(value):
    raw = json.dumps(canonical(value), ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
    return "sha256:" + hashlib.sha256(raw).hexdigest()


def _public_candidates(items, detail):
    """Keep non-key candidates out of public detail levels until validated."""
    if detail != "full":
        return []
    return [{**item, "status": "experimental"} for item in items]

def _stage(profile, name, started):
    if not profile:
        return
    elapsed_ns = time.monotonic_ns() - started
    print(json.dumps({"event": "analysis-stage", "stage": name,
                      "elapsedNs": elapsed_ns,
                      "elapsedMs": round(elapsed_ns / 1_000_000, 3)}, sort_keys=True),
          file=sys.stderr)


def probe(as_json=False):
    import music21
    if music21.__version__ != REQUIRED_MUSIC21_VERSION:
        raise ImportError(f"music21=={REQUIRED_MUSIC21_VERSION} required; found {music21.__version__}")
    result = {
        "worker": "mdplayer-music-analysis",
        "version": VERSION,
        "workerVersion": VERSION,
        "compatibility": WORKER_COMPATIBILITY,
        "schemaVersion": 1,
        "outputSchemaVersion": 1,
        "music21Version": music21.__version__,
        "requiredMusic21Version": REQUIRED_MUSIC21_VERSION,
        "partituraVersion": None,
        "compatible": True,
    }
    print(json.dumps(result, sort_keys=True) if as_json else music21.__version__)


def _assert_finite(value, path="$"):
    if isinstance(value, float) and not math.isfinite(value):
        raise InputSchemaError(f"non-finite number at {path}")
    if isinstance(value, dict):
        for key, child in value.items():
            _assert_finite(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _assert_finite(child, f"{path}[{index}]")

def validate_input(data):
    _assert_finite(data)
    try:
        import jsonschema
        schema = json.loads((Path(__file__).parent / "schemas" / "analysis-input.schema.json").read_text(encoding="utf-8"))
        jsonschema.validate(data, schema)
    except ImportError:
        raise
    except Exception as exc:
        raise InputSchemaError(str(exc)) from exc
    if data["endSample"] < data["startSample"]:
        raise InputSchemaError("endSample must be greater than or equal to startSample")
    channel_ids = set()
    for channel in data["channels"]:
        channel_id = channel["id"]
        if channel_id in channel_ids:
            raise InputSchemaError(f"duplicate channel id: {channel_id}")
        channel_ids.add(channel_id)
        for note in channel["notes"]:
            if note["endSample"] < note["startSample"]:
                raise InputSchemaError(f"note endSample precedes startSample: {note.get('id', '<unnamed>')}")
            if note["startSample"] < data["startSample"] or note["endSample"] > data["endSample"]:
                raise InputSchemaError(f"note is outside source range: {note.get('id', '<unnamed>')}")
    for item in data.get("arpeggioEvidence", []):
        if item.get("endSample", 0) < item.get("startSample", 0):
            raise InputSchemaError("arpeggio evidence endSample precedes startSample")
    timing = data.get("timing", {})
    beats = timing.get("beats", [])
    if timing.get("timingMode") == "beats":
        if len(beats) < 2:
            raise InputSchemaError("beat timing requires at least two beat events")
        for previous, current in zip(beats, beats[1:]):
            if current.get("sample", 0) <= previous.get("sample", 0) \
                    or current.get("beat", 0) <= previous.get("beat", 0):
                raise InputSchemaError("beat events must increase in sample and beat order")
        for beat in beats:
            if not data["startSample"] <= beat.get("sample", -1) <= data["endSample"]:
                raise InputSchemaError("beat event is outside the source range")

def run(inp, detail, analysis_profile=False):
    profile_started = time.monotonic_ns()
    import music21
    if music21.__version__ != REQUIRED_MUSIC21_VERSION:
        raise ImportError(f"music21=={REQUIRED_MUSIC21_VERSION} required; found {music21.__version__}")
    score, notes, warnings = build_score(inp, detail)
    _stage(analysis_profile, "score", profile_started)
    key = analyze_key(score, notes, detail)
    _stage(analysis_profile, "key", profile_started)
    local_keys = analyze_local_keys(notes, inp.get("timing", {}), detail)
    _stage(analysis_profile, "local-keys", profile_started)
    features = analyze_features(notes, score, detail)
    _stage(analysis_profile, "features", profile_started)
    warnings.extend({"code": "JSYMBOLIC_LIMITED", "message": message}
                    for message in features.pop("_warnings", []))
    harmony = _public_candidates(analyze_harmony(
            score,
            notes,
            key,
            detail,
            inp.get("arpeggioEvidence", []),
            inp.get("sampleRate"),
            inp.get("timing", {}).get("loops", []),
        ), detail) if detail == "full" else []
    motifs = _public_candidates(analyze_motifs(notes, detail, inp.get("timing", {}).get("loops", [])), detail) if detail == "full" else []
    pedal_tones = _public_candidates(analyze_pedal_tones(notes, float(inp.get("sampleRate", 1) or 1)), detail) if detail == "full" else []
    ostinatos = _public_candidates(analyze_ostinatos(
            notes,
            detail,
            inp.get("timing", {}).get("loops", []),
            float(inp.get("sampleRate", 1) or 1),
        ), detail) if detail == "full" else []
    relationships = _public_candidates(analyze_relationships(notes, float(inp.get("sampleRate", 1) or 1), detail), detail) if detail == "full" else []
    boundaries = _public_candidates(analyze_boundaries(
        notes,
        float(inp.get("sampleRate", 1) or 1),
        inp.get("timing", {}).get("loops", []),
    ), detail) if detail == "full" else []
    _stage(analysis_profile, "full-analysis", profile_started)
    timing = inp.get("timing", {})
    timing_mode = "beats" if timing.get("timingMode") == "beats" and timing.get("beats") else "seconds"
    key_regions = []
    for region in local_keys:
        primary_region = {
            "startSample": region["startSample"],
            "endSample": region["endSample"],
            "tonic": region["tonic"],
            "mode": region["mode"],
            "tonicPitchClass": music21.pitch.Pitch(region["tonic"]).pitchClass,
            "confidence": region["confidence"],
        }
        key_regions.append(primary_region)
    primary = key.get("primary")
    if primary is not None and not key_regions:
        key_regions.append({
            "startSample": int(inp.get("startSample", 0)),
            "endSample": int(inp.get("endSample", 0)),
            "tonicPitchClass": int(primary["tonicPitchClass"]),
            "tonic": primary["tonic"],
            "mode": primary["mode"],
            "confidence": key["confidence"],
        })
    result = {"schemaVersion": 1, "analysisId": inp.get("analysisId") or digest(inp),
      "engine": {"name": "mdplayer-music-analysis", "version": VERSION,
                  "music21Version": music21.__version__, "partituraVersion": None},
      "timingMode": timing_mode,
      "global": {"key": key, "pitch": {k: v for k, v in features.items() if k != "channels"}}, "channels": features.get("channels", []),
      "keys": key_regions, "harmony": harmony, "motifs": motifs,
      "relationships": relationships, "pedalTones": pedal_tones, "ostinatos": ostinatos,
      "boundaries": boundaries, "warnings": warnings}
    if result["timingMode"] == "seconds":
        result["warnings"].append({"code":"TIMING_UNAVAILABLE", "message":"Beat-aligned analysis was skipped because no validated FMP timing map was available."})
    return canonical(result)

def main(argv=None):
    p = argparse.ArgumentParser()
    p.add_argument("--input"); p.add_argument("--output"); p.add_argument("--detail", choices=("minimal","standard","full"), default="standard")
    p.add_argument("--probe", action="store_true")
    p.add_argument("--json", action="store_true", dest="probe_json",
                   help="emit structured output for --probe")
    p.add_argument("--analysis-profile", action="store_true",
                   help="emit monotonic analysis stage metrics on stderr")
    a = p.parse_args(argv)
    try:
        if a.probe: probe(a.probe_json); return 0
        if not a.input or not a.output: p.error("--input and --output are required")
        data = json.loads(Path(a.input).read_text(encoding="utf-8"))
        validate_input(data)
        result = run(data, a.detail, a.analysis_profile)
        try:
            import jsonschema
            schema = json.loads((Path(__file__).parent / "schemas" / "analysis-output.schema.json").read_text(encoding="utf-8"))
            jsonschema.validate(result, schema)
        except ImportError:
            raise
        except Exception as exc:
            raise OutputSchemaError(str(exc)) from exc
        output_path = Path(a.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(json.dumps(result, ensure_ascii=False, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        return 0
    except InputSchemaError as e:
        print(f"invalid input schema: {e}", file=sys.stderr); return 3
    except OutputSchemaError as e:
        print(f"invalid output schema: {e}", file=sys.stderr); return 6
    except ImportError as e:
        print(f"missing Python dependency: {e}", file=sys.stderr); return 4
    except Exception as e:
        print(f"analysis failed: {e}", file=sys.stderr); return 5
if __name__ == "__main__": sys.exit(main())
