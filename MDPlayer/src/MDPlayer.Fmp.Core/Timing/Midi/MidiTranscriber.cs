#nullable enable

using System.Globalization;
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
    IReadOnlyList<string>? BendRangeDiagnostics = null,
    int EffectivePpq = 0,
    int QuantizationLossyEventCount = 0,
    double MaxQuantizationLossTicks = 0,
    int SampleIdentityCount = 0,
    IReadOnlyList<string>? SampleIdentityMappings = null)
{
    public IReadOnlyList<string> BendRangeDiagnostics { get; init; } =
        BendRangeDiagnostics ?? Array.Empty<string>();

    /// <summary>
    /// One line per deduplicated DAC sample identity:
    /// <c>sampleId -&gt; port P ch N note NN (name, ordinal O, K playbacks)</c>.
    /// The ordinal is the dedup ordinal in this export; the note is the stable
    /// sample-trigger note assigned to it (channel 10, never a pitch estimate).
    /// </summary>
    public IReadOnlyList<string> SampleIdentityMappings { get; init; } =
        SampleIdentityMappings ?? Array.Empty<string>();
}

/// <summary>
/// Fixed-transport source-timeline -> SMF transcription: every event maps to
/// the fixed clock tick = round((sample - StartSample) * 2 * PPQ / SampleRate)
/// at a single 120 BPM transport. No BPM/grid inference, PPQ search, tuning
/// normalization, instrument splitting or structural analysis is allowed here.
/// </summary>
internal sealed class MidiTranscriber
{
    internal const int TransportMicrosecondsPerQuarter = 500_000; // 120 BPM
    internal const int DefaultPpq = 960;
    internal const int DefaultVelocity = 96;

    private const int PercussionChannel = 9;
    private const int UnknownNativeDrumNote = 60;

    /// <summary>DAC sample-trigger channel: MIDI channel 10 (zero-based 9).</summary>
    private const int DacChannel = 9;

    /// <summary>
    /// Reserved identity range base: sample ordinal 0 maps to note 36 (36..95
    /// holds the first 60 unique DAC samples). The note is a stable trigger
    /// label, never a pitch estimate.
    /// </summary>
    private const int DacBaseNote = 36;

    /// <summary>Each (port, channel 10) lane can label 128 unique samples.</summary>
    private const int DacNotesPerLane = 128;

    private readonly int _ppq;
    private int _quantizationLossyEventCount;
    private decimal _maxQuantizationLossTicks;

    public MidiTranscriber(int ppq = DefaultPpq)
    {
        if (ppq <= 0 || ppq > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(ppq));
        _ppq = ppq;
    }

    /// <summary>The PPQ used for the emitted SMF (fixed: the requested PPQ).</summary>
    public int EffectivePpq => _ppq;

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
            ExpandDacHits(timeline, timeline.SamplePlayback ?? Array.Empty<SamplePlaybackEvent>()),
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

        _quantizationLossyEventCount = 0;
        _maxQuantizationLossTicks = 0m;

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
            PlannedNote[] notePlans = voiceNotes
                .Select(source => PlanNote(timeline, source, ref oneTickNotes))
                .ToArray();
            int bendRange = BendRange(notePlans);
            Dictionary<string, int> instrumentPrograms = InstrumentPrograms(voiceNotes);

            var track = new MidiTrack(notePlans.Sum(n => 3 + n.PitchStates.Count))
            {
                Name = VoiceName(voiceNotes[0].Note),
                SourceVoiceId = voiceNotes[0].Voice,
                Endpoint = endpoint,
                SourceNotes = notePlans
                    .Select(n => n.PitchNote)
                    .ToArray(),
            };
            var plan = new List<Planned>(1 + notePlans.Sum(n => 3 + n.PitchStates.Count));
            if (bendRange > 0)
            {
                plan.Add(new Planned(0, long.MinValue, int.MinValue, -1, 0,
                    PackedMidiEvent.BendRange(0, trackIndex, endpoint.Channel, bendRange)));
            }

            string? previousInstrument = null;
            foreach (PlannedNote source in notePlans)
            {
                NoteEvent note = source.Note;
                string instrument = source.PitchNote.InstrumentId;
                if (!string.Equals(previousInstrument, instrument, StringComparison.Ordinal))
                {
                    plan.Add(new Planned(source.OnTick, note.StartSample, source.SourceIndex, 1, -1,
                        PackedMidiEvent.Program(
                            source.OnTick,
                            trackIndex,
                            endpoint.Channel,
                            instrumentPrograms[instrument])));
                    previousInstrument = instrument;
                }
                if (bendRange > 0)
                {
                    // The attack bend is always emitted (even for a zero delta):
                    // a residual bend from a previous note would otherwise
                    // corrupt the attack pitch.
                    int previousBend = MidiPitchCompiler.EncodeSignedBend(
                        source.PitchStates[0].Pitch - source.BaseNote, bendRange);
                    plan.Add(new Planned(source.OnTick, note.StartSample, source.SourceIndex, 1, 0,
                        PackedMidiEvent.PitchBend(
                            source.OnTick, trackIndex, endpoint.Channel, previousBend)));

                    for (int pitchIndex = 1; pitchIndex < source.PitchStates.Count; pitchIndex++)
                    {
                        PitchState state = source.PitchStates[pitchIndex];
                        int bend = MidiPitchCompiler.EncodeSignedBend(
                            state.Pitch - source.BaseNote, bendRange);
                        if (bend == previousBend)
                            continue;
                        previousBend = bend;
                        plan.Add(new Planned(state.Tick, state.SourceSample, source.SourceIndex, 1, pitchIndex,
                            PackedMidiEvent.PitchBend(
                                state.Tick, trackIndex, endpoint.Channel, bend)));
                    }
                }
                plan.Add(new Planned(source.OnTick, note.StartSample, source.SourceIndex, 2, 0,
                    PackedMidiEvent.Note(
                        source.OnTick, trackIndex, endpoint.Channel,
                        source.BaseNote, source.PitchNote.Velocity, noteOn: true)));

                plan.Add(new Planned(source.OffTick, note.EndSample, source.SourceIndex, 0, 0,
                    PackedMidiEvent.Note(
                        source.OffTick, trackIndex, endpoint.Channel,
                        source.BaseNote, source.PitchNote.Velocity, noteOn: false)));
            }

            FinishTrack(track, plan);
            tracks.Add(track);
        }

        // DAC sample-trigger compiler (separate from melodic FM CH1-6 pitch
        // conversion): every playback emits exactly one NoteOn with its stable
        // identity note on channel 10 plus a matching NoteOff. No pitch bend,
        // no melodic pitch derivation, no bank select.
        Dictionary<string, DacTriggerAssignment> sampleAssignments = samples.Count == 0
            ? new Dictionary<string, DacTriggerAssignment>(StringComparer.Ordinal)
            : SampleAssignments(samples, rhythm.Count > 0);
        if (indexedSamples.Length > 0)
        {
            var laneSamples = new Dictionary<byte, List<IndexedSample>>();
            foreach (IndexedSample source in indexedSamples)
            {
                DacTriggerAssignment assignment = sampleAssignments[source.Event.SampleId];
                if (!laneSamples.TryGetValue(assignment.Port, out List<IndexedSample>? lane))
                {
                    lane = new List<IndexedSample>();
                    laneSamples.Add(assignment.Port, lane);
                }
                lane.Add(source);
            }

            foreach (byte port in laneSamples.Keys.OrderBy(value => value))
            {
                List<IndexedSample> lane = laneSamples[port]
                    .OrderBy(sample => sample.SourceIndex)
                    .ToList();
                string[] laneVoices = lane
                    .Select(sample => sample.Event.VoiceId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                int trackIndex = tracks.Count;
                var track = new MidiTrack(lane.Count * 2)
                {
                    Name = laneVoices.Length == 1 ? "Sample " + laneVoices[0] : "Sample Triggers",
                    SourceVoiceId = string.Join("+", laneVoices),
                    Endpoint = new MidiEndpoint(port, DacChannel),
                };
                var plan = new List<Planned>(lane.Count * 2);

                foreach (IndexedSample source in lane)
                {
                    SamplePlaybackEvent sample = source.Event;
                    ValidateSample(timeline, sample);
                    DacTriggerAssignment identity = sampleAssignments[sample.SampleId];
                    long onTick = SampleToTick(timeline, sample.StartSample);
                    long offTick = SampleToTick(timeline, sample.EndSample);
                    if (offTick <= onTick)
                    {
                        offTick = checked(onTick + 1);
                        oneTickNotes++;
                    }
                    int velocity = SampleVelocity(sample);

                    // NoteOff is planned before NoteOn so a same-tick retrigger
                    // serializes as off-then-on (MidiEventOrder rank 0 vs 4).
                    plan.Add(new Planned(offTick, sample.EndSample, source.SourceIndex, 0, 0,
                        PackedMidiEvent.Note(
                            offTick, trackIndex, identity.Channel,
                            identity.Note, velocity, noteOn: false)));
                    plan.Add(new Planned(onTick, sample.StartSample, source.SourceIndex, 1, 0,
                        PackedMidiEvent.Note(
                            onTick, trackIndex, identity.Channel,
                            identity.Note, velocity, noteOn: true)));
                }

                FinishTrack(track, plan);
                tracks.Add(track);
            }
        }

        if (rhythm.Count > 0)
            tracks.Add(BuildRhythmTrack(timeline, rhythm, tracks.Count));

        int serializedNoteOnCount = tracks
            .SelectMany(track => track.Events)
            .OfType<MidiNoteEvent>()
            .Count(note => note.NoteOn);
        // Strict attack conservation: every unique audible source attack must
        // survive tick quantization as exactly one serialized NoteOn. Two
        // attacks that quantize to the same tick are emitted sequentially at
        // that tick; a source NoteEvent is never deleted.
        if (serializedNoteOnCount != uniqueAudibleAttackCount)
        {
            throw new InvalidOperationException(
                $"MIDI attack conservation failed: source attacks={uniqueAudibleAttackCount}, " +
                $"serialized NoteOn events={serializedNoteOnCount}.");
        }

        byte[] bytes = new MidiFileWriter(_ppq).Write(
            MidiConductor.FixedTransport(), tracks);
        return new MidiTranscriptionResult
        {
            Bytes = bytes,
            Tracks = tracks,
            Diagnostics = new MidiTranscriptionDiagnostics(
                notes.Count, rhythm.Count, samples.Count, collisions, oneTickNotes,
                uniqueAudibleAttackCount,
                BendRangeDiagnostics(tracks),
                _ppq,
                _quantizationLossyEventCount,
                (double)_maxQuantizationLossTicks,
                sampleAssignments.Count,
                SampleIdentityMappings(timeline, sampleAssignments, samples)),
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

    /// <summary>
    /// Expands the continuous YM2612 DAC sample playback into one trigger per
    /// audible hit. The timeline's <see cref="DacHitEvent"/> list is the
    /// per-trigger view ("one continuous stream can contain many audible
    /// hits"); without it, a whole song's DAC stream would serialize as a
    /// single NoteOn. Each expanded trigger is identified by the hit's
    /// CONTENT hash (SHA-256 of the hit slice), not the stream asset id: two
    /// byte-identical slices are the same underlying sample and share one MIDI
    /// note, while different content maps to a different note. Non-DAC
    /// playbacks (NES DPCM, Oki) are kept as-is.
    /// </summary>
    private static IReadOnlyList<SamplePlaybackEvent> ExpandDacHits(
        VisualizationTimeline timeline,
        IReadOnlyList<SamplePlaybackEvent> sourceSamples)
    {
        if (timeline.DacHits is null || timeline.DacHits.Length == 0)
            return sourceSamples;
        var expanded = new List<SamplePlaybackEvent>();
        foreach (SamplePlaybackEvent sample in sourceSamples)
        {
            if (!sample.SampleId.StartsWith("dac:", StringComparison.Ordinal))
            {
                expanded.Add(sample);
                continue;
            }
            bool anyHit = false;
            foreach (DacHitEvent hit in timeline.DacHits)
            {
                if (!string.Equals(hit.SampleId, sample.SampleId, StringComparison.Ordinal))
                    continue;
                anyHit = true;
                expanded.Add(new SamplePlaybackEvent(
                    sample.VoiceId,
                    hit.StartSample,
                    hit.EndSample,
                    hit.ContentHash,
                    MidiPitch: null,
                    PlaybackRate: 1.0,
                    Gain: hit.PeakLevel,
                    Pan: 0f,
                    Retrigger: false,
                    Looping: false));
            }
            // Identity with no inferred hits: keep the playback as-is so the
            // trigger is not silently lost.
            if (!anyHit)
                expanded.Add(sample);
        }
        return expanded;
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

    /// <summary>
    /// Deduplicates sample playbacks by their stable timeline identity
    /// (<see cref="SamplePlaybackEvent.SampleId"/>) and assigns each identity a
    /// deterministic sample-trigger note. The dedup happens BEFORE note
    /// allocation: playbacks are never counted per instance, and start offset,
    /// playback length or extraction differences cannot mint a new identity.
    ///
    /// Ordinals: YM2612 DAC identities ("dac:{catalogOrdinal}") carry the
    /// content-dedup ordinal produced by <see cref="DacSampleCatalog"/> (by
    /// first use, then sequence, then hash), so their MIDI note is stable
    /// across exports of the same capture. All other identities (NES DPCM, Oki
    /// ADPCM, ...) are ordered by first appearance after the DAC identities.
    /// </summary>
    private static Dictionary<string, DacTriggerAssignment> SampleAssignments(
        IReadOnlyList<SamplePlaybackEvent> samples,
        bool rhythmPresent)
    {
        var assignments = new Dictionary<string, DacTriggerAssignment>(StringComparer.Ordinal);
        SampleIdentity[] identities = samples
            .Select((sample, index) => (sample.SampleId, index))
            .GroupBy(pair => pair.SampleId, StringComparer.Ordinal)
            .Select(group => new SampleIdentity(
                group.Key,
                TryDacCatalogOrdinal(group.Key),
                group.Min(pair => pair.index)))
            .OrderBy(identity => identity.CatalogOrdinal ?? int.MaxValue)
            .ThenBy(identity => identity.FirstIndex)
            .ToArray();
        int nextOrdinal = (identities
            .Select(identity => identity.CatalogOrdinal)
            .Where(ordinal => ordinal.HasValue)
            .DefaultIfEmpty(-1)
            .Max() ?? -1) + 1;
        foreach (SampleIdentity identity in identities)
        {
            int ordinal = identity.CatalogOrdinal ?? nextOrdinal++;
            assignments.Add(identity.SampleId, MapDacOrdinal(ordinal, rhythmPresent));
        }
        return assignments;
    }

    /// <summary>
    /// Extracts the catalog dedup ordinal from a YM2612 DAC identity
    /// ("dac:{N}"); null for any other identity namespace.
    /// </summary>
    private static int? TryDacCatalogOrdinal(string sampleId)
    {
        if (!sampleId.StartsWith("dac:", StringComparison.Ordinal))
            return null;
        return int.TryParse(
            sampleId.AsSpan(4),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : null;
    }

    /// <summary>
    /// Deterministic identity-note assignment: <c>note = DacBaseNote + ordinal</c>
    /// (sample #0 -&gt; 36, #1 -&gt; 37, ...). The reserved 36..95 range holds the
    /// first 60 identities; beyond that the note extends across the usable
    /// 0..127 range ((36 + slot) mod 128), and once a (port, channel 10) lane
    /// holds 128 identities the next lane spills to the next port on the same
    /// channel 10. Identities are NEVER merged because the namespace fills.
    /// </summary>
    private static DacTriggerAssignment MapDacOrdinal(int ordinal, bool rhythmPresent)
    {
        if (ordinal < 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), "DAC sample ordinal must be non-negative.");
        int lane = ordinal / DacNotesPerLane;
        // Channel 10 is the DAC trigger channel; the native-rhythm track also
        // owns (port 0, channel 10) when present, so the DAC lanes start on
        // port 1 in that case. The channel is always 10 (0-based 9).
        int port = lane + (rhythmPresent ? 1 : 0);
        if (port >= 128)
            throw new InvalidOperationException(
                $"DAC sample identities require MIDI port {port}; the port meta (0x21) is 7-bit.");
        int note = (DacBaseNote + (ordinal % DacNotesPerLane)) % 128;
        return new DacTriggerAssignment((byte)port, DacChannel, note, ordinal);
    }

    private static string[] SampleIdentityMappings(
        VisualizationTimeline timeline,
        IReadOnlyDictionary<string, DacTriggerAssignment> assignments,
        IReadOnlyList<SamplePlaybackEvent> samples)
    {
        Dictionary<string, string> displayNames = (timeline.Samples ?? Array.Empty<SampleDefinition>())
            .ToDictionary(sample => sample.Id, sample => sample.DisplayName, StringComparer.Ordinal);
        return assignments
            .OrderBy(pair => pair.Value.Ordinal)
            .Select(pair =>
            {
                int playbacks = samples.Count(sample => string.Equals(
                    sample.SampleId, pair.Key, StringComparison.Ordinal));
                string name = displayNames.TryGetValue(pair.Key, out string? display)
                    ? display
                    : pair.Key;
                DacTriggerAssignment assignment = pair.Value;
                return $"{pair.Key} -> port {assignment.Port} ch {assignment.Channel + 1} " +
                    $"note {assignment.Note} ({name}, ordinal {assignment.Ordinal}, {playbacks} playbacks)";
            })
            .ToArray();
    }

    /// <summary>
    /// Amplitude to velocity. A reliable per-trigger amplitude is carried by
    /// producers such as NES DPCM and Oki ADPCM (<see cref="SamplePlaybackEvent.Gain"/>);
    /// those map gain through the same curve as native rhythm hits. The YM2612
    /// DAC decoder does NOT capture per-trigger amplitude (its gain is a
    /// constant 1f sentinel), so DAC velocity is the fixed default; the sample
    /// identity is unchanged either way. Hit-expanded DAC events carry their
    /// inferred peak level as Gain, which is a reliable per-trigger amplitude.
    /// </summary>
    private static int SampleVelocity(SamplePlaybackEvent sample)
    {
        if (sample.SampleId.StartsWith("dac:", StringComparison.Ordinal))
        {
            return sample.Gain > 0f && sample.Gain < 1f
                ? DrumVelocity(sample.Gain)
                : DefaultVelocity;
        }
        return DrumVelocity(sample.Gain);
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

    private static void ValidateSample(VisualizationTimeline timeline, SamplePlaybackEvent sample)
    {
        if (sample.StartSample < timeline.StartSample || sample.EndSample < sample.StartSample)
            throw new InvalidOperationException("Sample playback range is outside the timeline.");
        if (string.IsNullOrWhiteSpace(sample.SampleId))
            throw new InvalidOperationException("Sample playback has no stable sample identity.");
    }

    private MidiTrack BuildRhythmTrack(
        VisualizationTimeline timeline,
        IReadOnlyList<RhythmEvent> rhythm,
        int trackIndex)
    {
        var track = new MidiTrack(rhythm.Count * 2)
        {
            Name = "Native Rhythm",
            SourceVoiceId = "rhythm",
            Endpoint = new MidiEndpoint(0, PercussionChannel),
        };
        var plan = new List<Planned>(rhythm.Count * 2);
        for (int index = 0; index < rhythm.Count; index++)
        {
            RhythmEvent hit = rhythm[index];
            long tick = SampleToTick(timeline, hit.SamplePosition);
            int note = GeneralMidiDrumMapper.TryMap(hit, out int mapped)
                ? mapped
                : UnknownNativeDrumNote; // preserve the attack; do not invent a role.
            int velocity = DrumVelocity(hit.Strength);
            plan.Add(new Planned(tick, hit.SamplePosition, index, 0, 0,
                PackedMidiEvent.Note(tick, trackIndex, PercussionChannel, note, velocity, noteOn: true)));
            plan.Add(new Planned(checked(tick + 1), hit.SamplePosition, index, 1, 0,
                PackedMidiEvent.Note(checked(tick + 1), trackIndex, PercussionChannel, note, 0, noteOn: false)));
        }
        FinishTrack(track, plan);
        return track;
    }

    private int CountSameTickAttacks(VisualizationTimeline timeline, IReadOnlyList<IndexedNote> notes)
    {
        var seen = new HashSet<(string Voice, long Tick)>();
        int collisions = 0;
        foreach (IndexedNote note in notes)
        {
            long tick = decimal.ToInt64(decimal.Round(
                SampleToTickExact(timeline, note.Note.StartSample), 0, MidpointRounding.AwayFromZero));
            if (!seen.Add((note.Voice, tick)))
                collisions++;
        }
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
        long onTick = SampleToTick(timeline, pitchNote.StartSample);
        long offTick = SampleToTick(timeline, pitchNote.EndSample);
        if (offTick <= onTick)
        {
            // Representational floor, not source release fidelity: MIDI cannot
            // express a release at or before the attack tick, so a sub-tick
            // source note is serialized as off = on + 1. No source note is
            // ever deleted to avoid this.
            offTick = checked(onTick + 1);
            oneTickNotes++;
        }

        var statesByTick = new SortedDictionary<long, PitchState>
        {
            [onTick] = new(onTick, pitchNote.StartSample, pitchNote.InitialMidiNote),
        };
        foreach (SourcePitchPoint point in pitchNote.PitchCurve.Skip(1))
        {
            long tick = SampleToTick(timeline, point.Sample);
            // Several source transitions can quantize to one MIDI tick. The last
            // source state is the only state a synth can observe at that tick. If
            // it lands on NoteOn, fold it into the single attack bend below.
            statesByTick[tick] = new(
                tick,
                tick == onTick ? pitchNote.StartSample : point.Sample,
                point.MidiNote);
        }

        PitchState[] states = statesByTick.Values.ToArray();
        // The base note is the attack pitch rounded to the nearest semitone; the
        // initial bend carries the fractional part. No minimax search.
        int baseNote = ClampMidiNote(
            (long)Math.Round(pitchNote.InitialMidiNote, MidpointRounding.AwayFromZero));
        return new PlannedNote(source, onTick, offTick, baseNote, states);
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

    private long SampleToTick(VisualizationTimeline timeline, long sample)
    {
        decimal exact = SampleToTickExact(timeline, sample);
        long rounded = decimal.ToInt64(
            decimal.Round(exact, 0, MidpointRounding.AwayFromZero));
        if (exact != rounded)
        {
            _quantizationLossyEventCount++;
            decimal loss = Math.Abs(exact - rounded);
            if (loss > _maxQuantizationLossTicks)
                _maxQuantizationLossTicks = loss;
        }
        return rounded;
    }

    private decimal SampleToTickExact(VisualizationTimeline timeline, long sample) =>
        (decimal)(sample - timeline.StartSample) * (2m * _ppq) / timeline.SampleRate;

    private static string[] BendRangeDiagnostics(IReadOnlyList<MidiTrack> tracks) =>
        tracks
            .Select(track => new
            {
                track.SourceVoiceId,
                BendRange = track.PackedEvents
                    .Where(evt => evt.Kind == PackedMidiEventKind.BendRange)
                    .Select(evt => evt.A)
                    .DefaultIfEmpty(0)
                    .First(),
            })
            .Where(info => info.BendRange > 12)
            .OrderBy(info => info.SourceVoiceId, StringComparer.Ordinal)
            .Select(info => $"{info.SourceVoiceId}: bend-range={info.BendRange}")
            .ToArray();

    private static int ClampMidiNote(long value) => (int)Math.Clamp(value, 0L, 127L);

    private static void FinishTrack(MidiTrack track, List<Planned> plan)
    {
        plan = FoldSameTickPitchBends(plan);
        for (int index = 0; index < plan.Count; index++)
        {
            PackedMidiEvent evt = plan[index].Event;
            evt.SourceOrder = index;
            plan[index] = plan[index] with { Event = evt };
        }
        plan.Sort(static (left, right) => MidiEventOrder.Compare(left.Event, right.Event));
        foreach (Planned item in plan)
            track.AddPacked(item.Event);
        track.HasCanonicalEventOrder = true;
    }

    private static List<Planned> FoldSameTickPitchBends(IReadOnlyList<Planned> plan)
    {
        var winnerByTick = new Dictionary<long, int>();
        for (int index = 0; index < plan.Count; index++)
        {
            if (plan[index].Event.Kind != PackedMidiEventKind.PitchBend)
                continue;
            if (!winnerByTick.TryGetValue(plan[index].Tick, out int previous)
                || ComparePitchState(plan[previous], plan[index]) <= 0)
            {
                winnerByTick[plan[index].Tick] = index;
            }
        }

        if (winnerByTick.Count == plan.Count(item => item.Event.Kind == PackedMidiEventKind.PitchBend))
            return plan.ToList();

        var folded = new List<Planned>(plan.Count -
            plan.Count(item => item.Event.Kind == PackedMidiEventKind.PitchBend) + winnerByTick.Count);
        for (int index = 0; index < plan.Count; index++)
        {
            if (plan[index].Event.Kind == PackedMidiEventKind.PitchBend
                && winnerByTick.GetValueOrDefault(plan[index].Tick) != index)
                continue;
            folded.Add(plan[index]);
        }
        return folded;
    }

    private static int ComparePitchState(Planned left, Planned right)
    {
        int compare = left.SourceSample.CompareTo(right.SourceSample);
        if (compare != 0)
            return compare;
        compare = left.SourceIndex.CompareTo(right.SourceIndex);
        return compare != 0 ? compare : left.LocalOrder.CompareTo(right.LocalOrder);
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

    /// <summary>Stable sample-trigger destination for one deduplicated identity.</summary>
    private sealed record DacTriggerAssignment(byte Port, int Channel, int Note, int Ordinal);

    private sealed record SampleIdentity(string SampleId, int? CatalogOrdinal, int FirstIndex);

    private sealed record Planned(
        long Tick, long SourceSample, int SourceIndex, int Phase, int LocalOrder, PackedMidiEvent Event);
}