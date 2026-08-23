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
    int SamplePlaybackCount,
    int SameTickAttackCollisions,
    int OneTickNotes,
    int UniqueAudibleAttackCount,
    IReadOnlyList<string>? BendRangeDiagnostics = null)
{
    public IReadOnlyList<string> BendRangeDiagnostics { get; init; } =
        BendRangeDiagnostics ?? Array.Empty<string>();
}

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
        ValidatePhysicalVoiceMonophony(notes);
        int uniqueAudibleAttackCount = CountUniqueAudibleAttacks(
            notes,
            rhythm,
            timeline.SamplePlayback ?? Array.Empty<SamplePlaybackEvent>(),
            out IReadOnlyList<SamplePlaybackEvent> samples);
        IndexedNote[] indexed = notes
            .Select((note, index) =>
            {
                string voice = VoiceId(note);
                return new IndexedNote(note, Canonicalize(note, voice), index, voice);
            })
            .ToArray();
        IndexedSample[] indexedSamples = samples
            .Select((sample, index) => new IndexedSample(sample, index))
            .ToArray();
        Dictionary<string, DacNoteAssignment> sampleAssignments = SampleAssignments(samples);

        int collisions = CountSameTickAttacks(timeline, indexed);
        int oneTickNotes = 0;
        var tracks = new List<MidiTrack>();
        var domainsByEndpoint = new Dictionary<MidiEndpoint, MidiVoiceDomain>();

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
            PlannedNote[] notePlans = voiceNotes
                .Select(source => PlanNote(timeline, source, ref oneTickNotes))
                .ToArray();
            int bendRange = BendRange(notePlans);
            Dictionary<string, int> instrumentPrograms = InstrumentPrograms(voiceNotes);
            MidiVoiceDomain domain = CreateVoiceDomain(
                voiceNotes[0].Note.Domain,
                VoiceKind.Aggregate,
                voiceIndex,
                endpoint,
                bendRange) with
            {
                Notes = voiceNotes.Select(source => source.PitchNote).ToArray(),
            };
            RegisterDomain(domainsByEndpoint, domain);
            var track = new MidiTrack
            {
                Name = VoiceName(voiceNotes[0].Note),
                SourceVoiceId = voiceNotes[0].Voice,
                Endpoint = endpoint,
                VoiceDomain = domain,
                ChannelProgram = new MidiChannelProgram(
                    voiceNotes[0].Voice, endpoint.Channel, bendRange),
            };
            var plan = new List<Planned>(1 + notePlans.Sum(n => 3 + n.PitchStates.Count));
            if (bendRange > 0)
            {
                plan.Add(new Planned(0, long.MinValue, int.MinValue, -1, 0,
                    new MidiBendRangeEvent(0, trackIndex, endpoint.Channel, bendRange)));
            }

            string? previousInstrument = null;
            foreach (PlannedNote source in notePlans)
            {
                NoteEvent note = source.Note;
                string instrument = source.PitchNote.InstrumentId;
                if (!string.Equals(previousInstrument, instrument, StringComparison.Ordinal))
                {
                    plan.Add(new Planned(source.OnTick, note.StartSample, source.SourceIndex, 1, -1,
                        new MidiProgramEvent(
                            source.OnTick,
                            trackIndex,
                            endpoint.Channel,
                            instrumentPrograms[instrument])));
                    previousInstrument = instrument;
                }
                if (bendRange > 0)
                {
                    int previousBend = MidiPitchCompiler.EncodeSignedBend(
                        source.PitchStates[0].Pitch - source.BaseNote, bendRange);
                    plan.Add(new Planned(source.OnTick, note.StartSample, source.SourceIndex, 1, 0,
                        new MidiPitchBendEvent(source.OnTick, trackIndex, endpoint.Channel, previousBend)));

                    for (int pitchIndex = 1; pitchIndex < source.PitchStates.Count; pitchIndex++)
                    {
                        PitchState state = source.PitchStates[pitchIndex];
                        int bend = MidiPitchCompiler.EncodeSignedBend(
                            state.Pitch - source.BaseNote, bendRange);
                        if (bend == previousBend)
                            continue;
                        previousBend = bend;
                        plan.Add(new Planned(state.Tick, state.SourceSample, source.SourceIndex, 1, pitchIndex,
                            new MidiPitchBendEvent(state.Tick, trackIndex, endpoint.Channel, bend)));
                    }
                }
                plan.Add(new Planned(source.OnTick, note.StartSample, source.SourceIndex, 2, 0,
                    new MidiNoteEvent(source.OnTick, trackIndex, endpoint.Channel,
                        source.BaseNote, source.PitchNote.Velocity, NoteOn: true)));

                plan.Add(new Planned(source.OffTick, note.EndSample, source.SourceIndex, 0, 0,
                    new MidiNoteEvent(source.OffTick, trackIndex, endpoint.Channel,
                        source.BaseNote, source.PitchNote.Velocity, NoteOn: false)));
            }

            FinishTrack(track, plan);
            tracks.Add(track);
        }

        IGrouping<string, IndexedSample>[] sampleVoices = indexedSamples
            .GroupBy(sample => sample.Event.VoiceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        for (int sampleVoiceIndex = 0; sampleVoiceIndex < sampleVoices.Length; sampleVoiceIndex++)
        {
            IndexedSample[] sampleEvents = sampleVoices[sampleVoiceIndex]
                .OrderBy(sample => sample.SourceIndex)
                .ToArray();
            int endpointIndex = voices.Length + sampleVoiceIndex;
            MidiEndpoint endpoint = MelodicEndpoint(endpointIndex);
            int trackIndex = tracks.Count;
            int bendRange = SampleBendRange(sampleEvents, sampleAssignments);
            MidiVoiceDomain domain = CreateVoiceDomain(
                source: null,
                VoiceKind.PcmVoice,
                endpointIndex,
                endpoint,
                bendRange);
            RegisterDomain(domainsByEndpoint, domain);
            var track = new MidiTrack
            {
                Name = "Sample " + sampleEvents[0].Event.VoiceId,
                SourceVoiceId = sampleEvents[0].Event.VoiceId,
                Endpoint = endpoint,
                VoiceDomain = domain,
                ChannelProgram = new MidiChannelProgram(
                    sampleEvents[0].Event.VoiceId, endpoint.Channel, bendRange),
            };
            var plan = new List<Planned>(sampleEvents.Length * 4);
            if (bendRange > 0)
                plan.Add(new Planned(0, long.MinValue, int.MinValue, -1, 0,
                    new MidiBendRangeEvent(0, trackIndex, endpoint.Channel, bendRange)));

            foreach (IndexedSample source in sampleEvents)
            {
                SamplePlaybackEvent sample = source.Event;
                ValidateSample(timeline, sample);
                DacNoteAssignment identity = sampleAssignments[sample.SampleId];
                long onTick = MidiTransportClock.SampleToTick(
                    timeline.StartSample, sample.StartSample, timeline.SampleRate, _ppq);
                long offTick = MidiTransportClock.SampleToTick(
                    timeline.StartSample, sample.EndSample, timeline.SampleRate, _ppq);
                if (offTick <= onTick)
                {
                    offTick = checked(onTick + 1);
                    oneTickNotes++;
                }

                plan.Add(new Planned(offTick, sample.EndSample, source.SourceIndex, 0, 0,
                    new MidiNoteEvent(offTick, trackIndex, endpoint.Channel,
                        identity.Note, DefaultVelocity, NoteOn: false)));
                plan.Add(new Planned(onTick, sample.StartSample, source.SourceIndex, 1, 0,
                    new MidiBankEvent(onTick, trackIndex, endpoint.Channel, identity.Bank)));
                if (sample.MidiPitch is double pitch && bendRange > 0)
                {
                    plan.Add(new Planned(onTick, sample.StartSample, source.SourceIndex, 1, 1,
                        new MidiPitchBendEvent(onTick, trackIndex, endpoint.Channel,
                            MidiPitchCompiler.EncodeSignedBend(pitch - identity.Note, bendRange))));
                }
                plan.Add(new Planned(onTick, sample.StartSample, source.SourceIndex, 2, 0,
                    new MidiNoteEvent(onTick, trackIndex, endpoint.Channel,
                        identity.Note, DefaultVelocity, NoteOn: true)));
            }

            FinishTrack(track, plan);
            tracks.Add(track);
        }

        if (rhythm.Count > 0)
        {
            MidiTrack rhythmTrack = BuildRhythmTrack(timeline, rhythm, tracks.Count);
            MidiVoiceDomain domain = CreateVoiceDomain(
                rhythm[0].Domain,
                VoiceKind.Rhythm,
                0,
                rhythmTrack.Endpoint,
                bendRange: 0);
            RegisterDomain(domainsByEndpoint, domain);
            rhythmTrack.VoiceDomain = domain;
            tracks.Add(rhythmTrack);
        }

        int serializedNoteOnCount = tracks
            .SelectMany(track => track.Events)
            .OfType<MidiNoteEvent>()
            .Count(note => note.NoteOn);
        if (serializedNoteOnCount != uniqueAudibleAttackCount)
        {
            throw new InvalidOperationException(
                $"MIDI attack conservation failed: source attacks={uniqueAudibleAttackCount}, " +
                $"serialized NoteOn events={serializedNoteOnCount}.");
        }

        IReadOnlyList<MidiEventBase> conductor = MidiConductor.FixedTransport();
        byte[] bytes = new MidiFileWriter(_ppq).Write(conductor, tracks);
        return new MidiTranscriptionResult
        {
            Bytes = bytes,
            Tracks = tracks,
            Diagnostics = new MidiTranscriptionDiagnostics(
                notes.Count, rhythm.Count, samples.Count, collisions, oneTickNotes,
                uniqueAudibleAttackCount,
                domainsByEndpoint.Values
                    .Where(domain => MidiPitchCompiler.ClassifyBendRange(domain.BendRange)
                        is not MidiBendRangeClassification.Zero
                        and not MidiBendRangeClassification.Ordinary)
                    .OrderBy(domain => domain.Source.ToString(), StringComparer.Ordinal)
                    .Select(domain =>
                        $"{domain.Source}: bend-range={domain.BendRange}; "
                        + $"classification={MidiPitchCompiler.ClassifyBendRange(domain.BendRange)}")
                    .ToArray()),
        };
    }


    private static void ValidatePhysicalVoiceMonophony(IReadOnlyList<NoteEvent> notes)
    {
        foreach (IGrouping<string, NoteEvent> voice in notes
            .GroupBy(PhysicalVoiceKey, StringComparer.Ordinal))
        {
            NoteEvent[] ordered = voice
                .OrderBy(note => note.StartSample)
                .ThenBy(note => note.EndSample)
                .ToArray();
            for (int index = 1; index < ordered.Length; index++)
            {
                NoteEvent previous = ordered[index - 1];
                NoteEvent next = ordered[index];
                if (next.StartSample < previous.EndSample)
                {
                    throw new InvalidOperationException(
                        $"Physical voice '{PhysicalVoiceKey(next)}' has overlapping notes: " +
                        $"{previous.StartSample}-{previous.EndSample} and " +
                        $"{next.StartSample}-{next.EndSample}.");
                }
            }
        }
    }

    private static int CountUniqueAudibleAttacks(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<RhythmEvent> rhythm,
        IReadOnlyList<SamplePlaybackEvent> allSamples,
        out IReadOnlyList<SamplePlaybackEvent> samples)
    {
        ValidateDuplicateAttackIds("Note", notes.Select(note => note.SourceAttackId));
        ValidateDuplicateAttackIds("Rhythm", rhythm.Select(hit => hit.SourceAttackId));
        ValidateDuplicateAttackIds("SamplePlayback", allSamples.Select(sample => sample.SourceAttackId));

        HashSet<string> noteAttackIds = notes
            .Select(note => note.SourceAttackId)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        samples = allSamples
            .Where(sample => sample.SourceAttackId is null
                || !noteAttackIds.Contains(sample.SourceAttackId))
            .ToArray();

        var attacks = new HashSet<AttackKey>();
        for (int index = 0; index < notes.Count; index++)
            attacks.Add(AttackKey.For("Note", notes[index].SourceAttackId, index));
        for (int index = 0; index < rhythm.Count; index++)
            attacks.Add(AttackKey.For("Rhythm", rhythm[index].SourceAttackId, index));
        for (int index = 0; index < samples.Count; index++)
            attacks.Add(AttackKey.For("SamplePlayback", samples[index].SourceAttackId, index));
        return attacks.Count;
    }

    private static void ValidateDuplicateAttackIds(
        string family,
        IEnumerable<string?> attackIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? attackId in attackIds)
        {
            if (attackId is not null && !seen.Add(attackId))
            {
                throw new InvalidOperationException(
                    $"Duplicate {family} SourceAttackId '{attackId}'.");
            }
        }
    }

    private static string PhysicalVoiceKey(NoteEvent note) =>
        note.Domain is SourceDomainKey domain
            ? "domain:" + domain
            : "channel:" + note.ChannelId;

    private readonly record struct AttackKey(string Family, string? AttackId, int LocalIndex)
    {
        public static AttackKey For(string family, string? attackId, int localIndex) =>
            new(family, attackId, attackId is null ? localIndex : -1);
    }

    private static Dictionary<string, DacNoteAssignment> SampleAssignments(
        IReadOnlyList<SamplePlaybackEvent> samples)
    {
        var assignments = new Dictionary<string, DacNoteAssignment>(StringComparer.Ordinal);
        DacNoteMapper mapper = new();
        foreach (string sampleId in samples
            .Select(sample => sample.SampleId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            int ordinal = assignments.Count;
            if (ordinal >= 128 * 128)
                throw new InvalidOperationException(
                    "MIDI sample identity table exceeds the representable bank/note range.");
            assignments.Add(sampleId, mapper.Map(ordinal));
        }
        return assignments;
    }

    private static Dictionary<string, int> InstrumentPrograms(
        IReadOnlyList<IndexedNote> notes)
    {
        string[] instruments = notes
            .Select(note => note.PitchNote.InstrumentId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (instruments.Length > 128)
            throw new InvalidOperationException(
                $"Source domain contains {instruments.Length} instruments; MIDI supports 128 programs.");
        return instruments
            .Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index, StringComparer.Ordinal);
    }

    private static int SampleBendRange(
        IReadOnlyList<IndexedSample> samples,
        IReadOnlyDictionary<string, DacNoteAssignment> assignments)
    {
        var pitches = new List<(IReadOnlyList<SourcePitchPoint> Curve, int BaseNote)>();
        foreach (IndexedSample indexed in samples)
        {
            ValidateSamplePitch(indexed.Event);
            if (indexed.Event.MidiPitch is not double pitch)
                continue;
            int baseNote = assignments[indexed.Event.SampleId].Note;
            pitches.Add((new[] { new SourcePitchPoint(indexed.Event.StartSample, pitch) }, baseNote));
        }
        return MidiPitchCompiler.RequiredBendRange(pitches);
    }

    private static void ValidateSample(VisualizationTimeline timeline, SamplePlaybackEvent sample)
    {
        if (sample.StartSample < timeline.StartSample || sample.EndSample < sample.StartSample)
            throw new InvalidOperationException("Sample playback range is outside the timeline.");
        if (string.IsNullOrWhiteSpace(sample.SampleId))
            throw new InvalidOperationException("Sample playback has no stable sample identity.");
        ValidateSamplePitch(sample);
    }

    private static void ValidateSamplePitch(SamplePlaybackEvent sample)
    {
        if (sample.MidiPitch is double pitch && !double.IsFinite(pitch))
            throw new InvalidOperationException(
                $"Sample playback pitch for '{sample.SampleId}' is not finite.");
    }

    private MidiTrack BuildRhythmTrack(
        VisualizationTimeline timeline,
        IReadOnlyList<RhythmEvent> rhythm,
        int trackIndex)
    {
        var track = new MidiTrack
        {
            Name = "Native Rhythm",
            SourceVoiceId = "rhythm",
            Endpoint = new MidiEndpoint(0, PercussionChannel),
            ChannelProgram = new MidiChannelProgram(
                "rhythm", PercussionChannel, bendRange: 0),
        };
        var plan = new List<Planned>(rhythm.Count * 2);
        for (int index = 0; index < rhythm.Count; index++)
        {
            RhythmEvent hit = rhythm[index];
            long tick = MidiTransportClock.SampleToTick(
                timeline.StartSample, hit.SamplePosition, timeline.SampleRate, _ppq);
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
            if (!seen.Add((note.Voice, MidiTransportClock.SampleToTick(
                    timeline.StartSample, note.Note.StartSample, timeline.SampleRate, _ppq))))
                collisions++;
        return collisions;
    }

    private static int BendRange(IReadOnlyList<PlannedNote> notes)
        => MidiPitchCompiler.RequiredBendRange(
            notes.Select(note => (note.PitchNote.PitchCurve, note.BaseNote)));

    private PlannedNote PlanNote(
        VisualizationTimeline timeline,
        IndexedNote source,
        ref int oneTickNotes)
    {
        NoteEvent note = source.Note;
        SourcePitchNote pitchNote = source.PitchNote;
        ValidateNote(timeline, pitchNote);
        long onTick = MidiTransportClock.SampleToTick(
            timeline.StartSample, pitchNote.StartSample, timeline.SampleRate, _ppq);
        long offTick = MidiTransportClock.SampleToTick(
            timeline.StartSample, pitchNote.EndSample, timeline.SampleRate, _ppq);
        if (offTick <= onTick)
        {
            offTick = checked(onTick + 1);
            oneTickNotes++;
        }

        var statesByTick = new SortedDictionary<long, PitchState>
        {
            [onTick] = new(onTick, pitchNote.StartSample, pitchNote.InitialMidiNote),
        };
        foreach (SourcePitchPoint point in pitchNote.PitchCurve.Skip(1))
        {
            long tick = MidiTransportClock.SampleToTick(
                timeline.StartSample, point.Sample, timeline.SampleRate, _ppq);
            // Several source transitions can quantize to one MIDI tick. The last
            // source state is the only state a synth can observe at that tick. If
            // it lands on NoteOn, fold it into the single attack bend below.
            statesByTick[tick] = new(
                tick,
                tick == onTick ? pitchNote.StartSample : point.Sample,
                point.MidiNote);
        }

        PitchState[] states = statesByTick.Values.ToArray();
        int baseNote = MidiPitchCompiler.SelectMinimaxBaseNote(pitchNote.PitchCurve);
        return new PlannedNote(source, onTick, offTick, baseNote, states);
    }

    private static MidiVoiceDomain CreateVoiceDomain(
        SourceDomainKey? source,
        VoiceKind fallbackKind,
        int fallbackIndex,
        MidiEndpoint endpoint,
        int bendRange)
    {
        SourceDomainKey identity = source ?? new SourceDomainKey(
            new DeviceId(ChipType.Unknown, 0), fallbackKind, fallbackIndex);
        return new MidiVoiceDomain(
            identity,
            endpoint.Channel,
            bendRangeSemitones: bendRange,
            port: endpoint.Port);
    }

    private static void RegisterDomain(
        IDictionary<MidiEndpoint, MidiVoiceDomain> domainsByEndpoint,
        MidiVoiceDomain domain)
    {
        MidiEndpoint endpoint = new(domain.Port, domain.Channel);
        if (domainsByEndpoint.TryGetValue(endpoint, out MidiVoiceDomain existing))
        {
            existing.EnsureCompatible(domain);
            return;
        }
        domainsByEndpoint.Add(endpoint, domain);
    }

    private static SourcePitchNote Canonicalize(NoteEvent note, string sourceVoiceId)
    {
        if (note.EndSample < note.StartSample)
            throw new InvalidOperationException("Source note ends before it starts.");
        if (!double.IsFinite(note.InitialMidiNote))
            throw new InvalidOperationException("Source note pitch is not finite.");

        var curve = new List<SourcePitchPoint>
        {
            new(note.StartSample, note.InitialMidiNote),
        };
        IReadOnlyList<PitchChange> changes = note.Pitch ?? Array.Empty<PitchChange>();
        for (int index = 0; index < changes.Count; index++)
        {
            PitchChange change = changes[index];
            if (index > 0 && change.SamplePosition < changes[index - 1].SamplePosition)
                throw new InvalidOperationException(
                    $"Pitch changes on '{VoiceName(note)}' are not in source order.");
            if (!double.IsFinite(change.MidiNote))
                throw new InvalidOperationException(
                    $"Non-finite pitch on '{VoiceName(note)}' at sample {change.SamplePosition}.");
            if (change.SamplePosition < note.StartSample || change.SamplePosition >= note.EndSample)
                continue;

            var point = new SourcePitchPoint(change.SamplePosition, change.MidiNote);
            if (change.SamplePosition == note.StartSample)
                curve[0] = point;
            else if (curve[^1].Sample == change.SamplePosition)
                curve[^1] = point;
            else
                curve.Add(point);
        }

        return new SourcePitchNote(
            note.StartSample,
            note.EndSample,
            curve,
            note.InstrumentId ?? string.Empty,
            DefaultVelocity,
            sourceVoiceId);
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

    private static void ValidateNote(VisualizationTimeline timeline, SourcePitchNote note)
    {
        if (note.StartSample < timeline.StartSample)
            throw new InvalidOperationException("Source note starts before the timeline.");
        if (note.EndSample < note.StartSample)
            throw new InvalidOperationException("Source note ends before it starts.");
        if (note.PitchCurve.Count == 0)
            throw new InvalidOperationException("Source note has no canonical pitch curve.");
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
        if (track.ChannelProgram is MidiChannelProgram program)
        {
            program.MutableEvents.Clear();
            foreach (Planned item in plan)
                program.Append(item.Event);
            program.Seal();
        }
        else
        {
            for (int index = 0; index < plan.Count; index++)
                plan[index].Event.SourceOrder = index;
            plan.Sort(static (left, right) => MidiEventOrder.Compare(left.Event, right.Event));
            foreach (Planned item in plan)
                track.Events.Add(item.Event);
        }
        track.HasCanonicalEventOrder = true;
    }

    private readonly record struct PitchState(long Tick, long SourceSample, double Pitch);
    private sealed record PlannedNote(
        IndexedNote Source,
        long OnTick,
        long OffTick,
        int BaseNote,
        IReadOnlyList<PitchState> PitchStates)
    {
        public NoteEvent Note => Source.Note;
        public SourcePitchNote PitchNote => Source.PitchNote;
        public int SourceIndex => Source.SourceIndex;
    }

    private sealed record IndexedNote(
        NoteEvent Note,
        SourcePitchNote PitchNote,
        int SourceIndex,
        string Voice);
    private sealed record IndexedSample(SamplePlaybackEvent Event, int SourceIndex);
    private sealed record Planned(
        long Tick, long SourceSample, int SourceIndex, int Phase, int LocalOrder, MidiEventBase Event);
}
