#!/usr/bin/env python3
"""Run the adversarial MIDI corpus against the built CLI.

The runner is intentionally external to the product and test projects. It can
use raw source files, fall back to persisted timelines when a decoder backend is
not available, and emits a machine-readable report without adding generated
files to the repository.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
import json
import re
import subprocess
import tempfile
from pathlib import Path
from typing import Any


class SmfError(ValueError):
    pass


def read_vlq(data: bytes, position: int, end: int) -> tuple[int, int]:
    value = 0
    for _ in range(4):
        if position >= end:
            raise SmfError("truncated variable-length quantity")
        byte = data[position]
        position += 1
        value = (value << 7) | (byte & 0x7F)
        if byte & 0x80 == 0:
            return value, position
    raise SmfError("variable-length quantity exceeds four bytes")


def read_u16(data: bytes, position: int) -> tuple[int, int]:
    if position + 2 > len(data):
        raise SmfError("truncated 16-bit value")
    return int.from_bytes(data[position : position + 2], "big"), position + 2


def read_u32(data: bytes, position: int) -> tuple[int, int]:
    if position + 4 > len(data):
        raise SmfError("truncated 32-bit value")
    return int.from_bytes(data[position : position + 4], "big"), position + 4


def event_rank(kind: str, controller: int | None = None) -> int:
    if kind == "note_off":
        return 0
    if kind in {"bank", "program"}:
        return 1
    if kind == "control" and controller in {101, 100, 6, 38}:
        return 2
    if kind == "pitch_bend":
        return 3
    if kind == "note_on":
        return 4
    if kind == "control":
        return 5
    return 6


def validate_smf(data: bytes) -> dict[str, int]:
    """Parse and validate the serialized SMF independently of .NET code."""

    if data[:4] != b"MThd":
        raise SmfError("missing SMF header")
    header_length, position = read_u32(data, 4)
    if header_length != 6:
        raise SmfError("unsupported SMF header length")
    format_value, position = read_u16(data, position)
    track_count, position = read_u16(data, position)
    division, position = read_u16(data, position)
    if format_value != 1 or track_count == 0 or division & 0x8000:
        raise SmfError("SMF must be Format 1 with a PPQ division")

    endpoint_owners: dict[tuple[int, int], int] = {}
    track_events: list[list[dict[str, int | str | None]]] = []
    tempo_at_zero = False
    total_channel_events = 0
    total_pitch_bends = 0

    for track_index in range(track_count):
        if data[position : position + 4] != b"MTrk":
            raise SmfError(f"track {track_index} is missing MTrk")
        length, position = read_u32(data, position + 4)
        end = position + length
        if end > len(data):
            raise SmfError(f"track {track_index} exceeds file length")
        tick = 0
        port = 0
        running = 0
        ended = False
        events: list[dict[str, int | str | None]] = []
        while position < end:
            delta, position = read_vlq(data, position, end)
            tick += delta
            status = data[position]
            if status < 0x80:
                if running == 0:
                    raise SmfError("data byte without running status")
                status = running
            else:
                position += 1
                if status < 0xF0:
                    running = status
                else:
                    running = 0

            if status == 0xFF:
                if position >= end:
                    raise SmfError("truncated meta event")
                meta = data[position]
                length_value, position = read_vlq(data, position + 1, end)
                meta_end = position + length_value
                if meta_end > end:
                    raise SmfError("meta event exceeds track")
                if meta == 0x21 and length_value == 1:
                    port = data[position]
                elif meta == 0x51 and length_value == 3:
                    if track_index == 0 and tick == 0:
                        tempo_at_zero = True
                    events.append({"tick": tick, "kind": "tempo", "channel": None})
                elif meta == 0x58:
                    if tick != 0:
                        raise SmfError("time signature is not at tick zero")
                    events.append({"tick": tick, "kind": "time_signature", "channel": None})
                if meta == 0x2F:
                    if length_value != 0 or meta_end != end:
                        raise SmfError("End of Track is not final")
                    position = end
                    ended = True
                    break
                position = meta_end
                continue

            if status in {0xF0, 0xF7}:
                length_value, position = read_vlq(data, position, end)
                position += length_value
                if position > end:
                    raise SmfError("sysex event exceeds track")
                continue

            if status < 0x80 or status >= 0xF0:
                raise SmfError("unsupported system event")
            channel = status & 0x0F
            message = status & 0xF0
            if position >= end:
                raise SmfError("truncated channel event")
            data1 = data[position]
            position += 1
            data2 = 0
            if message not in {0xC0, 0xD0}:
                if position >= end:
                    raise SmfError("truncated channel event data")
                data2 = data[position]
                position += 1
            if message == 0x80 or (message == 0x90 and data2 == 0):
                kind = "note_off"
            elif message == 0x90:
                kind = "note_on"
            elif message == 0xB0 and data1 in {0, 32}:
                kind = "bank"
            elif message == 0xB0:
                kind = "control"
            elif message == 0xC0:
                kind = "program"
            elif message == 0xE0:
                kind = "pitch_bend"
            else:
                kind = "other"
            event = {
                "tick": tick,
                "kind": kind,
                "channel": channel,
                "controller": data1 if kind == "control" else None,
                "data1": data1,
                "data2": data2,
                "port": port,
            }
            events.append(event)
            total_channel_events += 1
            endpoint = (port, channel)
            owner = endpoint_owners.setdefault(endpoint, track_index)
            if owner != track_index:
                raise SmfError(f"endpoint {endpoint} is owned by multiple tracks")
            if kind == "pitch_bend":
                total_pitch_bends += 1
        if not ended:
            raise SmfError(f"track {track_index} is missing final End of Track")
        track_events.append(events)

    if not tempo_at_zero:
        raise SmfError("conductor has no tempo at tick zero")

    for track_index, events in enumerate(track_events):
        active: dict[tuple[int, int], int] = defaultdict(int)
        previous_tick = -1
        previous_rank = -1
        bends_at_tick: dict[tuple[int, int], int] = defaultdict(int)
        rpn: dict[int, tuple[int, int]] = defaultdict(lambda: (127, 127))
        ranges: dict[int, int] = defaultdict(int)
        range_at_zero: set[int] = set()
        cc38_zero: set[int] = set()
        null_rpn: set[int] = set()
        for event in events:
            tick_value = int(event["tick"])
            kind = str(event["kind"])
            if tick_value != previous_tick:
                previous_tick = tick_value
                previous_rank = -1
            rank = event_rank(kind, event.get("controller"))
            if rank < previous_rank:
                raise SmfError(f"track {track_index} has ambiguous same-tick ordering")
            previous_rank = rank
            channel_value = event.get("channel")
            if channel_value is None:
                continue
            channel = int(channel_value)
            key = (channel, int(event.get("data1", 0)))
            if kind == "note_on":
                active[key] += 1
            elif kind == "note_off":
                if active[key] == 0:
                    raise SmfError("NoteOff has no preceding NoteOn")
                active[key] -= 1
            elif kind == "pitch_bend":
                bends_at_tick[(channel, tick_value)] += 1
                if bends_at_tick[(channel, tick_value)] > 1:
                    raise SmfError("multiple same-tick pitch bends")
                if ranges[channel] == 0:
                    raise SmfError("pitch bend was observed before RPN range setup")
            elif kind == "control":
                controller = int(event["controller"])
                value = int(event["data2"])
                if controller == 101:
                    rpn[channel] = (value, rpn[channel][1])
                elif controller == 100:
                    rpn[channel] = (rpn[channel][0], value)
                elif controller == 6 and rpn[channel] == (0, 0):
                    ranges[channel] += 1
                    if tick_value != 0 or ranges[channel] != 1:
                        raise SmfError("bend range changed after initialization")
                    range_at_zero.add(channel)
                elif controller == 38 and rpn[channel] == (0, 0) and value == 0:
                    cc38_zero.add(channel)
                if rpn[channel] == (127, 127):
                    null_rpn.add(channel)
        if any(value > 0 for value in active.values()):
            raise SmfError(f"track {track_index} ends with active notes")
        for channel, count in ranges.items():
            if count != 1 or channel not in range_at_zero or channel not in cc38_zero or channel not in null_rpn:
                raise SmfError("RPN bend range initialization is incomplete")

    return {
        "tracks": track_count,
        "channelEvents": total_channel_events,
        "pitchBends": total_pitch_bends,
    }


def run_export(
    cli: Path,
    source: Path | None,
    timeline: Path | None,
    timeout: float,
) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="mdplayer-midi-corpus-") as directory:
        output = Path(directory) / "export.mid"
        if source is not None:
            command = [str(cli), "midi", str(source)]
            capture = "source"
        elif timeline is not None:
            command = [str(cli), "midi", "dummy", "--timeline", str(timeline)]
            capture = "timeline"
        else:
            return {"status": "blocked", "error": "no source or timeline"}
        command += ["--output", str(output), "--musical-grid"]
        try:
            completed = subprocess.run(
                command,
                capture_output=True,
                text=True,
                timeout=timeout,
                check=False,
            )
        except subprocess.TimeoutExpired:
            return {"status": "blocked", "capture": capture, "error": "timeout"}
        text = completed.stdout + "\n" + completed.stderr
        if completed.returncode != 0:
            return {
                "status": "blocked",
                "capture": capture,
                "exitCode": completed.returncode,
                "error": text.strip()[-1000:],
            }
        if not output.exists():
            return {"status": "failed", "capture": capture, "error": "missing SMF"}
        data = output.read_bytes()
        try:
            smf = validate_smf(data)
        except SmfError as error:
            return {"status": "failed", "capture": capture, "error": f"invalid SMF: {error}"}
        tempo = re.search(r"tempo: ([0-9]+(?:\.[0-9]+)?) BPM", text)
        notes = re.search(r"events: notes=([0-9]+)", text)
        resolution = re.search(
            r"tempo-resolved: (True|False); meter: ([^;]+); "
            r"meter-resolved: (True|False); downbeat-resolved: (True|False)",
            text,
        )
        return {
            "status": "passed",
            "capture": capture,
            "bytes": output.stat().st_size,
            "smf": smf,
            "tempo": float(tempo.group(1)) if tempo else None,
            "notes": int(notes.group(1)) if notes else None,
            "tempoResolved": resolution.group(1) == "True" if resolution else None,
            "meter": resolution.group(2) if resolution else None,
            "meterResolved": resolution.group(3) == "True" if resolution else None,
            "downbeatResolved": resolution.group(4) == "True" if resolution else None,
        }


def review_error(entry: dict[str, Any], result: dict[str, Any]) -> str | None:
    """Check reviewed sidecar labels without inventing labels for pending files."""

    if entry.get("reviewStatus") != "reviewed" or result.get("status") != "passed":
        return None
    expected = entry.get("expected", {})
    if expected.get("tempo") is not None:
        actual = result.get("tempo")
        tolerance = float(entry.get("tempoTolerance", 0.5))
        if not isinstance(actual, (int, float)) or abs(float(actual) - float(expected["tempo"])) > tolerance:
            return f"tempo {actual!r} does not match reviewed value {expected['tempo']!r}"
    if expected.get("meter") is not None:
        actual = result.get("meter")
        if actual != expected["meter"]:
            return f"meter {actual!r} does not match reviewed value {expected['meter']!r}"
    if expected.get("downbeat") is not None:
        actual = result.get("downbeatResolved")
        if not isinstance(expected["downbeat"], bool) or actual != expected["downbeat"]:
            return f"downbeat resolution {actual!r} does not match reviewed value {expected['downbeat']!r}"
    return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--timeline-root", type=Path)
    parser.add_argument("--mdplayer", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=120.0)
    parser.add_argument("--require-reviewed", action="store_true")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    results: list[dict[str, Any]] = []
    for entry in manifest["entries"]:
        source_name = entry["source"]
        source = args.source_root / source_name
        stem = Path(source_name).stem
        timeline = (
            args.timeline_root / f"{stem}.visualization" / "timeline.json"
            if args.timeline_root is not None
            else None
        )
        result = run_export(
            args.mdplayer,
            source if source.exists() else None,
            timeline if timeline is not None and timeline.exists() else None,
            args.timeout,
        )
        if result["status"] != "passed" and timeline is not None and timeline.exists() and source.exists():
            fallback = run_export(args.mdplayer, None, timeline, args.timeout)
            if fallback["status"] == "passed":
                result = fallback
                result["sourceFallback"] = True
        error = review_error(entry, result)
        if error is not None:
            result = {**result, "status": "failed", "error": f"reviewed sidecar mismatch: {error}"}
        result["source"] = source_name
        result["reviewStatus"] = entry.get("reviewStatus", "pending")
        results.append(result)

    pending = sum(result["reviewStatus"] != "reviewed" for result in results)
    failed = sum(result["status"] != "passed" for result in results)
    report = {
        "manifest": str(args.manifest),
        "entries": results,
        "summary": {
            "total": len(results),
            "passed": len(results) - failed,
            "failedOrBlocked": failed,
            "pendingReview": pending,
        },
    }
    print(json.dumps(report, indent=2, sort_keys=True))
    if failed:
        return 2
    return 3 if args.require_reviewed and pending else 0


if __name__ == "__main__":
    raise SystemExit(main())
