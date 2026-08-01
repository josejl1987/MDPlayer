import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent))
from mdplayer_music_analysis import VERSION, WORKER_COMPATIBILITY, _stage


def test_worker_compatibility_is_bumped_without_changing_output_schema():
    assert VERSION == "1.1.0"
    assert WORKER_COMPATIBILITY == "1.1.0"


def test_profile_records_are_json_and_monotonic(capsys, monkeypatch):
    values = iter((1_000_000, 3_500_000))
    monkeypatch.setattr("mdplayer_music_analysis.time.monotonic_ns", lambda: next(values))
    _stage(True, "first", 0)
    _stage(True, "second", 0)
    records = [json.loads(line) for line in capsys.readouterr().err.splitlines()]
    assert [record["stage"] for record in records] == ["first", "second"]
    assert records[0]["elapsedMs"] < records[1]["elapsedMs"]
    assert all(record["event"] == "analysis-stage" for record in records)


def test_profile_is_silent_when_disabled(capsys):
    _stage(False, "ignored", 0)
    assert capsys.readouterr().err == ""
