#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

internal sealed class MidiTranscriptionResult
{
    public required byte[] Bytes { get; init; }
    public required IReadOnlyList<MidiTrack> Tracks { get; init; }
    public required MidiTranscriptionDiagnostics Diagnostics { get; init; }
}

internal sealed record MidiTranscriptionDiagnostics(
    int SourceNoteCount,
    int NativeRhythmHitCount,
    int SameTickAttackCollisions,
    int OneTickNotes);

/// <summary>
/// Raw source-timeline -> SMF transcription. No BPM/grid inference, tuning
/// normalization, instrument splitting, quantization, FM-drum guessing or
/// structural analysis is allowed here.
/// </summary>
internal sealed class MidiTranscriber
{
    internal const int TransportMicrosecondsPerQuarter = 500_000; // 120 BPM
    internal const int DefaultPpq = 960;
    internal const int DefaultVelocity = 96;

    private const int MinimumBendRange = 2;
    private const int PercussionChannel = 9;
    private const int UnknownNativeDrumNote = 60;

    private readonly int _ppq;

    public MidiTranscriber(int ppq = DefaultPpq)
    {
        if (ppq <= 0 || ppq > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(ppq));
        _ppq = ppq;
    }

    public MidiTranscriptionResult Transcribe(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.SampleRate <= 0)
            throw new InvalidOperationException("Timeline sample rate must be positive.");
        if (timeline.EndSample < timeline.StartSample)
            throw new InvalidOperationException("Timeline end precedes timeline start.");

        IReadOnlyList<NoteEvent> notes = timeline.Notes ?? Array.Empty<NoteEvent>();
        IReadOnlyList<RhythmEvent> rhythm = timeline.Rhythm ?? Array.Empty<RhythmEvent>();
        IndexedNote[] indexed = notes
            .Select((note, index) => new IndexedNote(note, index, VoiceId(note)))
            .ToArray();

        int collisions = CountSameTickAttacks(timeline, indexed);
        int oneTickNotes = 0;
        var tracks = new List<MidiTrack>();

        IGrouping<string, IndexedNote>[] voices = indexed
            .GroupBy(n => n.Voice, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();

        for (int voiceIndex = 0; voiceIndex < voices.Length; voiceIndex++)
        {
            IndexedNote[] voiceNotes = voices[voiceIndex]
                .OrderBy(n => n.SourceIndex)
                .ToArray();
            MidiEndpoint endpoint = MelodicEndpoint(voiceIndex);
            int trackIndex = tracks.Count;
            int bendRange = BendRange(voiceNotes);
            var track = new MidiTrack
            {
                Name = VoiceName(voiceNotes[0].Note),
                Endpoint = endpoint,
            };
            var plan = new List<Planned>(1 + voiceNotes.Sum(n => 3 + (n.Note.Pitch?.Count ?? 0)))
            {
                new(0, long.MinValue, int.MinValue, -1, 0,
                    new MidiBendRangeEvent(0, trackIndex, endpoint.Channel, bendRange)),
            };

            foreach (IndexedNote source in voiceNotes)
            {
                NoteEvent note = source.Note;
                ValidateNote(timeline, note);
                double initialPitch = InitialPitch(note);
                int baseNote = BaseNote(initialPitch);
                long onTick = ToTick(timeline, note.StartSample);
                long offTick = ToTick(timeline, note.EndSample);
                if (offTick <= onTick)
                {
                    offTick = checked(onTick + 1);
                    oneTickNotes++;
                }

                plan.Add(new Planned(onTick, note.StartSample, source.SourceIndex, 1, 0,
                    new MidiPitchBendEvent(onTick, trackIndex, endpoint.Channel,
                        EncodeBend(initialPitch - baseNote, bendRange))));
                plan.Add(new Planned(onTick, note.StartSample, source.SourceIndex, 2, 0,
                    new MidiNoteEvent(onTick, trackIndex, endpoint.Channel,
                        baseNote, DefaultVelocity, NoteOn: true)));

                int pitchOrder = 0;
                foreach (PitchChange change in PitchStates(note))
                {
                    if (change.SamplePosition == note.StartSample)
                        continue; // folded into the attack; last write wins.
                    if (change.SamplePosition <= note.StartSample || change.SamplePosition >= note.EndSample)
                        continue;
                    if (!double.IsFinite(change.MidiNote))
                        throw new InvalidOperationException(
                            $"Non-finite pitch on '{VoiceName(note)}' at sample {change.SamplePosition}.");

                    long tick = ToTick(timeline, change.SamplePosition);
                    plan.Add(new Planned(tick, change.SamplePosition, source.SourceIndex, 1, ++pitchOrder,
                        new MidiPitchBendEvent(tick, trackIndex, endpoint.Channel,
                            EncodeBend(change.MidiNote - baseNote, bendRange))));
                }

                plan.Add(new Planned(offTick, note.EndSample, source.SourceIndex, 0, 0,
                    new MidiNoteEvent(offTick, trackIndex, endpoint.Channel,
                        baseNote, DefaultVelocity, NoteOn: false)));
            }

            FinishTrack(track, plan);
            tracks.Add(track);
        }

        if (rhythm.Count > 0)
            tracks.Add(BuildRhythmTrack(timeline, rhythm, tracks.Count));

        var tempo = new MidiTempoEvent(0, TransportMicrosecondsPerQuarter) { SourceOrder = 0 };
        byte[] bytes = new MidiFileWriter(_ppq).Write(new MidiEventBase[] { tempo }, tracks);
        return new MidiTranscriptionResult
        {
            Bytes = bytes,
            Tracks = tracks,
            Diagnostics = new MidiTranscriptionDiagnostics(notes.Count, rhythm.Count, collisions, oneTickNotes),
        };
    }

    private MidiTrack BuildRhythmTrack(
        VisualizationTimeline timeline,
        IReadOnlyList<RhythmEvent> rhythm,
        int trackIndex)
    {
        var track = new MidiTrack
        {
            Name = "Native Rhythm",
            Endpoint = new MidiEndpoint(0, PercussionChannel),
        };
        var plan = new List<Planned>(rhythm.Count * 2);
        for (int index = 0; index < rhythm.Count; index++)
        {
            RhythmEvent hit = rhythm[index];
            long tick = ToTick(timeline, hit.SamplePosition);
            int note = GeneralMidiDrumMapper.TryMap(hit, out int mapped)
                ? mapped
                : UnknownNativeDrumNote; // preserve the attack; do not invent a role.
            int velocity = DrumVelocity(hit.Strength);
            plan.Add(new Planned(tick, hit.SamplePosition, index, 0, 0,
                new MidiNoteEvent(tick, trackIndex, PercussionChannel, note, velocity, NoteOn: true)));
            plan.Add(new Planned(checked(tick + 1), hit.SamplePosition, index, 1, 0,
                new MidiNoteEvent(checked(tick + 1), trackIndex, PercussionChannel, note, 0, NoteOn: false)));
        }
        FinishTrack(track, plan);
        return track;
    }

    private int CountSameTickAttacks(VisualizationTimeline timeline, IReadOnlyList<IndexedNote> notes)
    {
        var seen = new HashSet<(string Voice, long Tick)>();
        int collisions = 0;
        foreach (IndexedNote note in notes)
            if (!seen.Add((note.Voice, ToTick(timeline, note.Note.StartSample))))
                collisions++;
        return collisions;
    }

    private static int BendRange(IReadOnlyList<IndexedNote> notes)
    {
        double maximum = 0;
        foreach (IndexedNote source in notes)
        {
            NoteEvent note = source.Note;
            double initial = InitialPitch(note);
            int baseNote = BaseNote(initial);
            maximum = Math.Max(maximum, Math.Abs(initial - baseNote));
            foreach (PitchChange change in PitchStates(note))
            {
                if (change.SamplePosition <= note.StartSample || change.SamplePosition >= note.EndSample)
                    continue;
                if (!double.IsFinite(change.MidiNote))
                    throw new InvalidOperationException("Source pitch change is not finite.");
                maximum = Math.Max(maximum, Math.Abs(change.MidiNote - baseNote));
            }
        }
        if (maximum > 127.0 + 1e-9)
            throw new InvalidOperationException(
                $"Required pitch-bend excursion {maximum:0.###} exceeds MIDI's 127-semitone RPN limit.");
        return Math.Clamp((int)Math.Ceiling(maximum), MinimumBendRange, 127);
    }

    private static double InitialPitch(NoteEvent note)
    {
        double pitch = note.InitialMidiNote;
        foreach (PitchChange change in PitchStates(note))
            if (change.SamplePosition == note.StartSample)
                pitch = change.MidiNote;
        if (!double.IsFinite(pitch))
            throw new InvalidOperationException("Source pitch is not finite.");
        return pitch;
    }

    /// <summary>Pitch writes are state. Timeline decoders append them in source
    /// order, so multiple writes at one sample collapse to the final state. A
    /// decreasing sample position is a decoder/timeline bug and fails loudly.
    /// </summary>
    private static IEnumerable<PitchChange> PitchStates(NoteEvent note)
    {
        IReadOnlyList<PitchChange> changes = note.Pitch ?? Array.Empty<PitchChange>();
        for (int index = 0; index < changes.Count; index++)
        {
            PitchChange change = changes[index];
            if (index > 0 && change.SamplePosition < changes[index - 1].SamplePosition)
                throw new InvalidOperationException(
                    $"Pitch changes on '{VoiceName(note)}' are not in source order.");

            // A later write at the same source sample supersedes this state.
            if (index + 1 < changes.Count
                && changes[index + 1].SamplePosition == change.SamplePosition)
                continue;

            yield return change;
        }
    }

    private static int BaseNote(double pitch) =>
        (int)Math.Clamp(
            checked((long)Math.Round(pitch, MidpointRounding.AwayFromZero)),
            0L,
            127L);

    private static int EncodeBend(double offset, int range)
    {
        if (!double.IsFinite(offset) || Math.Abs(offset) > range + 1e-9)
            throw new InvalidOperationException($"Pitch offset {offset:0.###} is outside +/-{range} semitones.");
        double normalized = offset / range;
        double scaled = normalized < 0 ? normalized * 8192.0 : normalized * 8191.0;
        return Math.Clamp((int)Math.Round(scaled, MidpointRounding.AwayFromZero), -8192, 8191);
    }

    private long ToTick(VisualizationTimeline timeline, long sample)
    {
        long delta = checked(sample - timeline.StartSample);
        if (delta < 0)
            throw new InvalidOperationException($"Source sample {sample} precedes timeline start {timeline.StartSample}.");
        decimal ticks = (decimal)delta * (2m * _ppq) / timeline.SampleRate; // 120 BPM = 2 quarters/sec.
        return decimal.ToInt64(decimal.Round(ticks, 0, MidpointRounding.AwayFromZero));
    }

    private static MidiEndpoint MelodicEndpoint(int voiceIndex)
    {
        int port = voiceIndex / 15;
        if (port > 127)
            throw new InvalidOperationException("MIDI transcription requires more than 128 ports.");
        int slot = voiceIndex % 15;
        int channel = slot < PercussionChannel ? slot : slot + 1;
        return new MidiEndpoint((byte)port, channel);
    }

    private static string VoiceId(NoteEvent note)
    {
        if (note.Domain is SourceDomainKey domain)
            return "domain:" + domain;
        if (!string.IsNullOrWhiteSpace(note.ChannelId))
            return "channel:" + note.ChannelId;
        throw new InvalidOperationException("Source note has no physical voice identity.");
    }

    private static string VoiceName(NoteEvent note) =>
        note.Domain is SourceDomainKey domain ? domain.ToString() : note.ChannelId;

    private static void ValidateNote(VisualizationTimeline timeline, NoteEvent note)
    {
        if (note.StartSample < timeline.StartSample)
            throw new InvalidOperationException("Source note starts before the timeline.");
        if (note.EndSample < note.StartSample)
            throw new InvalidOperationException("Source note ends before it starts.");
        if (!double.IsFinite(note.InitialMidiNote))
            throw new InvalidOperationException("Source note pitch is not finite.");
    }

    private static int DrumVelocity(float strength)
    {
        double normalized = Math.Clamp(strength, 0f, 1f);
        return normalized <= 0
            ? 1
            : Math.Clamp((int)Math.Ceiling(24.0 + 103.0 * Math.Pow(normalized, 0.45)), 1, 127);
    }

    private static void FinishTrack(MidiTrack track, List<Planned> plan)
    {
        plan.Sort(static (a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            if (c != 0) return c;
            c = a.SourceSample.CompareTo(b.SourceSample);
            if (c != 0) return c;
            // At an exact retrigger boundary every NoteOff must happen before the
            // next attack state. After that boundary barrier, keep each source
            // attack's bend -> NoteOn pair together instead of globally grouping
            // all bends ahead of all attacks.
            c = BoundaryRank(a.Phase).CompareTo(BoundaryRank(b.Phase));
            if (c != 0) return c;
            c = a.SourceIndex.CompareTo(b.SourceIndex);
            if (c != 0) return c;
            c = a.Phase.CompareTo(b.Phase);
            return c != 0 ? c : a.LocalOrder.CompareTo(b.LocalOrder);
        });
        for (int i = 0; i < plan.Count; i++)
        {
            plan[i].Event.SourceOrder = i;
            track.Events.Add(plan[i].Event);
        }
        // Equal-tick source order is part of fidelity. Do not let the writer regroup
        // every bend ahead of every NoteOn at the same tick.
        track.HasCanonicalEventOrder = true;
    }

    private static int BoundaryRank(int phase) => phase == 0 ? 0 : 1;

    private sealed record IndexedNote(NoteEvent Note, int SourceIndex, string Voice);
    private sealed record Planned(
        long Tick, long SourceSample, int SourceIndex, int Phase, int LocalOrder, MidiEventBase Event);
}