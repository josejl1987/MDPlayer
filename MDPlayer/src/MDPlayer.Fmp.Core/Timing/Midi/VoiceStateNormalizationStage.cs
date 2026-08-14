namespace Fmp.Core.Midi;

using Fmp.Core.Visualization;

/// <summary>
/// Voice-state normalization stage (INV3-INV7): the bridge between the raw
/// chip-decoded note timeline and the semantic note/pitch/trigger timeline the
/// MIDI planner serializes. Runs BEFORE pitch normalization inside
/// <see cref="MusicalMidiExporter.Export"/>, so both the CLI and the Application
/// export paths see only normalized voice states.
///
/// The stage is a pure function of the note timeline and enforces:
/// - INV5 — no melodic note whose effective pitch (initial + every change) is
///   outside the MIDI pitch domain [0, 127] or non-finite is emitted; the note
///   is SUPPRESSED, never clamped. Intentional unpitched-noise notes (SSG noise
///   modes) are left untouched for the exporter's own noise exclusion.
/// - INV6 — a transient (≤ ~1 ms) out-of-domain span inside a note is bridged
///   (dropped), and a short invalid-pitch note between two valid notes of the
///   same pitch is suppressed and its flanks merged, so it never interrupts a
///   sustained note.
/// - INV7 — adjacent segments on the same voice/instrument/mode whose effective
///   pitch joins within 1e-6 semitones (an exact continuity match, NOT a
///   rounded-pitch match — legitimate SCC fast runs are never swallowed) are
///   merged unconditionally when the gap maps to zero MIDI ticks, or when the
///   gap is exactly a bridged invalid note (≤ ~1 ms). Merges never cross a loop
///   marker: notes must not silently span a loop pass.
/// - INV3 — unchanged melodic state therefore cannot cause NoteOff + NoteOn:
///   the only remaining note boundaries are real pitch changes, gate changes,
///   or loop points.
/// </summary>
internal static class VoiceStateNormalizationStage
{
    /// <summary>Exact-continuity tolerance for "identical effective pitch" (INV7):
    /// 1e-6 semitones — deliberately NOT a rounded-pitch match so genuine SCC fast
    /// runs (consecutive distinct pitches) are never merged away.</summary>
    private const double PitchEqualityEpsilon = 1e-6;

    /// <summary>Upper MIDI pitch domain bound (INV5).</summary>
    private const double MaxMidiNote = 127.0;

    /// <summary>Lower MIDI pitch domain bound (INV5).</summary>
    private const double MinMidiNote = 0.0;

    /// <summary>
    /// Returns a timeline whose note list is the normalized voice-state timeline.
    /// All adjacency decisions remain in source-sample time; MIDI tick collisions
    /// are intentionally left to the later MIDI planning stage.
    /// </summary>
    public static VisualizationTimeline Normalize(
        VisualizationTimeline timeline,
        List<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        IReadOnlyList<NoteEvent> source = timeline.Notes ?? Array.Empty<NoteEvent>();
        long transientWindow = Math.Max(1, timeline.SampleRate / 1000); // ~1 ms

        var bridged = new HashSet<NoteEvent>();   // notes suppressed by the INV6 bridge
        var normalized = new List<NoteEvent>(source.Count);
        int suppressedCount = 0;
        foreach (NoteEvent note in source)
        {
            if (note.EndSample <= note.StartSample)
                continue; // degenerate segment: unrepresentable, dropped
            NoteEvent bridgedNote = BridgeTransientChanges(note, transientWindow);
            if (bridgedNote is null)
                continue;
            if (!InDomain(bridgedNote))
            {
                suppressedCount++;
                bridged.Add(note);
                continue;
            }
            normalized.Add(bridgedNote);
        }

        if (normalized.Count > 0)
            normalized = MergeAdjacentSegments(normalized, timeline, transientWindow, bridged);

        bool notesChanged = !(normalized.Count == source.Count && normalized.SequenceEqual(source));
        IReadOnlyList<RhythmEvent> sourceRhythm = timeline.Rhythm ?? Array.Empty<RhythmEvent>();
        IReadOnlyList<RhythmEvent> rhythm = DeduplicateRhythm(sourceRhythm);
        bool rhythmChanged = !rhythm.SequenceEqual(sourceRhythm);

        if (warnings is not null)
        {
            if (suppressedCount > 0)
                warnings.Add($"voice-state normalization: {suppressedCount} note(s) suppressed — effective " +
                    "pitch outside the MIDI domain [0, 127] (garbage/init states never become MIDI notes)");
            int mergedCount = source.Count - normalized.Count - suppressedCount;
            if (mergedCount > 0)
                warnings.Add($"voice-state normalization: {mergedCount} adjacent segment(s) merged — " +
                    "unchanged melodic state no longer causes NoteOff+NoteOn");
            int rhythmDuplicates = sourceRhythm.Count - rhythm.Count;
            if (rhythmDuplicates > 0)
                warnings.Add($"voice-state normalization: {rhythmDuplicates} duplicate rhythm event(s) coalesced — " +
                    "one trigger per (timestamp, voice, instrument) identity");
        }

        if (!notesChanged && !rhythmChanged)
            return timeline;

        return new VisualizationTimeline
        {
            SchemaVersion = timeline.SchemaVersion,
            SampleRate = timeline.SampleRate,
            StartSample = timeline.StartSample,
            EndSample = timeline.EndSample,
            Source = timeline.Source,
            StopReason = timeline.StopReason,
            Devices = timeline.Devices,
            Voices = timeline.Voices,
            Notes = notesChanged ? normalized : timeline.Notes,
            Rhythm = rhythmChanged ? rhythm : timeline.Rhythm,
            Ppz8 = timeline.Ppz8,
            AdpcmB = timeline.AdpcmB,
            Waveforms = timeline.Waveforms,
            Samples = timeline.Samples,
            WaveformChanges = timeline.WaveformChanges,
            SamplePlayback = timeline.SamplePlayback,
            SpcVoiceStates = timeline.SpcVoiceStates,
            NoiseStates = timeline.NoiseStates,
            AggregateHits = timeline.AggregateHits,
            Timing = timeline.Timing,
            Beats = timeline.Beats,
            LoopMarkers = timeline.LoopMarkers,
            Instruments = timeline.Instruments,
            Capabilities = timeline.Capabilities,
            Warnings = timeline.Warnings,
        };
    }

    /// <summary>
    /// Coalesces rhythm events that are equivalent at the sampling identity level:
    /// same source timestamp, same voice (channel) identity, same instrument/sample
    /// identity. Repeated sink observations of an unchanged active noise/percussion
    /// state therefore collapse to one trigger, without removing distinct hits.
    /// Order is preserved (first occurrence wins).
    /// </summary>
    private static IReadOnlyList<RhythmEvent> DeduplicateRhythm(IReadOnlyList<RhythmEvent> source)
    {
        if (source.Count < 2)
            return source;
        var seen = new HashSet<(long Sample, string ChannelId, string InstrumentId)>();
        var result = new List<RhythmEvent>(source.Count);
        foreach (RhythmEvent rhythm in source)
        {
            if (!seen.Add((rhythm.SamplePosition, rhythm.ChannelId, rhythm.InstrumentId)))
                continue;
            result.Add(rhythm);
        }
        return result.Count == source.Count ? source : result;
    }

    /// <summary>True when the pitch point is a finite MIDI-domain pitch.</summary>
    private static bool InDomain(double midiNote) =>
        double.IsFinite(midiNote) && midiNote >= MinMidiNote && midiNote <= MaxMidiNote;

    /// <summary>
    /// INV5 whole-note test: a melodic note is in domain only when its initial
    /// pitch AND every retained pitch change are finite MIDI-domain pitches.
    /// Intentional unpitched-noise modes are exempt (the exporter excludes them).
    /// </summary>
    private static bool InDomain(NoteEvent note)
    {
        if (IsIntentionalUnpitchedNoise(note))
            return true;
        if (note.Domain is not null)
            return true; // chip voices re-express out-of-range pitch via the exporter's bend-range expansion
        if (!InDomain(note.InitialMidiNote))
            return false;
        IReadOnlyList<PitchChange> changes = note.Pitch ?? Array.Empty<PitchChange>();
        for (int i = 0; i < changes.Count; i++)
        {
            if (!InDomain(changes[i].MidiNote))
                return false;
        }
        return true;
    }

    /// <summary>True for the intentional unpitched-noise modes the exporter itself
    /// excludes from the melodic export; their -1 sentinel is not garbage.</summary>
    private static bool IsIntentionalUnpitchedNoise(NoteEvent note) =>
        note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise;

    /// <summary>
    /// INV6 within-note bridging: drops out-of-domain pitch changes whose span is
    /// ≤ <paramref name="transientWindow"/> samples and whose neighbors are valid,
    /// so a sub-ms invalid blip never splits or corrupts a sustained note. A
    /// trailing invalid run of ≤ the window is clipped from the note's end (the
    /// note keeps only its representable extent); a leading invalid initial pitch
    /// suppresses the note (there is no valid state before it to bridge to).
    /// Returns null when the note has no representable extent left.
    /// </summary>
    private static NoteEvent? BridgeTransientChanges(NoteEvent note, long transientWindow)
    {
        if (IsIntentionalUnpitchedNoise(note))
            return note;
        if (!InDomain(note.InitialMidiNote))
            return note.Domain is not null && double.IsFinite(note.InitialMidiNote)
                ? note
                : null;
        IReadOnlyList<PitchChange> changes = note.Pitch ?? Array.Empty<PitchChange>();
        if (changes.Count == 0)
            return note;

        var retained = new List<PitchChange>(changes.Count);
        int index = 0;
        while (index < changes.Count)
        {
            if (InDomain(changes[index].MidiNote))
            {
                retained.Add(changes[index]);
                index++;
                continue;
            }

            // Out-of-domain run [index, end).
            int runStart = index;
            while (index < changes.Count && !InDomain(changes[index].MidiNote))
                index++;
            double previousPitch = retained.Count > 0 ? retained[^1].MidiNote : note.InitialMidiNote;
            bool hasNextValid = index < changes.Count; // the run stops at the first in-domain change
            long runEndSample = index < changes.Count ? changes[index].SamplePosition : note.EndSample;
            long span = runEndSample - changes[runStart].SamplePosition;
            bool transient = InDomain(previousPitch) && hasNextValid && span <= transientWindow;
            if (transient)
                continue; // bridged: the run never existed
            if (!hasNextValid && span <= transientWindow)
            {
                // Trailing sub-ms invalid tail: clip the note to its representable end.
                return note with { EndSample = changes[runStart].SamplePosition, Pitch = retained };
            }
            for (int copy = runStart; copy < index; copy++)
                retained.Add(changes[copy]);
        }

        return note with { Pitch = retained };
    }

    /// <summary>Effective pitch of a note at its END (the last change, or initial).</summary>
    private static double BoundaryPitch(NoteEvent note)
    {
        IReadOnlyList<PitchChange> changes = note.Pitch ?? Array.Empty<PitchChange>();
        return changes.Count > 0 ? changes[^1].MidiNote : note.InitialMidiNote;
    }

    /// <summary>
    /// INV5 suppression + INV6/INV7 merge over the note list. Notes are grouped by
    /// voice/instrument/mode identity; within a group, adjacent segments whose
    /// effective pitch joins within <see cref="PitchEqualityEpsilon"/> are merged
    /// when the gap maps to zero MIDI ticks, or when the gap is exactly a bridged
    /// invalid note (≤ ~1 ms). Merges never cross a loop marker.
    /// </summary>
    private static List<NoteEvent> MergeAdjacentSegments(
        List<NoteEvent> notes,
        VisualizationTimeline timeline,
        long transientWindow,
        HashSet<NoteEvent> bridged)
    {
        var result = new List<NoteEvent>(notes.Count);
        foreach (IGrouping<(string ChannelId, string InstrumentId, VisualizationNoteMode Mode), NoteEvent> group
                 in notes.GroupBy(n => (n.ChannelId, n.InstrumentId, n.Mode)))
        {
            NoteEvent? accumulator = null;
            foreach (NoteEvent note in group.OrderBy(n => n.StartSample).ThenBy(n => n.EndSample))
            {
                if (accumulator is null)
                {
                    accumulator = note;
                    continue;
                }
                if (TryMerge(accumulator, note, timeline, transientWindow, bridged, out NoteEvent merged))
                {
                    accumulator = merged;
                    continue;
                }
                result.Add(accumulator);
                accumulator = note;
            }
            if (accumulator is not null)
                result.Add(accumulator);
        }
        return result;
    }

    private static bool TryMerge(
        NoteEvent first,
        NoteEvent second,
        VisualizationTimeline timeline,
        long transientWindow,
        HashSet<NoteEvent> bridged,
        out NoteEvent merged)
    {
        merged = first;
        if (second.StartSample < first.EndSample)
            return false; // overlapping segments are not adjacent
        if (second.IsRetrigger)
            return false; // deliberate same-pitch retrigger is a real boundary (INV3)
        if (Math.Abs(BoundaryPitch(first) - second.InitialMidiNote) >= PitchEqualityEpsilon)
            return false; // different effective pitch at the junction — a real boundary

        long gap = second.StartSample - first.EndSample;
        bool bridgedGap = bridged.Contains(second) || gap <= transientWindow;
        if (!bridgedGap)
            return false;
        if (CrossesLoopMarker(timeline, first.EndSample, second.StartSample))
            return false;

        merged = first with
        {
            EndSample = second.EndSample,
            Pitch = (first.Pitch ?? Array.Empty<PitchChange>())
                .Concat(second.Pitch ?? Array.Empty<PitchChange>())
                .ToArray(),
        };
        return true;
    }

    /// <summary>True when a loop marker sits at or between the two segments: a merge
    /// would make the note span a loop point (INV3: loop points are real boundaries).</summary>
    private static bool CrossesLoopMarker(VisualizationTimeline timeline, long firstEnd, long secondStart)
    {
        foreach (LoopMarker marker in timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
        {
            if (marker.SamplePosition >= firstEnd && marker.SamplePosition <= secondStart)
                return true;
        }
        return false;
    }
}
