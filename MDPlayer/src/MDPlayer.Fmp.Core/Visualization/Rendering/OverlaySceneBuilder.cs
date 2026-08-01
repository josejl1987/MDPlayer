using Fmp.Core.Metadata;
using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Builds an immutable <see cref="OverlayScene"/> from a
/// <see cref="VisualizationTimeline"/> and layout. Pre-sorts notes,
/// resolves colors, and precomputes pitch ranges. The resulting scene is
/// consumed read-only by the CPU overlay renderer, so no color resolution,
/// sorting, or string formatting occurs on the per-frame hot path.
/// </summary>
internal static class OverlaySceneBuilder
{
    /// <summary>
    /// Builds a prepared scene from the timeline and layout.
    /// <paramref name="samplesPerFrame"/> is the output frame duration in
    /// samples; it enables deterministic hard key-off detection (§8.6) at
    /// preparation time. When zero (legacy callers), every note is treated as
    /// a normal release.
    /// </summary>
    public static OverlayScene Build(
        VisualizationTimeline timeline,
        OverlayLayout layout,
        VisualizationMetadata metadata = null,
        double samplesPerFrame = 0,
        NoteColorMode noteColorMode = NoteColorMode.Instrument)
        => Build(
            timeline,
            layout,
            VisualizationTopologyBuilder.Build(timeline),
            metadata,
            samplesPerFrame,
            noteColorMode);

    public static OverlayScene Build(
        VisualizationTimeline timeline,
        OverlayLayout layout,
        VisualizationTopology topology,
        VisualizationMetadata metadata = null,
        double samplesPerFrame = 0,
        NoteColorMode noteColorMode = NoteColorMode.Instrument)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(topology);

        var instrumentById = timeline.Instruments.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var panels = new PreparedPanel[topology.Panels.Count];
        for (int index = 0; index < panels.Length; index++)
        {
            VisualizationPanel topologyPanel = topology.Panels[index];
            string id = topologyPanel.Id;
            var kind = topologyPanel.Kind;
            double pitchTolerance = PitchToleranceSemitones(layout, index, kind);

            var mainNotes = (kind is PreparedPanelKind.Pitched or PreparedPanelKind.Fm3 or PreparedPanelKind.Ssg)
                ? ToPreparedNotes(
                    timeline.Notes.Where(n => topologyPanel.VoiceIds.Contains(n.ChannelId, StringComparer.Ordinal)),
                    pitchTolerance,
                    samplesPerFrame,
                    timeline.SampleRate,
                    index,
                    noteColorMode,
                    instrumentById)
                : Array.Empty<PreparedNote>();

            var operatorNotes = kind == PreparedPanelKind.Fm3
                ? Enumerable.Range(0, 4).Select(op =>
                    ToPreparedNotes(
                        timeline.Notes.Where(n => topologyPanel.OperatorVoiceIds.Contains(n.ChannelId, StringComparer.Ordinal)
                            && n.ChannelId.EndsWith($".{op + 1}", StringComparison.Ordinal)),
                        pitchTolerance,
                        samplesPerFrame,
                        timeline.SampleRate,
                        index,
                        noteColorMode,
                        instrumentById)
                ).ToArray()
                : Array.Empty<PreparedNote[]>();

            PreparedNote[] cameraNotes = kind == PreparedPanelKind.Fm3
                ? mainNotes.Concat(operatorNotes.SelectMany(notes => notes)).ToArray()
                : mainNotes;

            var rhythm = kind == PreparedPanelKind.Rhythm
                ? timeline.Rhythm
                    .Where(e => topologyPanel.VoiceIds.Any(id =>
                        string.Equals(e.ChannelId, id, StringComparison.Ordinal)
                        || e.ChannelId.StartsWith(id + ".", StringComparison.Ordinal)))
                    .OrderBy(e => e.SamplePosition)
                    .ThenBy(e => e.Voice, StringComparer.Ordinal)
                    .Select(e => new PreparedRhythmEvent
                    {
                        Voice = e.Voice,
                        SamplePosition = e.SamplePosition,
                        Strength = e.Strength,
                        Pan = e.Pan,
                    })
                    .ToArray()
                : Array.Empty<PreparedRhythmEvent>();

            Ppz8Event[] ppz8 = string.Equals(id, "ppz8.0", StringComparison.Ordinal)
                ? timeline.Ppz8
                    .Where(value => value.EndSample > value.StartSample)
                    .OrderBy(value => value.StartSample)
                    .ThenBy(value => value.Channel)
                    .ToArray()
                : Array.Empty<Ppz8Event>();

            AdpcmBEvent[] adpcmB = id.EndsWith(".adpcm-b", StringComparison.Ordinal)
                ? timeline.AdpcmB
                    .Where(value => value.EndSample > value.StartSample)
                    .OrderBy(value => value.StartSample)
                    .ToArray()
                : Array.Empty<AdpcmBEvent>();

            // Compute pitch range from visible notes.
            var (minMidi, maxMidi) = ComputePitchRange(mainNotes);

            bool hasTrackEvents = kind switch
            {
                PreparedPanelKind.Fm3 =>
                    mainNotes.Length > 0 ||
                    Array.Exists(operatorNotes, notes => notes.Length > 0),
                PreparedPanelKind.Pitched or PreparedPanelKind.Ssg =>
                    mainNotes.Length > 0,
                PreparedPanelKind.Rhythm =>
                    rhythm.Length > 0,
                PreparedPanelKind.Placeholder =>
                    ppz8.Length > 0 || adpcmB.Length > 0,
                _ => false,
            };

            panels[index] = new PreparedPanel
            {
                Index = index,
                Id = id,
                Label = topologyPanel.Label,
                Kind = kind,
                MainNotes = mainNotes,
                InstrumentChanges = mainNotes.Where(note => note.HasInstrumentChange).ToArray(),
                OperatorNotes = operatorNotes,
                CameraNotes = cameraNotes,
                Rhythm = rhythm,
                Ppz8 = ppz8,
                AdpcmB = adpcmB,
                MinMidi = minMidi,
                MaxMidi = maxMidi,
                Accent = InstrumentColorResolver.ResolveChannelAccent(index),
                HasTrackEvents = hasTrackEvents,
            };
        }

        return new OverlayScene
        {
            Layout = layout,
            Topology = topology,
            Panels = panels,
            Metadata = metadata ?? new VisualizationMetadata(),
            SampleRate = timeline.SampleRate,
            StartSample = timeline.StartSample,
            EndSample = timeline.EndSample,
        };
    }

    /// <summary>
    /// Sorts, validates, and converts the channel's <see cref="NoteEvent"/>s
    /// into prepared notes, resolving each note's release style (§8.6) from
    /// its successor on the same channel.
    /// </summary>
    private static PreparedNote[] ToPreparedNotes(
        IEnumerable<NoteEvent> source,
        double pitchToleranceSemitones,
        double samplesPerFrame,
        int sampleRate,
        int panelIndex,
        NoteColorMode noteColorMode,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments)
    {
        NoteEvent[] ordered = source
            .Where(IsNoteValid)
            .OrderBy(n => n.StartSample)
            .ThenBy(n => n.EndSample)
            .ToArray();

        var prepared = new PreparedNote[ordered.Length];
        bool[] dense = ComputeDenseOnsets(ordered, sampleRate);
        for (int index = 0; index < ordered.Length; index++)
        {
            // §13.2: detect instrument changes by comparing consecutive notes'
            // InstrumentId. The change is attributed to the note that introduces
            // the new instrument, so the overlay appears at the new note's onset.
            bool hasChange = index > 0
                && !string.Equals(ordered[index].InstrumentId, ordered[index - 1].InstrumentId, StringComparison.Ordinal);
            long changeSample = hasChange ? ordered[index].StartSample : 0;

            prepared[index] = ToPreparedNote(
                ordered[index],
                pitchToleranceSemitones,
                ResolveReleaseStyle(ordered, index, samplesPerFrame),
                dense[index],
                hasChange,
                changeSample,
                panelIndex,
                noteColorMode,
                instruments);
        }
        return prepared;
    }

    /// <summary>
    /// Marks notes whose onset falls inside a 100 ms window containing more
    /// than 8 onsets (§9.3). A dense onset suppresses the ripple alpha and the
    /// active-flash size enlargement but never the onset caps. Uses a
    /// two-pointer sweep over the sorted onset array: note <c>i</c> is dense
    /// when more than 8 notes (including <c>i</c>) start within
    /// <c>[onset_i, onset_i + 100ms)</c>.
    /// </summary>
    private static bool[] ComputeDenseOnsets(NoteEvent[] ordered, int sampleRate)
    {
        int n = ordered.Length;
        var dense = new bool[n];
        if (n == 0 || sampleRate <= 0)
            return dense;

        long windowSamples = (long)Math.Round(0.1 * sampleRate); // 100 ms
        int threshold = 8; // more than 8 onsets → dense
        int right = 0;
        for (int i = 0; i < n; i++)
        {
            if (right < i)
                right = i;
            long windowEnd = ordered[i].StartSample + windowSamples;
            while (right < n && ordered[right].StartSample < windowEnd)
                right++;
            int count = right - i;
            if (count > threshold)
            {
                for (int j = i; j < right; j++)
                    dense[j] = true;
            }
        }
        return dense;
    }

    /// <summary>
    /// Determines a note's release style (§8.6) at preparation time. A note is
    /// a hard key-off when the next note on the same channel begins within one
    /// output frame — its key was cut abruptly to play the next note. Without
    /// a frame duration (legacy callers) every note is a normal release.
    /// </summary>
    private static NoteReleaseStyle ResolveReleaseStyle(
        NoteEvent[] ordered,
        int index,
        double samplesPerFrame)
    {
        if (samplesPerFrame <= 0 || index + 1 >= ordered.Length)
            return NoteReleaseStyle.Normal;
        long gapSamples = ordered[index + 1].StartSample - ordered[index].EndSample;
        return gapSamples <= samplesPerFrame ? NoteReleaseStyle.HardKeyOff : NoteReleaseStyle.Normal;
    }

    /// <summary>
    /// Converts a timeline <see cref="NoteEvent"/> into a <see cref="PreparedNote"/>
    /// with pre-resolved colors and a simplified, sorted pitch contour, so the
    /// per-frame hot path performs no color calculation, sorting, or
    /// simplification.
    /// </summary>
    private static PreparedNote ToPreparedNote(
        NoteEvent note,
        double pitchToleranceSemitones,
        NoteReleaseStyle releaseStyle,
        bool isDenseOnset,
        bool hasInstrumentChange = false,
        long instrumentChangeSample = 0,
        int panelIndex = 0,
        NoteColorMode noteColorMode = NoteColorMode.Instrument,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments = null)
    {
        var fill = InstrumentColorResolver.ResolveFill(
            noteColorMode, note.InstrumentId, panelIndex, note.InitialMidiNote);
        var activeFill = fill.Lighten(0.3);

        return new PreparedNote
        {
            StartSample = note.StartSample,
            EndSample = note.EndSample,
            InitialMidiNote = note.InitialMidiNote,
            Mode = note.Mode,
            InstrumentId = note.InstrumentId,
            Text = PrepareInstrumentText(note.InstrumentId, instruments),
            IsRetrigger = note.IsRetrigger,
            IsDenseOnset = isDenseOnset,
            HasInstrumentChange = hasInstrumentChange,
            InstrumentChangeSample = instrumentChangeSample,
            ReleaseStyle = releaseStyle,
            Fill = fill,
            ActiveFill = activeFill,
            CapFill = fill.Lighten(0.45),
            Accent = fill.Lighten(0.5),
            Pitch = BuildPitchPoints(note, pitchToleranceSemitones),
        };
    }

    private static PreparedInstrumentText PrepareInstrumentText(
        string instrumentId,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments)
    {
        if (string.IsNullOrWhiteSpace(instrumentId))
            return PreparedInstrumentText.Empty;

        if (instruments != null && instruments.TryGetValue(instrumentId, out InstrumentDefinition definition))
        {
            string shortLabel = definition.Algorithm.HasValue
                ? "ALG" + definition.Algorithm.Value
                : "";
            var parts = new List<string>(4);
            if (definition.Algorithm.HasValue) parts.Add("ALG " + definition.Algorithm.Value);
            if (definition.Feedback.HasValue && definition.Feedback.Value != 0) parts.Add("FB " + definition.Feedback.Value);
            if (definition.Ams.HasValue && definition.Ams.Value != 0) parts.Add("AMS " + definition.Ams.Value);
            if (definition.Fms.HasValue && definition.Fms.Value != 0) parts.Add("FMS " + definition.Fms.Value);
            string changeLabel = string.Join("  ", parts);
            var badges = new List<string>(3);
            if (definition.Feedback is > 0) badges.Add("FB" + definition.Feedback.Value);
            if (definition.Ams is > 0) badges.Add("AMS" + definition.Ams.Value);
            if (definition.Fms is > 0) badges.Add("FMS" + definition.Fms.Value);
            string badgeLabel = string.Join(" ", badges);
            string[] operatorLabels = ["OP1", "OP2", "OP3", "OP4"];
            if (shortLabel.Length > 0 || changeLabel.Length > 0)
                return new PreparedInstrumentText(shortLabel, changeLabel, badgeLabel, operatorLabels);
        }

        int separator = instrumentId.LastIndexOf(':');
        string suffix = separator >= 0 ? instrumentId[(separator + 1)..] : instrumentId;
        if (suffix.Length > 8)
            suffix = suffix[..8];
        return new PreparedInstrumentText(suffix.ToUpperInvariant(), "", "", ["OP1", "OP2", "OP3", "OP4"]);
    }

    /// <summary>
    /// The simplification tolerance for a panel, in semitones, that maps the
    /// Visualization 2.0 §8.4 bound of 0.15 vertical pixels onto the rendered
    /// pitch scale. The scale used is the tightest zoom the panel can show:
    /// the PitchCamera's minimum 12-semitone span for main lanes, and the
    /// fixed ±2-semitone operator-ribbon mapping for FM3 operator lanes.
    /// </summary>
    private static double PitchToleranceSemitones(OverlayLayout layout, int index, PreparedPanelKind kind)
    {
        if (kind == PreparedPanelKind.Fm3)
        {
            int operatorRowHeight = Math.Max(1, layout.GetFm3OperatorRect(index).Height / 4);
            return 4.0 * 0.15 / operatorRowHeight;
        }

        int laneHeight = layout.GetPitchedLaneRect(index, kind == PreparedPanelKind.Fm3).Height;
        return 12.0 * 0.15 / Math.Max(1, laneHeight);
    }

    /// <summary>
    /// Builds the prepared pitch contour for a note: keeps valid (finite,
    /// pitched) points, sorts by sample position, collapses same-sample
    /// duplicates, then simplifies with a bounded vertical error (§8.4).
    /// </summary>
    private static PreparedPitchPoint[] BuildPitchPoints(NoteEvent note, double toleranceSemitones)
    {
        var valid = new List<PreparedPitchPoint>(Math.Min(note.Pitch.Count, 64));
        foreach (PitchChange p in note.Pitch)
        {
            if (!double.IsFinite(p.MidiNote) || p.MidiNote < 0)
                continue;
            if (!double.IsFinite(p.SamplePosition))
                continue;
            valid.Add(new PreparedPitchPoint(p.SamplePosition, p.MidiNote));
        }
        if (valid.Count == 0)
            return Array.Empty<PreparedPitchPoint>();

        valid.Sort(static (a, b) => a.SamplePosition.CompareTo(b.SamplePosition));

        var deduplicated = new List<PreparedPitchPoint>(valid.Count);
        foreach (var point in valid)
        {
            if (deduplicated.Count > 0 && deduplicated[^1].SamplePosition == point.SamplePosition)
                deduplicated[^1] = point; // same sample: keep the latest value
            else
                deduplicated.Add(point);
        }

        return SimplifyPitch(deduplicated.ToArray(), toleranceSemitones);
    }

    /// <summary>
    /// Ramer–Douglas–Peucker-style simplification with a vertical (pitch)
    /// error bound. Points that may never be discarded (§8.4) act as hard
    /// anchors: the first and last points, local pitch extrema, and points
    /// adjacent to a change larger than 0.1 semitone. Between anchors the
    /// contour is simplified so no original point deviates from the retained
    /// polyline by more than <paramref name="toleranceSemitones"/>.
    /// </summary>
    private static PreparedPitchPoint[] SimplifyPitch(PreparedPitchPoint[] points, double toleranceSemitones)
    {
        if (points.Length <= 2 || toleranceSemitones <= 0)
            return points;

        var keep = new bool[points.Length];
        keep[0] = true;
        keep[^1] = true;
        for (int i = 1; i < points.Length - 1; i++)
        {
            if (IsProtected(points, i))
                keep[i] = true;
        }

        // Recursive RDP over each run between consecutive protected anchors,
        // using an explicit stack (preparation time; no per-frame cost).
        var stack = new Stack<(int First, int Last)>();
        int runStart = 0;
        for (int i = 1; i < points.Length; i++)
        {
            if (keep[i])
            {
                if (i - runStart > 1)
                    stack.Push((runStart, i));
                runStart = i;
            }
        }

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last - first < 2)
                continue;

            long spanSamples = points[last].SamplePosition - points[first].SamplePosition;
            if (spanSamples <= 0)
                continue;

            double slope = (points[last].MidiNote - points[first].MidiNote) / spanSamples;
            double maxDeviation = 0;
            int maxIndex = -1;
            for (int i = first + 1; i < last; i++)
            {
                double chordMidi = points[first].MidiNote
                    + slope * (points[i].SamplePosition - points[first].SamplePosition);
                double deviation = Math.Abs(points[i].MidiNote - chordMidi);
                if (deviation > maxDeviation)
                {
                    maxDeviation = deviation;
                    maxIndex = i;
                }
            }

            if (maxIndex >= 0 && maxDeviation > toleranceSemitones)
            {
                keep[maxIndex] = true;
                stack.Push((first, maxIndex));
                stack.Push((maxIndex, last));
            }
        }

        var result = new List<PreparedPitchPoint>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Points that the simplification must never discard (§8.4).
    /// </summary>
    private static bool IsProtected(PreparedPitchPoint[] points, int i)
    {
        double previous = points[i - 1].MidiNote;
        double current = points[i].MidiNote;
        double next = points[i + 1].MidiNote;

        // Local pitch extremum (keeps vibrato peaks and valleys intact).
        if (current > previous && current > next)
            return true;
        if (current < previous && current < next)
            return true;

        // Adjacent to a change larger than 0.1 semitone.
        if (Math.Abs(current - previous) > 0.1)
            return true;
        if (Math.Abs(next - current) > 0.1)
            return true;

        return false;
    }

    /// <summary>
    /// Validates a note for preparation. Rejects notes with
    /// non-positive duration, invalid MIDI pitch, or non-finite values.
    /// </summary>
    private static bool IsNoteValid(NoteEvent note)
    {
        if (note.EndSample <= note.StartSample)
            return false;
        if (!double.IsFinite(note.InitialMidiNote))
            return false;
        // Reject pitch points outside note bounds or with non-finite values.
        foreach (var p in note.Pitch)
        {
            if (!double.IsFinite(p.MidiNote))
                return false;
            if (p.SamplePosition < note.StartSample || p.SamplePosition > note.EndSample)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Computes a pitch range for the visible notes using a duration-weighted
    /// percentile approach. Longer notes contribute proportionally more to
    /// the range, preventing short glitches from expanding it.
    /// </summary>
    private static (double min, double max) ComputePitchRange(PreparedNote[] notes)
    {
        if (notes.Length == 0)
            return (48, 72);

        // Collect pitch values weighted by note duration.
        // Each note contributes its pitch endpoints, repeated proportionally
        // to its duration (rounded to integer weight, minimum 1).
        var values = new List<double>();
        foreach (var note in notes)
        {
            double duration = note.EndSample - note.StartSample;
            if (duration <= 0) continue;

            // Weight = duration in "centiseconds" (min 1, max 100) to avoid
            // excessive list growth while preserving proportional influence.
            int weight = Math.Clamp((int)Math.Round(duration / 1000.0), 1, 100);

            if (note.Pitch.Length > 0)
            {
                // Add all pitch contour points, weighted by duration.
                foreach (var p in note.Pitch)
                {
                    if (double.IsFinite(p.MidiNote))
                        for (int w = 0; w < weight; w++)
                            values.Add(p.MidiNote);
                }
            }
            else
            {
                double midi = note.InitialMidiNote;
                if (double.IsFinite(midi))
                    for (int w = 0; w < weight; w++)
                        values.Add(midi);
            }
        }

        if (values.Count == 0)
            return (48, 72);

        values.Sort();

        // Use 5th-95th percentile to ignore extreme spikes.
        // For small arrays, use the full range.
        int p5, p95;
        if (values.Count <= 4)
        {
            p5 = 0;
            p95 = values.Count - 1;
        }
        else
        {
            p5 = (int)Math.Round(values.Count * 0.05);
            p95 = (int)Math.Round(values.Count * 0.95);
            p5 = Math.Clamp(p5, 0, values.Count - 1);
            p95 = Math.Clamp(p95, 0, values.Count - 1);
        }

        double min = values[p5];
        double max = values[p95];

        // Ensure minimum range.
        if (max - min < 6)
        {
            double center = (min + max) / 2;
            min = center - 3;
            max = center + 3;
        }

        // Add padding.
        min -= 2;
        max += 2;

        return (min, max);
    }
}
