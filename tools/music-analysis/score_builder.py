from __future__ import annotations

from bisect import bisect_left


class AnalysisContext(list):
    """Canonical note collection plus deterministic indexes for full analysis.

    It remains a list for compatibility with the worker's existing analyzers,
    while making the repeated structural lookups O(log n) or O(1).
    """

    def __init__(self, notes=()):
        super().__init__(notes)
        self.by_channel = {}
        self.by_onset = {}
        self._onsets = []
        self._score = None
        for note in self:
            channel = note.get("channelId", "")
            self.by_channel.setdefault(channel, []).append(note)
            onset = int(note.get("startSample", 0))
            self.by_onset.setdefault(onset, []).append(note)
        for values in self.by_channel.values():
            values.sort(key=lambda n: (n.get("startSample", 0), n.get("id", "")))
        self._onsets = sorted(self.by_onset)

    def notes_in_window(self, start, end):
        """Return notes beginning before *end*, retaining overlap semantics."""
        left = bisect_left(self._onsets, start)
        right = bisect_left(self._onsets, end)
        earlier = []
        if left:
            earlier = [n for n in self.by_onset[self._onsets[left - 1]]
                       if n.get("endSample", 0) > start]
        return earlier + [n for onset in self._onsets[left:right] for n in self.by_onset[onset]]

    def score(self, factory):
        if self._score is None:
            self._score = factory()
        return self._score


def _theory_pitch(note, pitch):
    if note.get("microtonal", False):
        return False
    pitch_class = note.get("pitchClass", -1)
    if pitch_class is None or pitch_class == -1:
        return abs(pitch - round(pitch)) * 100 <= 35
    if isinstance(pitch_class, bool) or not isinstance(pitch_class, (int, float)):
        return False
    if int(pitch_class) not in range(12):
        return False
    return abs(pitch - round(pitch)) * 100 <= 35


def _insert_region(part, note, channel_id, sample_rate, region, region_index, region_count):
    import music21

    start = max(int(note["startSample"]), int(region.get("startSample", note["startSample"])))
    end = min(int(note["endSample"]), int(region.get("endSample", note["endSample"])))
    if end <= start:
        return
    pitch = float(region.get("midiPitch", note["pitch"]))
    obj = music21.note.Note(round(pitch))
    obj.offset = start / sample_rate
    obj.duration.quarterLength = (end - start) / sample_rate
    obj.editorial.mdplayer_id = note.get("id")
    obj.editorial.channel_id = channel_id
    if region_count > 1:
        obj.tie = music21.tie.Tie(
            "start" if region_index == 0 else "stop" if region_index == region_count - 1 else "continue"
        )
    part.insert(obj)


def build_score(data, detail="standard"):
    import music21

    score = music21.stream.Score()
    notes = []
    warnings = []
    has_gliding_note = False
    sample_rate = float(data.get("sampleRate", 1) or 1)
    for channel in sorted(data.get("channels", []), key=lambda c: c.get("id", "")):
        channel_id = channel.get("id", "unknown")
        channel_kind = channel.get("kind", "unknown")
        part = music21.stream.Part(id=channel_id)
        score.insert(0, part)
        channel_weight = float(channel.get("analysisWeight", 1.0) or 0.0)
        for source in sorted(
            channel.get("notes", []), key=lambda x: (x.get("startSample", 0), x.get("id", ""))
        ):
            start = int(source.get("startSample", 0))
            end = int(source.get("endSample", start))
            if end <= start:
                continue
            is_pitched = (
                bool(source.get("isPitched", True))
                and not source.get("isNoise", False)
                and channel_kind not in {"rhythm", "ssg-noise"}
            )
            pitch_value = source.get("structuralMidiPitch")
            pitch = float(pitch_value) if pitch_value is not None else -1.0
            if not is_pitched:
                notes.append({
                    **source,
                    "channelId": channel_id,
                    "channelKind": channel_kind,
                    "channelWeight": channel_weight,
                    "offset": start / sample_rate,
                    "duration": (end - start) / sample_rate,
                    "pitch": -1.0,
                    "pitchClass": -1,
                    "theoryPitched": False,
                })
                continue
            if not 0 <= pitch <= 127:
                continue
            pitch_class = source.get("pitchClass", -1)
            pitch_class = int(pitch_class) if pitch_class is not None else -1
            theory_pitched = _theory_pitch(source, pitch)
            if theory_pitched and pitch_class not in range(12):
                pitch_class = int(round(pitch)) % 12
            entry = {
                **source,
                "channelId": channel_id,
                "channelKind": channel_kind,
                "channelWeight": channel_weight,
                "offset": start / sample_rate,
                "duration": (end - start) / sample_rate,
                "pitch": pitch,
                "pitchClass": pitch_class if theory_pitched else -1,
                "theoryPitched": theory_pitched,
            }
            notes.append(entry)
            has_gliding_note = has_gliding_note or bool(source.get("gliding", False))
            if not theory_pitched or (channel_kind == "fm3-operator" and detail != "full"):
                continue
            regions = [
                region for region in source.get("pitchRegions", [])
                if int(region.get("endSample", 0)) > int(region.get("startSample", 0))
            ]
            if not regions:
                regions = [{"startSample": start, "endSample": end, "midiPitch": pitch}]
            for region_index, region in enumerate(regions):
                _insert_region(part, entry, channel_id, sample_rate, region, region_index, len(regions))
    if has_gliding_note:
        warnings.append({
            "code": "STRUCTURAL_GLIDE",
            "message": "One or more notes contain an unresolved continuous pitch glide; structural pitch was retained without fabricating intermediate notes.",
        })
    return score, AnalysisContext(notes), warnings
