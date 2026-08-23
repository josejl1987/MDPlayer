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


def validate_smf(data: bytes) -> dict[str, Any]:
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
    track_names: list[str | None] = []
    tempo_events: list[tuple[int, int]] = []
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
        track_name: str | None = None
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
                elif meta == 0x03:
                    track_name = data[position:meta_end].decode("utf-8", errors="replace")
                elif meta == 0x51 and length_value == 3:
                    if track_index == 0:
                        tempo_events.append((tick, int.from_bytes(data[position:meta_end], "big")))
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
        track_names.append(track_name)

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
        "_division": division,
        "_trackEvents": track_events,
        "_trackNames": track_names,
        "_tempoEvents": tempo_events,
    }


def source_pitch_points(note: dict[str, Any]) -> list[tuple[int, float]]:
    start = int(note["startSample"])
    end = int(note["endSample"])
    points: list[tuple[int, float]] = [(start, float(note["initialMidiNote"]))]
    for change in note.get("pitch") or []:
        sample = int(change["sample"])
        if sample < start or sample >= end:
            continue
        point = (sample, float(change["midiNote"]))
        if points[-1][0] == sample:
            points[-1] = point
        else:
            points.append(point)
    return points


def round_away_from_zero(value: float) -> int:
    return int(value + 0.5) if value >= 0 else int(value - 0.5)


def relative_serialized_tick(base_tick: float, phase: float) -> int:
    """Model SampleToTick(sample)-SampleToTick(start) including map phase."""

    return round_away_from_zero(base_tick + phase) - round_away_from_zero(phase)


def source_tick(
    sample: int,
    timeline: dict[str, Any],
    tempo_events: list[tuple[int, int]],
    ppq: int,
) -> float:
    """Invert the serialized tempo map for source-relative seconds."""

    start = int(timeline.get("startSample", 0))
    sample_rate = float(timeline["sampleRate"])
    target_seconds = (sample - start) / sample_rate
    ordered = sorted(tempo_events)
    if not ordered or ordered[0][0] != 0:
        raise SmfError("cannot validate source timing without a tick-zero tempo")

    elapsed = 0.0
    previous_tick, previous_tempo = ordered[0]
    for tick, tempo in ordered[1:]:
        if tempo <= 0 or tick < previous_tick:
            raise SmfError("serialized tempo map is invalid")
        segment_seconds = (tick - previous_tick) * previous_tempo / 1_000_000.0 / ppq
        if target_seconds <= elapsed + segment_seconds:
            return previous_tick + (target_seconds - elapsed) / (
                previous_tempo / 1_000_000.0 / ppq)
        elapsed += segment_seconds
        previous_tick, previous_tempo = tick, tempo
    if previous_tempo <= 0:
        raise SmfError("serialized tempo map contains a non-positive tempo")
    return previous_tick + (target_seconds - elapsed) / (
        previous_tempo / 1_000_000.0 / ppq)


def validate_source_pitch(
    smf: dict[str, Any],
    timeline: dict[str, Any],
    phase_tick_origin: float | None = None,
) -> dict[str, int | float]:
    """Reconstruct real-note pitch from serialized channel state independently."""

    ppq = int(smf["_division"])
    track_events: list[list[dict[str, Any]]] = smf["_trackEvents"]
    track_names: list[str | None] = smf["_trackNames"]
    tempo_events: list[tuple[int, int]] = smf["_tempoEvents"]
    notes = timeline.get("notes") or []
    groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for note in notes:
        if isinstance(note, dict) and note.get("voiceId") is not None:
            groups[str(note["voiceId"])].append(note)

    def group_for_track(name: str | None) -> tuple[str | None, list[dict[str, Any]] | None]:
        if name is None:
            return None, None
        if name in groups:
            return name, groups[name]
        key = name.removeprefix("domain:")
        return (key, groups[key]) if key in groups else (None, None)

    note_on_events = {
        track_index: [event for event in events if event["kind"] == "note_on"]
        for track_index, events in enumerate(track_events)
        if track_index != 0
    }
    note_track_counts: dict[int, int] = {
        track_index: len(events)
        for track_index, events in note_on_events.items()
    }
    group_track_candidates: dict[str, list[int]] = {}
    for key, group in groups.items():
        named = [
            track_index for track_index, name in enumerate(track_names)
            if track_index != 0 and group_for_track(name)[0] == key
        ]
        if named:
            group_track_candidates[key] = named
        else:
            group_track_candidates[key] = [
                track_index for track_index, count in note_track_counts.items()
                if count == len(group)
            ]

    # The serialized conductor carries tempo but not the inferred fractional
    # phase used before source-relative tick subtraction. Infer that phase from
    # the serialized NoteOn/source-start pairs, then use it for every pitch
    # checkpoint. Without this, a source point near a half-tick boundary can be
    # assigned to the adjacent tick even though the compiler correctly folded it
    # using the map's phase.
    phase_pairs: list[tuple[float, int]] = []
    for key, group in groups.items():
        candidates = group_track_candidates[key]
        if len(candidates) != 1:
            continue
        note_ons = note_on_events[candidates[0]]
        for note, event in zip(group, note_ons):
            phase_pairs.append((
                source_tick(int(note["startSample"]), timeline, tempo_events, ppq),
                int(event["tick"]),
            ))
    if phase_tick_origin is not None:
        phase = phase_tick_origin
    elif phase_pairs:
        lower, upper = -0.499999999, 0.499999999
        for base, actual in phase_pairs:
            lower = max(lower, actual - 0.5 - base)
            upper = min(upper, actual + 0.5 - base)
        if lower < upper:
            phase = (lower + upper) / 2.0
        else:
            # Tempo values are serialized as integer microseconds, so a
            # perfectly common phase interval can be empty by a few ulps over
            # long files. Evaluate all interval boundaries and choose the
            # least-error candidate rather than hiding that mismatch.
            candidates = {-0.499999999, 0.0, 0.499999999}
            for base, actual in phase_pairs:
                candidates.add(max(-0.499999999, min(0.499999999, actual - 0.5 - base)))
                candidates.add(max(-0.499999999, min(0.499999999, actual + 0.5 - base)))
            phase = min(
                candidates,
                key=lambda candidate: (
                    sum(abs(relative_serialized_tick(base, candidate) - actual)
                        for base, actual in phase_pairs),
                    abs(candidate),
                ),
            )
    else:
        phase = 0.0

    checked_notes = 0
    checked_points = 0
    maximum_error = 0.0
    assignments: dict[int, str] = {}
    used_tracks: set[int] = set()
    for key, group in sorted(groups.items()):
        candidates = [
            track_index for track_index in group_track_candidates[key]
            if track_index not in used_tracks
        ]
        if not candidates:
            continue

        def candidate_score(track_index: int) -> tuple[int, float, int]:
            errors = [
                abs(relative_serialized_tick(
                    source_tick(int(note["startSample"]), timeline, tempo_events, ppq),
                    phase) - int(event["tick"]))
                for note, event in zip(group, note_on_events[track_index])
            ]
            matched = sum(error <= 1 for error in errors)
            total = sum(errors)
            return matched, -total, -track_index

        track_index = max(candidates, key=candidate_score)
        assignments[track_index] = key
        used_tracks.add(track_index)

    matched_groups: set[str] = set()
    for track_index, group_key in sorted(assignments.items()):
        group = groups[group_key]
        if not group:
            continue
        matched_groups.add(str(group_key))
        events = track_events[track_index]
        rpn_msb, rpn_lsb = 127, 127
        bend_range = 0
        bend = 0
        active_note: dict[str, Any] | None = None
        active_base = 0
        active_start_tick = 0
        source_index = 0
        observed: dict[int, float] = {}

        def decoded_pitch() -> float:
            denominator = 8192.0 if bend < 0 else 8191.0
            return active_base + bend / denominator * bend_range

        def validate_note(note: dict[str, Any]) -> None:
            nonlocal checked_points, maximum_error
            end_tick = round_away_from_zero(
                relative_serialized_tick(
                    source_tick(int(note["endSample"]), timeline, tempo_events, ppq), phase))
            actual_ticks = sorted(observed)
            if not actual_ticks:
                raise SmfError(f"track {track_index} has no serialized pitch state")
            # The compiler folds all source changes that quantize to one MIDI
            # tick, so the independent oracle must apply the same observable
            # last-state rule before comparing pitch.
            expected_by_tick: dict[int, tuple[int, float]] = {}
            for sample, expected_pitch in source_pitch_points(note):
                expected_tick = round_away_from_zero(
                    relative_serialized_tick(
                        source_tick(sample, timeline, tempo_events, ppq), phase))
                expected_by_tick[expected_tick] = (sample, expected_pitch)
            for expected_tick, (sample, expected_pitch) in sorted(expected_by_tick.items()):
                if expected_tick >= end_tick:
                    continue
                candidates = [tick for tick in actual_ticks if tick <= expected_tick]
                if not candidates:
                    raise SmfError(
                        f"track {track_index} has no pitch state at source sample {sample}")
                actual = observed[candidates[-1]]
                delta = expected_pitch - active_base
                step = 0.0 if bend_range == 0 else bend_range / (
                    8192.0 if delta < 0 else 8191.0)
                error = abs(actual - expected_pitch)
                maximum_error = max(maximum_error, error)
                checked_points += 1
                if error > step + 1e-8:
                    raise SmfError(
                        f"track {track_index} pitch error {error:.9f} exceeds "
                        f"quantization step {step:.9f} at source sample {sample} "
                        f"(expected {expected_pitch:.9f}, actual {actual:.9f}, "
                        f"tick {expected_tick}, note start {note['startSample']}, "
                        f"base {active_base}, range {bend_range})")

        for event in events:
            kind = event["kind"]
            channel = event.get("channel")
            if channel is None:
                continue
            if kind == "control":
                controller = int(event["controller"])
                value = int(event["data2"])
                if controller == 101:
                    rpn_msb = value
                elif controller == 100:
                    rpn_lsb = value
                elif controller == 6 and (rpn_msb, rpn_lsb) == (0, 0):
                    bend_range = value
                continue
            if kind == "pitch_bend":
                unsigned = int(event["data2"]) * 128 + int(event["data1"])
                bend = unsigned - 8192
                if active_note is not None:
                    observed[int(event["tick"])] = decoded_pitch()
                continue
            if kind == "note_on":
                if active_note is not None:
                    raise SmfError(f"track {track_index} has overlapping serialized notes")
                if source_index >= len(group):
                    raise SmfError(f"track {track_index} has too many serialized NoteOns")
                active_note = group[source_index]
                source_index += 1
                active_base = int(event["data1"])
                expected_tick = relative_serialized_tick(
                    source_tick(int(active_note["startSample"]), timeline, tempo_events, ppq),
                    phase)
                active_start_tick = int(event["tick"])
                if abs(int(event["tick"]) - expected_tick) > 1:
                    raise SmfError(
                        f"track {track_index} NoteOn tick {event['tick']} differs from "
                        f"source tick {expected_tick}")
                observed[int(event["tick"])] = decoded_pitch()
                continue
            if kind == "note_off" and active_note is not None:
                expected_end_tick = relative_serialized_tick(
                    source_tick(int(active_note["endSample"]), timeline, tempo_events, ppq),
                    phase)
                if expected_end_tick <= active_start_tick:
                    expected_end_tick = active_start_tick + 1
                if int(event["tick"]) != expected_end_tick:
                    raise SmfError(
                        f"track {track_index} NoteOff tick {event['tick']} differs from "
                        f"source tick {expected_end_tick} for note start "
                        f"{active_note['startSample']}")
                validate_note(active_note)
                active_note = None
                observed.clear()

        if active_note is not None:
            validate_note(active_note)
        if source_index != len(group):
            raise SmfError(
                f"track {track_index} serialized {source_index} notes, expected {len(group)}")
        checked_notes += source_index

    expected_groups = {str(note["voiceId"]) for note in notes if isinstance(note, dict)}
    if expected_groups - matched_groups:
        missing = ", ".join(sorted(expected_groups - matched_groups))
        raise SmfError(f"source pitch groups missing from SMF tracks: {missing}")
    return {
        "pitchNotes": checked_notes,
        "pitchCheckpoints": checked_points,
        "maxPitchError": maximum_error,
    }


def validate_source_event_timing(
    smf: dict[str, Any],
    timeline: dict[str, Any],
    phase_tick_origin: float | None,
) -> dict[str, int]:
    """Check note, native-rhythm, and sample event ticks against source time."""

    ppq = int(smf["_division"])
    tempo_events: list[tuple[int, int]] = smf["_tempoEvents"]
    track_events: list[list[dict[str, Any]]] = smf["_trackEvents"]
    track_names: list[str | None] = smf["_trackNames"]
    phase = 0.0 if phase_tick_origin is None else phase_tick_origin
    checked = 0

    rhythm = timeline.get("rhythm") or []
    rhythm_tracks = [
        index for index, name in enumerate(track_names)
        if name == "Native Rhythm"
    ]
    if rhythm:
        if len(rhythm_tracks) != 1:
            raise SmfError("source rhythm events have no unique Native Rhythm track")
        onsets = [
            event for event in track_events[rhythm_tracks[0]]
            if event["kind"] == "note_on"
        ]
        if len(onsets) != len(rhythm):
            raise SmfError(
                f"serialized rhythm attacks {len(onsets)} differ from source {len(rhythm)}")
        for source, event in zip(rhythm, onsets):
            expected = relative_serialized_tick(
                source_tick(int(source["samplePosition"]), timeline, tempo_events, ppq),
                phase)
            if int(event["tick"]) != expected:
                raise SmfError(
                    f"rhythm event tick {event['tick']} differs from source tick {expected}")
            checked += 1

    samples = timeline.get("samplePlayback") or []
    note_attack_ids = {
        str(note["sourceAttackId"])
        for note in timeline.get("notes") or []
        if isinstance(note, dict) and note.get("sourceAttackId") is not None
    }
    samples = [
        sample for sample in samples
        if not isinstance(sample, dict)
        or sample.get("sourceAttackId") is None
        or str(sample["sourceAttackId"]) not in note_attack_ids
    ]
    sample_groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for sample in samples:
        if isinstance(sample, dict) and sample.get("voiceId") is not None:
            sample_groups[str(sample["voiceId"])].append(sample)
    for track_index, name in enumerate(track_names):
        if not name or not name.startswith("Sample "):
            continue
        voice = name.removeprefix("Sample ")
        group = sample_groups.get(voice)
        if group is None:
            continue
        events = track_events[track_index]
        onsets = [event for event in events if event["kind"] == "note_on"]
        offsets = [event for event in events if event["kind"] == "note_off"]
        if len(onsets) != len(group) or len(offsets) != len(group):
            raise SmfError(
                f"sample track {name} event count does not match source voice {voice}")
        for source, onset, offset in zip(group, onsets, offsets):
            expected_on = relative_serialized_tick(
                source_tick(int(source["startSample"]), timeline, tempo_events, ppq),
                phase)
            expected_off = relative_serialized_tick(
                source_tick(int(source["endSample"]), timeline, tempo_events, ppq),
                phase)
            if expected_off <= expected_on:
                expected_off = expected_on + 1
            if int(onset["tick"]) != expected_on or int(offset["tick"]) != expected_off:
                raise SmfError(
                    f"sample track {name} timing differs from source "
                    f"({onset['tick']}/{offset['tick']} versus "
                    f"{expected_on}/{expected_off})")
            checked += 1
    if samples and not any(name and name.startswith("Sample ") for name in track_names):
        raise SmfError("source sample playback has no Sample track")
    return {"timingEvents": checked}


def validate_source_sample_pitch(
    smf: dict[str, Any],
    timeline: dict[str, Any],
) -> dict[str, int | float]:
    """Check optional absolute sample-playback pitch at serialized attacks."""

    samples = timeline.get("samplePlayback") or []
    note_attack_ids = {
        str(note["sourceAttackId"])
        for note in timeline.get("notes") or []
        if isinstance(note, dict) and note.get("sourceAttackId") is not None
    }
    samples = [
        sample for sample in samples
        if isinstance(sample, dict)
        and (sample.get("sourceAttackId") is None
             or str(sample["sourceAttackId"]) not in note_attack_ids)
    ]
    groups: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for sample in samples:
        if sample.get("voiceId") is not None:
            groups[str(sample["voiceId"])].append(sample)

    checked = 0
    maximum_error = 0.0
    track_events: list[list[dict[str, Any]]] = smf["_trackEvents"]
    for track_index, name in enumerate(smf["_trackNames"]):
        if not name or not name.startswith("Sample "):
            continue
        group = groups.get(name.removeprefix("Sample "))
        if not group:
            continue
        bend_range = 0
        bend = 0
        rpn_msb, rpn_lsb = 127, 127
        source_index = 0
        for event in track_events[track_index]:
            kind = event["kind"]
            if kind == "control":
                controller = int(event["controller"])
                value = int(event["data2"])
                if controller == 101:
                    rpn_msb = value
                elif controller == 100:
                    rpn_lsb = value
                elif controller == 6 and (rpn_msb, rpn_lsb) == (0, 0):
                    bend_range = value
            elif kind == "pitch_bend":
                bend = int(event["data2"]) * 128 + int(event["data1"]) - 8192
            elif kind == "note_on":
                if source_index >= len(group):
                    raise SmfError(f"sample track {name} has too many NoteOns")
                source = group[source_index]
                source_index += 1
                expected = source.get("midiPitch")
                if expected is None:
                    continue
                base = int(event["data1"])
                denominator = 8192.0 if bend < 0 else 8191.0
                actual = base + bend / denominator * bend_range
                delta = float(expected) - base
                step = 0.0 if bend_range == 0 else bend_range / (
                    8192.0 if delta < 0 else 8191.0)
                error = abs(actual - float(expected))
                checked += 1
                maximum_error = max(maximum_error, error)
                if error > step + 1e-8:
                    raise SmfError(
                        f"sample track {name} pitch error {error:.9f} exceeds "
                        f"quantization step {step:.9f}")
    return {"samplePitchCheckpoints": checked, "maxSamplePitchError": maximum_error}


def run_export(
    cli: Path,
    source: Path | None,
    timeline: Path | None,
    timeout: float,
) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix="mdplayer-midi-corpus-") as directory:
        output = Path(directory) / "export.mid"
        captured_timeline = Path(directory) / "captured-timeline.json"
        if source is not None:
            command = [str(cli), "midi", str(source)]
            capture = "source"
        elif timeline is not None:
            command = [str(cli), "midi", "dummy", "--timeline", str(timeline)]
            capture = "timeline"
        else:
            return {"status": "blocked", "error": "no source or timeline"}
        command += [
            "--output", str(output),
            "--timeline-out", str(captured_timeline),
            "--musical-grid",
        ]
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
        public_smf = {
            key: value for key, value in smf.items() if not key.startswith("_")
        }
        pitch: dict[str, int | float] | None = None
        timing: dict[str, int] | None = None
        validation_timeline = captured_timeline if captured_timeline.exists() else timeline
        if validation_timeline is not None and validation_timeline.exists():
            try:
                timeline_data = json.loads(validation_timeline.read_text(encoding="utf-8"))
                phase_match = re.search(
                    r"source-quarter-at-start: ([+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?)",
                    text,
                )
                phase_tick_origin = (
                    float(phase_match.group(1)) * int(smf["_division"])
                    if phase_match is not None else None
                )
                pitch = validate_source_pitch(smf, timeline_data, phase_tick_origin)
                timing = validate_source_event_timing(smf, timeline_data, phase_tick_origin)
                pitch.update(validate_source_sample_pitch(smf, timeline_data))
            except (OSError, ValueError, KeyError, TypeError, SmfError) as error:
                return {
                    "status": "failed",
                    "capture": capture,
                    "error": f"source pitch round trip failed: {error}",
                }
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
            "smf": public_smf,
            "pitch": pitch,
            "timing": timing,
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
