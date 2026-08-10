#nullable enable

using Fmp.Core.Timing;
using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>Optional export transforms. Timing accuracy is the default; quantization
/// and drum mapping are opt-in, applied only after the musical-time conversion.</summary>
internal sealed class MusicalMidiExportOptions
{
    public static readonly MusicalMidiExportOptions Default = new();

    /// <summary>off | eighth | sixteenth | thirtysecond (grid snapping for note-ons).</summary>
    public string Quantize { get; init; } = "off";

    /// <summary>Assign percussive voices to MIDI channel 9 (GM percussion).</summary>
    public bool UsePercussionChannel { get; init; } = true;

    /// <summary>Base MIDI note assigned to the first distinct rhythm voice.</summary>
    public int PercussionNoteBase { get; init; } = 36;

    /// <summary>Emit pitch-bend for microtonal / intra-note pitch movement (Batch 4).</summary>
    public bool EmitPitchBend { get; init; } = true;

    /// <summary>Semitones of the configured pitch-bend range (RPN), default 2.</summary>
    public int BendRangeSemitones { get; init; } = 2;

    public bool EmitInstrumentMetadata { get; init; } = true;

    /// <summary>Emit loop/section markers on the conductor track.</summary>
    public bool EmitMarkers { get; init; } = true;

    /// <summary>Emit conductor track name / source metadata / timing-confidence text.</summary>
    public bool EmitConductorMetadata { get; init; } = true;

    /// <summary>Default note velocity (1–127) when a voice override does not set one.</summary>
    public int Velocity { get; init; } = 90;

    /// <summary>Per-voice transforms keyed by <c>ChannelId</c>. A missing entry keeps defaults.</summary>
    public IReadOnlyList<VoiceExportOverride> VoiceOverrides { get; init; } = Array.Empty<VoiceExportOverride>();

    /// <summary>Resolve the override for a channel, or the voice's default if none.</summary>
    public VoiceExportOverride OverrideFor(string channelId)
    {
        foreach (VoiceExportOverride vo in VoiceOverrides)
        {
            if (string.Equals(vo.ChannelId, channelId, StringComparison.Ordinal))
                return vo;
        }
        return VoiceExportOverride.Default(channelId);
    }
}

/// <summary>
/// Per-voice export transform. All fields are optional floats/ints; a value of
/// <c>null</c> means "leave whatever the exporter would otherwise do".
/// </summary>
internal sealed class VoiceExportOverride
{
    public static VoiceExportOverride Default(string channelId) => new(channelId);

    public VoiceExportOverride(string channelId)
    {
        ChannelId = channelId;
        Include = true;
        Program = null;
        Channel = null;
        Velocity = null;
        TransposeSemitones = 0;
    }

    public string ChannelId { get; }

    /// <summary>False to exclude this voice from the export entirely (no track, no notes).</summary>
    public bool Include { get; set; } = true;

    /// <summary>GM program number (0–127) forced on this voice's track; null = no override.</summary>
    public int? Program { get; set; }

    /// <summary>MIDI channel (0–15) forced on this voice's track; null = default allocation.</summary>
    public int? Channel { get; set; }

    /// <summary>Optional bank-select MSB. No bank or program event is emitted by default.</summary>
    public int? Bank { get; set; }

    /// <summary>Note velocity (1–127); null = use option default.</summary>
    public int? Velocity { get; set; }

    /// <summary>Semitones to transpose this voice; 0 = none.</summary>
    public int TransposeSemitones { get; set; }
}

/// <summary>The exported MIDI stream plus the events it was built from.</summary>
internal sealed class MusicalMidiExportResult
{
    public required byte[] Bytes { get; init; }

    public required IReadOnlyList<MidiTrack> Tracks { get; init; }

    public required TimingDiagnostics Diagnostics { get; init; }

    /// <summary>
    /// The global origin shift applied to make every exported tick nonnegative
    /// (section 21), in quarter notes. The first emitted event maps to tick 0
    /// (rounded); a conductor tempo at the map start, an early pitch bend, a
    /// rhythm trigger, or a downbeat/loop marker can nudge this above zero.
    /// Exposed so the timing report can print the configured-PPQ origin tick.
    /// </summary>
    public double OriginOffsetQuarters { get; init; }
}

/// <summary>
/// Turns a <see cref="VisualizationTimeline"/> into a Format 1 MIDI file by routing
/// every event through the canonical <see cref="MusicalTimeMap"/>. This class holds
/// NO timing logic of its own — it only maps samples to absolute ticks, applies the
/// non-negative origin shift, and allocates tracks/channels (a separate policy).
/// </summary>
internal sealed class MusicalMidiExporter
{
    private readonly MusicalTimeMap _map;
    private readonly int _ppq;
    private readonly MusicalMidiExportOptions _options;

    /// <summary>Deterministic monotonic source-sequence key assigned to every exported
    /// event (conductor, program, note, bend, …) so the writer's equal-tick/equal-rank
    /// sort can be fully deterministic via <see cref="MidiEventBase.SourceOrder"/>.</summary>
    private int _sourceOrder;

    public MusicalMidiExporter(MusicalTimeMap map, int ppq, MusicalMidiExportOptions? options = null)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        if (ppq <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppq));
        _ppq = ppq;
        _options = (options ?? MusicalMidiExportOptions.Default);
        if (_options.BendRangeSemitones is < 1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(options), "Bend range must be in [1, 127].");
    }

    /// <summary>Optional diagnostics (from the map's builder) surfaced on the result.</summary>
    public TimingDiagnostics? Diagnostics { get; set; }

    public MusicalMidiExportResult Export(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        double originOffsetQuarters = ComputeOriginOffset(timeline);

        var conductor = new List<MidiEventBase>();
        BuildConductor(timeline, originOffsetQuarters, conductor);

        TrackAllocator allocator = BuildTracks(timeline);
        Dictionary<MidiChannelDomain, int> domainBendRanges = BuildDomainBendRanges(timeline, allocator);
        EmitDomainBendRanges(allocator, domainBendRanges);
        var emittedNoteTicks = new HashSet<(MidiTrackKey Key, long Tick)>();
        foreach (NoteEvent note in (timeline.Notes ?? Array.Empty<NoteEvent>())
                     .Where(IsNoteEmitted)
                     .OrderBy(n => TrackKeyFor(n), Comparer<MidiTrackKey>.Create(CompareTrackKeys))
                     .ThenBy(n => n.StartSample)
                     .ThenByDescending(n => n.EndSample)
                     .ThenBy(n => n.InitialMidiNote))
        {
            MidiTrackKey key = TrackKeyFor(note);
            TrackSlot slot = allocator.SlotFor(key);
            if (slot is null)
                continue;
            // One source-time collision has one musical decision. The ordering above
            // makes the winner independent of decoder encounter order.
            if (!emittedNoteTicks.Add((key, MapTick(note.StartSample, originOffsetQuarters))))
                continue;
            EmitNote(slot, note, originOffsetQuarters, domainBendRanges);
        }

        // Rhythm voices → percussion pitches (Batch 4 drum allocation). Each rhythm
        // track is keyed by its instrument identity (R9), and the percussion pitch is
        // allocated per identity so distinct instruments get distinct drum notes.
        var drumNoteByIdentity = new Dictionary<MidiTrackKey, int>();
        int nextDrum = _options.PercussionNoteBase;
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (!IsRhythmEmitted(rhythm))
                continue;
            MidiTrackKey rhythmKey = RhythmKeyFor(rhythm);
            TrackSlot slot = allocator.SlotFor(rhythmKey);
            if (slot is null)
                continue;
            if (!drumNoteByIdentity.TryGetValue(rhythmKey, out int note))
            {
                note = nextDrum++;
                if (note is < 0 or > 127)
                    throw new InvalidOperationException(
                        $"Percussion note exhaustion: rhythm domain '{rhythmKey}' cannot be assigned a MIDI note.");
                drumNoteByIdentity[rhythmKey] = note;
            }
            AddTrackEvent(slot.Track, new MidiNoteEvent(
                MapTick(rhythm.SamplePosition, originOffsetQuarters),
                slot.Index, slot.Channel, note, 100, NoteOn: true));
            AddTrackEvent(slot.Track, new MidiNoteEvent(
                MapTick(rhythm.SamplePosition, originOffsetQuarters) + ShortHitTicks,
                slot.Index, slot.Channel, note, 100, NoteOn: false));
        }

        ApplyQuantizationToGrid(allocator);

        var writer = new MidiFileWriter(_ppq);
        var tracks = allocator.Tracks.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
        byte[] bytes = writer.Write(conductor, tracks);
        return new MusicalMidiExportResult
        {
            Bytes = bytes,
            Tracks = tracks,
            Diagnostics = Diagnostics ?? new TimingDiagnostics
            {
                AnchorCount = 0,
                TempoSource = TimingSource.DriverBeatAnchors,
                PhaseSource = TimingSource.DriverBeatAnchors,
            },
            OriginOffsetQuarters = originOffsetQuarters,
        };
    }

    private readonly TimingDiagnostics NullDiagnostics = new()
    {
        AnchorCount = 0,
        TempoSource = TimingSource.DriverBeatAnchors,
        PhaseSource = TimingSource.DriverBeatAnchors,
    };

    private long MapTick(long sample, double originOffsetQuarters) =>
        _map.SampleToTick(sample, _ppq)
        + _map.QuarterPositionToTick(originOffsetQuarters, _ppq);

    private int ShortHitTicks => Math.Max(1, _ppq / 32);

    /// <summary>Single source of truth for whether a note is actually emitted (§21).
    /// Export emits a note only when it has positive duration AND its voice is
    /// included (excluded voices get no track/slot). ComputeOriginOffset must derive
    /// the global origin from EXACTLY the same set, so the two share this predicate —
    /// a note either side drops (non-positive duration or voice-excluded) emits no
    /// tick and must never push the origin back.</summary>
    private bool IsNoteEmitted(NoteEvent note) =>
        note is not null && note.EndSample > note.StartSample && _options.OverrideFor(note.ChannelId).Include;

    /// <summary>Single source of truth for whether a rhythm trigger is emitted (§21).
    /// Export/BuildTracks drop rhythm channels whose VoiceExportOverride.Include is
    /// false (no track, no slot, no events); ComputeOriginOffset must apply the same
    /// predicate so an excluded channel's early trigger cannot delay the origin.</summary>
    private bool IsRhythmEmitted(RhythmEvent rhythm) =>
        rhythm is not null && _options.OverrideFor(rhythm.ChannelId).Include;

    /// <summary>Single source of truth for whether a note serializes a pitch-bend
    /// family (§21). Used by BOTH EmitNote (to decide bend emission) and
    /// ComputeOriginOffset (to decide whether the note's pitch changes may contribute
    /// to the origin), so the two can never diverge. A note emits bends only when
    /// EmitPitchBend is set AND it has pitch changes OR a fractional / non-finite
    /// initial note (an initial bend so it sounds at its true pitch); any other note
    /// produces a single round-pitch note with no bend infrastructure — and must not
    /// fold pitch-change samples into the origin.</summary>
    private bool ShouldFoldPitch(NoteEvent note)
    {
        if (!_options.EmitPitchBend)
            return false;
        if (note.Pitch is { Count: > 0 })
            return true;
        return !double.IsFinite(note.InitialMidiNote)
            || Math.Abs(note.InitialMidiNote - Math.Round(note.InitialMidiNote)) > 1e-6;
    }

    private void EmitNote(TrackSlot slot, NoteEvent note, double originOffsetQuarters,
        IReadOnlyDictionary<MidiChannelDomain, int> domainBendRanges)
    {
        VoiceExportOverride voiceOverride = _options.OverrideFor(note.ChannelId);
        int vel = Math.Clamp(voiceOverride.Velocity ?? _options.Velocity, 1, 127);
        int transpose = voiceOverride.TransposeSemitones;

        bool needsBend = ShouldFoldPitch(note);
        int bendRange = needsBend ? domainBendRanges[slot.Domain] : 0;

        // When no bend is needed, emit a single note at the rounded pitch with no
        // bend infrastructure at all (keeps constant-pitch output minimal and
        // byte-identical to the pre-bend exporter for the common case).
        IReadOnlyList<NotePlaybackSegment> segments = needsBend
            ? new[] { new NotePlaybackSegment(note.StartSample, note.EndSample,
                double.IsFinite(note.InitialMidiNote) ? note.InitialMidiNote : 60,
                note.Pitch.OrderBy(p => p.SamplePosition).ThenBy(p => p.MidiNote).ToArray()) }
            : new[] { new NotePlaybackSegment(
                note.StartSample, note.EndSample,
                note.InitialMidiNote, Array.Empty<PitchChange>()) };

        foreach (NotePlaybackSegment segment in segments)
        {
            long segOn = MapTick(segment.StartSample, originOffsetQuarters);
            long segOff = MapTick(segment.EndSample, originOffsetQuarters);
            if (segOff <= segOn)
                segOff = segOn + 1;
            int pitch = (int)Math.Clamp(Math.Round(segment.BaseMidiNote) + transpose, 0, 127);
            AddTrackEvent(slot.Track, new MidiNoteEvent(segOn, slot.Index, slot.Channel, pitch, vel, NoteOn: true));
            AddTrackEvent(slot.Track, new MidiNoteEvent(segOff, slot.Index, slot.Channel, pitch, vel, NoteOn: false));

            if (!needsBend)
                continue;
            // Initial fractional correction so the note starts at its true pitch.
            double baseNote = segment.BaseMidiNote;
            int initialBend = EncodeBend(baseNote + transpose - pitch, bendRange);
            ValidateEffectivePitch(note, segment.StartSample, baseNote + transpose, pitch, initialBend, bendRange);
            AddTrackEvent(slot.Track, new MidiPitchBendEvent(segOn, slot.Index, slot.Channel, initialBend));
            // Each change, relative to the segment's base note.
            foreach (PitchChange change in segment.Changes)
            {
                if (!double.IsFinite(change.MidiNote))
                    continue;
                AddTrackEvent(slot.Track, new MidiPitchBendEvent(
                    MapTick(change.SamplePosition, originOffsetQuarters),
                    slot.Index, slot.Channel,
                    EncodeBend(change.MidiNote + transpose - pitch, bendRange)));
                ValidateEffectivePitch(note, change.SamplePosition, change.MidiNote + transpose,
                    pitch, EncodeBend(change.MidiNote + transpose - pitch, bendRange), bendRange);
            }
        }
    }

    /// <summary>Assigns the next deterministic source-sequence key and returns the event.</summary>
    private MidiEventBase WithSourceOrder(MidiEventBase evt)
    {
        evt.SourceOrder = _sourceOrder++;
        return evt;
    }

    /// <summary>Appends an event to a track with a deterministic source-sequence key.</summary>
    private void AddTrackEvent(MidiTrack track, MidiEventBase evt)
    {
        evt.SourceOrder = _sourceOrder++;
        track.Events.Add(evt);
    }

    /// <summary>7-bit bend value for a semitone offset within the configured range.</summary>
    private static int EncodeBend(double semitones, int rangeSemitones)
    {
        if (!double.IsFinite(semitones))
            return 0;
        double halfRange = Math.Max(1, rangeSemitones);
        double scaled = (semitones / halfRange) * 8191.0;
        return Math.Clamp((int)Math.Round(scaled), -8192, 8191);
    }

    private int RequiredBendRange(NoteEvent note)
    {
        ValidateSourcePitch(note);
        double basePitch = double.IsFinite(note.InitialMidiNote) ? note.InitialMidiNote : 60;
        double max = note.Pitch.Where(p => double.IsFinite(p.MidiNote))
            .Select(p => Math.Abs(p.MidiNote - Math.Round(basePitch)))
            .Append(Math.Abs(basePitch - Math.Round(basePitch)))
            .DefaultIfEmpty(0).Max();
        int required = Math.Max(_options.BendRangeSemitones, (int)Math.Ceiling(max));
        if (required > 127)
            throw new InvalidOperationException(
                $"Source note '{note.ChannelId}' requires a pitch-bend range of {required} semitones; MIDI supports at most 127.");
        return Math.Max(1, required);
    }

    private static void ValidateSourcePitch(NoteEvent note)
    {
        if (!double.IsFinite(note.InitialMidiNote) || note.InitialMidiNote is < 0 or > 127)
            throw new InvalidOperationException(
                $"Source note '{note.ChannelId}' at sample {note.StartSample} has invalid initial MIDI pitch " +
                $"'{note.InitialMidiNote}'. Expected a finite value in [0, 127].");
        foreach (PitchChange change in note.Pitch)
        {
            if (!double.IsFinite(change.MidiNote) || change.MidiNote is < 0 or > 127)
                throw new InvalidOperationException(
                    $"Source note '{note.ChannelId}' at sample {note.StartSample} has invalid pitch " +
                    $"'{change.MidiNote}' at sample {change.SamplePosition}. Expected a finite value in [0, 127].");
        }
    }

    private static void ValidateEffectivePitch(NoteEvent note, long sample, double planned,
        int midiNote, int bend, int range)
    {
        double reconstructed = midiNote + bend / 8191.0 * range;
        double tolerance = Math.Max(0.01, range / 8191.0 * 1.5);
        if (Math.Abs(reconstructed - planned) > tolerance)
            throw new InvalidOperationException(
                $"Pitch reconstruction failed for source note '{note.ChannelId}' at sample {sample}: " +
                $"planned {planned:0.###}, MIDI note {midiNote}, bend {bend}, range {range}, " +
                $"reconstructed {reconstructed:0.###}.");
    }

    /// <summary>
    /// Legacy helper retained for callers compiled against the previous planner.
    /// Semantic export now emits one note and selects a domain-wide bend range.
    /// </summary>
    private List<NotePlaybackSegment> BuildPlaybackSegments(NoteEvent note, int rangeSemitones)
    {
        double basePitch = double.IsFinite(note.InitialMidiNote) ? note.InitialMidiNote : 60;
        var result = new List<NotePlaybackSegment>();
        var changes = new List<PitchChange>();
        double segBase = basePitch;
        double segStart = note.StartSample;

        void CloseSegment(double endSample)
        {
            result.Add(new NotePlaybackSegment(
                (long)Math.Round(segStart), (long)Math.Round(endSample), segBase, changes.ToArray()));
        }

        foreach (PitchChange change in note.Pitch
                     .OrderBy(p => p.SamplePosition)
                     .ThenBy(p => p.MidiNote))
        {
            if (!double.IsFinite(change.MidiNote))
                continue;
            double needed = change.MidiNote - segBase;
            if (Math.Abs(needed) > Math.Max(1, rangeSemitones))
            {
                // Contour left this segment's range: close it at this change's
                // sample and start a fresh segment centred on the new pitch.
                CloseSegment(change.SamplePosition);
                segStart = change.SamplePosition;
                segBase = change.MidiNote;
                changes.Clear();
                continue;
            }
            changes.Add(change);
        }
        CloseSegment(note.EndSample);
        return result;
    }

    private readonly record struct NotePlaybackSegment(
        long StartSample,
        long EndSample,
        double BaseMidiNote,
        IReadOnlyList<PitchChange> Changes);

    private void ApplyQuantizationToGrid(TrackAllocator allocator)
    {
        string mode = (_options.Quantize ?? "off").ToLowerInvariant();
        int gridTicks = mode switch
        {
            "1/8" => _ppq / 2,
            "1/16" => _ppq / 4,
            "1/32" => _ppq / 8,
            _ => 0,
        };
        if (gridTicks <= 0)
            return;
        foreach ((int index, MidiTrack track) in allocator.Tracks.OrderBy(pair => pair.Key))
        {
            foreach (MidiEventBase evt in track.Events)
            {
                if (evt is MidiNoteEvent note && note.NoteOn)
                {
                    // Quantize start to the nearest grid line; keep end duration.
                    long group = (note.Tick + gridTicks / 2) / gridTicks * gridTicks;
                    note.Tick = Math.Max(0, group);
                }
            }
        }
    }

    private double ComputeOriginOffset(VisualizationTimeline timeline)
    {
        // The global origin must make every exported tick nonnegative (§21). The
        // map's FirstSample is the earliest sample any conductor event (tempo /
        // time signature / source marker) covers, and every note/rhythm sample sits
        // at or after it, so its quarter position is the lower bound. Include it
        // (plus any earlier downbeat marker) so a conductor tempo at the map start
        // is never left at a negative tick — a note/rhythm-only origin would miss it.
        double minQuarter = _map.SampleToQuarterPosition(_map.FirstSample);
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            // The origin must derive ONLY from events that will actually be emitted
            // (§21). Export emits a note only when it has positive duration AND its
            // voice is included (excluded voices get no track/slot). Shared via
            // IsNoteEmitted so the origin can never drift from emission — a dropped
            // early note would otherwise push back the origin and delay the first
            // tempo/event off tick 0.
            if (!IsNoteEmitted(note))
                continue;
            minQuarter = Math.Min(minQuarter, _map.SampleToQuarterPosition(note.StartSample));
            // Pitch bends are emitted at every pitch-change sample (which may sit
            // before the note's own start), so the global origin must cover them too
            // (§21) — but ONLY when bends are actually serialized (ShouldFoldPitch).
            // When EmitPitchBend is off or the note carries no pitch changes, no bend
            // is emitted, so non-finite/early pitch changes must not shift the origin.
            if (!ShouldFoldPitch(note))
                continue;
            foreach (PitchChange change in note.Pitch)
            {
                if (!double.IsFinite(change.MidiNote))
                    continue;
                minQuarter = Math.Min(minQuarter, _map.SampleToQuarterPosition(change.SamplePosition));
            }
        }
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            // The origin must derive ONLY from rhythm triggers that are actually
            // emitted (§21). Export/BuildTracks drop rhythm channels whose
            // VoiceExportOverride.Include is false (no track, no events); shared via
            // IsRhythmEmitted so an excluded channel's early trigger can never delay
            // the origin off tick 0.
            if (!IsRhythmEmitted(rhythm))
                continue;
            minQuarter = Math.Min(minQuarter, _map.SampleToQuarterPosition(rhythm.SamplePosition));
        }
        // Loop/section markers and the first-downbeat marker are projected onto the
        // conductor ONLY when EmitMarkers is set; they may precede the first
        // note/rhythm and must never map to a negative tick (§21). When markers are
        // disabled they are not emitted, so they must not push the origin back —
        // otherwise a marker before the map start would shift every real event and
        // leave leading ticks at the DAW's default tempo.
        if (_options.EmitMarkers)
        {
            foreach (LoopMarker loop in timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            {
                if (loop is null) continue;
                minQuarter = Math.Min(minQuarter, _map.SampleToQuarterPosition(loop.SamplePosition));
            }
            if (_map.FirstDownbeatQuarter is double downbeat)
                minQuarter = Math.Min(minQuarter, downbeat);
        }

        // origin must be nonnegative (§21); ceil so the minimum event maps to tick 0.
        double baseOffset = Math.Max(0, Math.Ceiling(-minQuarter));
        Meter? meter = _map.Meter;
        if (meter is not null)
        {
            double qpb = meter.QuartersPerBar;
            if (qpb > 0)
                baseOffset = Math.Ceiling(baseOffset / qpb) * qpb;
        }
        return baseOffset;
    }

    private void BuildConductor(VisualizationTimeline timeline, double originOffsetQuarters, List<MidiEventBase> conductor)
    {
        // Track name + source metadata text.
        if (_options.EmitConductorMetadata)
        {
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, timeline.Source?.Title ?? "MDPlayer Export")));
            if (!string.IsNullOrWhiteSpace(timeline.Source?.SourceFormat))
                conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, $"src-format {timeline.Source.SourceFormat}")));
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, $"sample-rate {timeline.SampleRate}")));
        }

        // Set Tempo per segment, at each segment's start tick. Adjacent segments
        // whose emitted µs/qn value (the MIDI integer actually written) is identical
        // produce ONE Set Tempo event (§19) — the raw BPM is never compared.
        int? lastUsPerQuarter = null;
        foreach (TempoSegment segment in _map.Segments)
        {
            int us = segment.MicrosecondsPerQuarter;
            if (us == lastUsPerQuarter)
                continue;
            lastUsPerQuarter = us;
            long tick = MapTick(segment.StartSample, originOffsetQuarters);
            conductor.Add(WithSourceOrder(new MidiTempoEvent(tick, us)));
        }

        // Time Signature when known; omit otherwise (DAW uses its default).
        if (_map.Meter is Meter meter)
        {
            conductor.Add(WithSourceOrder(new MidiTimeSignatureEvent(
                0,
                meter.Numerator, meter.Denominator)));
        }

        // Markers.
        if (_options.EmitMarkers)
        {
            conductor.Add(WithSourceOrder(new MidiMarkerEvent(MapTick(_map.FirstSample, originOffsetQuarters), "SOURCE_START")));
            if (_map.FirstDownbeatQuarter is double downbeat)
            {
                long downbeatTick = _map.QuarterPositionToTick(downbeat + originOffsetQuarters, _ppq);
                conductor.Add(WithSourceOrder(new MidiMarkerEvent(downbeatTick, "FIRST_DOWNBEAT")));
            }
            foreach (LoopMarker loop in timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            {
                string name = loop.Kind switch
                {
                    LoopMarkerKind.Start => "LOOP_START",
                    LoopMarkerKind.Restart => "LOOP_END",
                    _ => "LOOP_MARK",
                };
                conductor.Add(WithSourceOrder(new MidiMarkerEvent(MapTick(loop.SamplePosition, originOffsetQuarters), name)));
            }
        }

        // Timing-confidence text so the DAW/user sees what was inferred.
        if (_options.EmitConductorMetadata)
        {
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(
                MapTick(_map.FirstSample, originOffsetQuarters),
                0x01,
                TimingConfidenceText())));
        }
    }

    private string TimingConfidenceText()
    {
        var parts = new List<string>
        {
            $"tempo-source={_map.Segments[0].Source}",
            $"segments={_map.Segments.Count}",
            _map.Meter is not null ? $"meter={_map.Meter}" : "meter=unknown",
            _map.FirstDownbeatQuarter is not null ? "downbeat=known" : "downbeat=unknown",
            $"sample0-quarter={_map.SampleToQuarterPosition(_map.StartSample):0.###}",
        };
        return "timing " + string.Join(";", parts);
    }

    private TrackAllocator BuildTracks(VisualizationTimeline timeline)
    {
        var allocator = new TrackAllocator();
        int index = 1;

        // Deterministic, source-order-independent enumeration of the distinct
        // MidiTrackKey values carried by emitted notes + rhythm triggers. Building
        // the full key set up front (not lazily) lets us sort keys into a stable
        // cross-run order, so track indices, MIDI channels and names are repeatable.
        // Each key remembers the ORIGINAL source ChannelId that produced it: voice
        // overrides (VoiceExportOverride) are registered per ChannelId, so the
        // override must resolve against that real channel — not a synthetic key.
        var keyOrder = new List<MidiTrackKey>();
        var representativeByKey = new Dictionary<MidiTrackKey, string>();
        var seen = new HashSet<MidiTrackKey>();
        void AddKey(MidiTrackKey key, string channelId)
        {
            if (seen.Add(key))
            {
                keyOrder.Add(key);
                representativeByKey[key] = channelId;
            }
        }
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note is null || !IsNoteEmitted(note))
                continue;
            AddKey(TrackKeyFor(note), note.ChannelId);
        }
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null || !IsRhythmEmitted(rhythm))
                continue;
            AddKey(RhythmKeyFor(rhythm), rhythm.ChannelId);
        }
        keyOrder.Sort(CompareTrackKeys);

        ValidateChannelState(keyOrder, representativeByKey);

        foreach (MidiTrackKey key in keyOrder)
        {
            string channelId = representativeByKey[key];
            VoiceExportOverride voiceOverride = _options.OverrideFor(channelId);
            if (!voiceOverride.Include)
                continue; // excluded voice: no track, no notes.
            allocator.Add(key, index++, channelId, voiceOverride, _options, WithSourceOrder);
        }
        return allocator;
    }

    private Dictionary<MidiChannelDomain, int> BuildDomainBendRanges(
        VisualizationTimeline timeline, TrackAllocator allocator)
    {
        var ranges = new Dictionary<MidiChannelDomain, int>();
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (!IsNoteEmitted(note))
                continue;
            ValidateSourcePitch(note);
            if (!ShouldFoldPitch(note))
                continue;
            TrackSlot? slot = allocator.SlotFor(TrackKeyFor(note));
            if (slot is null)
                continue;
            int required = RequiredBendRange(note);
            ranges[slot.Domain] = ranges.TryGetValue(slot.Domain, out int current)
                ? Math.Max(current, required) : required;
        }
        return ranges;
    }

    private void EmitDomainBendRanges(TrackAllocator allocator,
        IReadOnlyDictionary<MidiChannelDomain, int> ranges)
    {
        foreach ((MidiChannelDomain domain, int range) in ranges
                     .OrderBy(pair => pair.Key.Device.Type)
                     .ThenBy(pair => pair.Key.Device.Instance)
                     .ThenBy(pair => pair.Key.VoiceFamily)
                     .ThenBy(pair => pair.Key.SourceChannel))
        {
            TrackSlot slot = allocator.Slots
                .Where(pair => pair.Value.Domain == domain)
                .OrderBy(pair => pair.Value.Index)
                .Select(pair => pair.Value)
                .First();
            // RPN state is channel-global across tracks. Initialize it once at
            // tick zero with the maximum range for the complete source domain.
            AddTrackEvent(slot.Track, new MidiBendRangeEvent(0, slot.Index, slot.Channel, range));
        }
    }

    private void ValidateChannelState(IReadOnlyList<MidiTrackKey> keys,
        IReadOnlyDictionary<MidiTrackKey, string> representatives)
    {
        var owners = new Dictionary<int, (MidiTrackKey Key, int? Program, int? Bank)>();
        foreach (MidiTrackKey key in keys)
        {
            VoiceExportOverride ov = _options.OverrideFor(representatives[key]);
            if (ov.Channel is not int channel) continue;
            if (owners.TryGetValue(channel, out var prior)
                && (prior.Program != ov.Program || prior.Bank != ov.Bank))
                throw new InvalidOperationException(
                    $"MIDI channel {channel} has incompatible state: domain '{prior.Key}' " +
                    $"requests program/bank {prior.Program?.ToString() ?? "none"}/{prior.Bank?.ToString() ?? "none"}, " +
                    $"domain '{key}' requests {ov.Program?.ToString() ?? "none"}/{ov.Bank?.ToString() ?? "none"}.");
            owners[channel] = (key, ov.Program, ov.Bank);
        }
    }

    /// <summary>Deterministic total order over keys (chip, then identity family,
    /// then canonical instrument string, then source channel).</summary>
    private static int CompareTrackKeys(MidiTrackKey a, MidiTrackKey b)
    {
        int c = a.Device.Type.CompareTo(b.Device.Type);
        if (c != 0) return c;
        c = a.Device.Instance.CompareTo(b.Device.Instance);
        if (c != 0) return c;
        c = a.VoiceFamily.CompareTo(b.VoiceFamily);
        if (c != 0) return c;
        c = a.Instrument.Family.CompareTo(b.Instrument.Family);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Instrument.Canonical, b.Instrument.Canonical);
        if (c != 0) return c;
        return a.SourceChannel.CompareTo(b.SourceChannel);
    }

    /// <summary>The MIDI track key owning a source note. The note is owned by the
    /// instrument active at its NoteOn for its ENTIRE lifetime (R4) — a register
    /// change mid-note never retargets the note to another track.</summary>
    private MidiTrackKey TrackKeyFor(NoteEvent note)
    {
        if (note.Domain is SourceDomainKey domain
            && InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity typedInstrument))
            return new MidiTrackKey(domain.Device, domain.VoiceFamily, domain.Index, typedInstrument);
        if (!TryParseSourceDomain(note.ChannelId, out DeviceId device, out VoiceKind voice, out int sourceChannel)
            || !InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity instrument))
            return PlaceholderKey(note.ChannelId);
        return new MidiTrackKey(device, voice, sourceChannel, instrument);
    }

    /// <summary>The MIDI track key owning a rhythm trigger, keyed by the rhythm
    /// instrument identity (R9). The exporter works from the identity — never from
    /// the raw voice-name string — so a future rename cannot change grouping.</summary>
    private MidiTrackKey RhythmKeyFor(RhythmEvent rhythm)
    {
        if (rhythm.Domain is SourceDomainKey domain)
        {
            string domainInstrument = string.IsNullOrWhiteSpace(rhythm.InstrumentId)
                ? $"rhythm:{rhythm.Voice.ToLowerInvariant()}" : rhythm.InstrumentId;
            if (!InstrumentIdentity.TryParse(domainInstrument, out InstrumentIdentity domainIdentity))
                domainIdentity = new InstrumentIdentity(IdentityFamily.Rhythm, 0, $"rhythm:{domainInstrument}");
            return new MidiTrackKey(domain.Device, domain.VoiceFamily, domain.Index, domainIdentity);
        }
        if (!TryParseSourceDomain(rhythm.ChannelId, out DeviceId device, out VoiceKind voice, out int sourceChannel))
            return PlaceholderKey(rhythm.ChannelId);
        string normalized = string.IsNullOrWhiteSpace(rhythm.InstrumentId)
            ? $"rhythm:{rhythm.Voice.ToLowerInvariant()}"
            : rhythm.InstrumentId;
        if (!InstrumentIdentity.TryParse(normalized, out InstrumentIdentity instrument))
            instrument = new InstrumentIdentity(IdentityFamily.Rhythm, 0, $"rhythm:{normalized}");
        return new MidiTrackKey(device, voice, sourceChannel, instrument);
    }

    /// <summary>Reads the source chip + channel from a VoiceId-style ChannelId
    /// (e.g. "ym2608.0.fm.2" → Ym2608, channel 1). The numeric suffix is 1-based;
    /// we fold it to zero-based SourceChannel so (Chip, SourceChannel) identifies a
    /// source voice, and cross-Kind separation (FM vs SSG on the same numbered
    /// channel) comes from InstrumentIdentity.Family in the key.</summary>
    private static bool TryParseSourceChannel(string channelId, out ChipType chip, out int sourceChannel)
    {
        chip = ChipType.Unknown;
        sourceChannel = 0;
        if (string.IsNullOrWhiteSpace(channelId))
            return false;
        string[] parts = channelId.Split('.');
        if (parts.Length < 4)
            return false;
        if (!DeviceId.TryParse(parts[0] + "." + parts[1], out DeviceId device))
            return false;
        string suffix = parts[^1];
        if (!int.TryParse(suffix, out int n) || n < 1)
            return false;
        chip = device.Type;
        sourceChannel = n - 1;
        return true;
    }

    private static bool TryParseSourceDomain(string channelId, out DeviceId device,
        out VoiceKind voice, out int sourceChannel)
    {
        device = default;
        voice = VoiceKind.Pcm;
        sourceChannel = 0;
        if (string.IsNullOrWhiteSpace(channelId)) return false;
        string[] parts = channelId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !DeviceId.TryParse(parts[0] + "." + parts[1], out device)) return false;
        string kind = parts[2];
        if (kind == "fm3" && parts.Length > 3 && parts[3] == "op") kind = "fm3";
        if (!Enum.TryParse(kind, true, out voice))
        {
            if (kind == "channel")
                voice = VoiceKind.MidiChannel;
            else if (kind == "adpcm-b")
                voice = VoiceKind.Adpcm;
            else
                return false;
        }
        string suffix = parts[^1];
        if (!int.TryParse(suffix, out int n) || n < 1)
            return false;
        sourceChannel = n - 1;
        return true;
    }

    /// <summary>Collapses notes whose instrument identity could not be resolved to a
    /// single per-source-channel placeholder track (R11). The key is stable within a
    /// ChannelId and otherwise unique, so all placeholder notes on one channel share
    /// exactly one track and never leak per-instrument tracks.</summary>
    private static MidiTrackKey PlaceholderKey(string channelId) =>
        new(new DeviceId(ChipType.Unknown, StableIndex(channelId)), VoiceKind.Pcm,
            StableIndex(channelId), InstrumentIdentity.Empty);

    private static int StableIndex(string channelId)
    {
        uint h = 2166136261;
        foreach (char c in channelId)
            h = (h ^ c) * 16777619;
        return (int)(h & 0x7FFFFFFF);
    }

    private sealed class TrackAllocator
    {
        public Dictionary<MidiTrackKey, TrackSlot> _slots = new();
        public Dictionary<int, MidiTrack> Tracks { get; } = new();
        private readonly HashSet<MidiEndpoint> _usedEndpoints = new();
        private readonly Dictionary<MidiChannelDomain, int> _requestedChannelsByDomain = new();

        public void Add(MidiTrackKey key, int index, string channelId, VoiceExportOverride voiceOverride,
            MusicalMidiExportOptions options, Func<MidiEventBase, MidiEventBase> withOrder)
        {
            TrackSlot? existing = SlotFor(key);
            if (existing is not null)
                return; // key already allocated (defensive; BuildTracks dedupes).
            bool percussive = key.Instrument.Family == IdentityFamily.Rhythm;
            string name = IdentityNameFor(key, percussive, channelId);
            MidiEndpoint endpoint = ResolveEndpoint(key, percussive, options, voiceOverride);
            var track = new MidiTrack { Name = name, Endpoint = endpoint };
            Tracks[index] = track;
            int channel = endpoint.Channel;
            MidiChannelDomain domain = new(key.Device, key.VoiceFamily, key.SourceChannel);
            _slots[key] = new TrackSlot(this, track, index, channel, percussive, domain)
            {
                Override = voiceOverride,
            };
            if (!percussive)
            {
                // One STABLE instrument per track: a single program is assigned at
                // track creation and never changed mid-track (R7). This initial
                // setup is also the captured "silent-period state" — no silence-only
                // track is created (R6).
                if (voiceOverride.Bank is int bank)
                    track.Events.Add(withOrder(new MidiBankEvent(0, index, channel, bank)));
                if (voiceOverride.Program is int program)
                    track.Events.Add(withOrder(new MidiProgramEvent(0, index, channel, program)));
            }
        }

        private MidiEndpoint ResolveEndpoint(MidiTrackKey key, bool percussive,
            MusicalMidiExportOptions options, VoiceExportOverride voiceOverride)
        {
            MidiChannelDomain domain = new(key.Device, key.VoiceFamily, key.SourceChannel);
            if (voiceOverride.Channel is int oc)
            {
                if (oc is < 0 or > 15) throw new InvalidOperationException(
                    $"Requested MIDI channel {oc} for source domain '{key}' is outside [0, 15].");
                if (percussive && oc != 9)
                    throw new InvalidOperationException(
                        $"Percussion source domain '{key}' must use MIDI channel 9.");
                if (!percussive && oc == 9)
                    throw new InvalidOperationException(
                        $"Melodic source domain '{key}' cannot use reserved MIDI channel 9.");
                if (_requestedChannelsByDomain.TryGetValue(domain, out int existing) && existing != oc)
                    throw new InvalidOperationException(
                        $"Source domain '{domain}' requests incompatible MIDI channels {existing} and {oc}.");
                _requestedChannelsByDomain[domain] = oc;
                return AllocateEndpoint(oc, percussive, key);
            }
            if (percussive)
                return AllocateEndpoint(9, percussive, key);
            return AllocateEndpoint(null, false, key);
        }

        private MidiEndpoint AllocateEndpoint(int? requestedChannel, bool percussive, MidiTrackKey key)
        {
            IEnumerable<int> channels = percussive
                ? new[] { 9 }
                : Enumerable.Range(0, 16).Where(channel => channel != 9);
            if (requestedChannel is int requested)
                channels = new[] { requested };
            for (int port = 0; port <= byte.MaxValue; port++)
            {
                foreach (int channel in channels)
                {
                    var endpoint = new MidiEndpoint((byte)port, channel);
                    if (_usedEndpoints.Add(endpoint))
                        return endpoint;
                }
            }
            throw new InvalidOperationException(
                $"MIDI port exhaustion: source track '{key}' cannot be assigned a unique endpoint (maximum port is 255).");
        }

        private static string IdentityNameFor(MidiTrackKey key, bool percussive, string channelId)
        {
            // Placeholder / unresolved instruments keep the per-channel name.
            if (key.Instrument.IsEmpty)
                return channelId;
            string instrument = percussive ? key.Instrument.Canonical : key.Instrument.DisplayName;
            // Deterministic, source-channel-aware prefix: "<CHIP> CH<n>" where n is
            // the 1-based source channel. Distinct (chip, channel, instrument) keys
            // therefore always get distinct names.
            string chip = key.Chip == ChipType.Unknown ? "CH" : $"{ChipPrefix(key.Chip)} CH";
            return $"{chip}{key.SourceChannel + 1} - {instrument}";
        }

        private static string ChipPrefix(ChipType chip) => chip switch
        {
            ChipType.Ym2203 => "YM2203",
            ChipType.Ym2608 => "YM2608",
            ChipType.Ym2610 => "YM2610",
            ChipType.Ym2612 => "YM2612",
            ChipType.Ym2151 => "YM2151",
            ChipType.Ym2413 => "YM2413",
            ChipType.Ym3526 => "YM3526",
            ChipType.Ym3812 => "YM3812",
            ChipType.Y8950 => "Y8950",
            ChipType.Ymf262 => "YMF262",
            ChipType.Ymf278b => "YMF278B",
            ChipType.Ymz280b => "YMZ280B",
            _ => chip.ToString().ToUpperInvariant(),
        };

        public TrackSlot? SlotFor(MidiTrackKey key) =>
            _slots.TryGetValue(key, out TrackSlot? slot) ? slot : null;

        public IReadOnlyDictionary<MidiTrackKey, TrackSlot> Slots => _slots;

        public IEnumerable<MidiTrack> Values => Tracks.Values;
    }

    private readonly record struct MidiChannelDomain(
        DeviceId Device,
        VoiceKind VoiceFamily,
        int SourceChannel);

    private sealed class TrackSlot
    {
        private readonly TrackAllocator _owner;

        public TrackSlot(TrackAllocator owner, MidiTrack track, int index, int channel, bool percussive,
            MidiChannelDomain domain)
        {
            _owner = owner;
            Track = track;
            Index = index;
            Channel = channel;
            Percussive = percussive;
            Domain = domain;
        }

        public MidiTrack Track { get; }
        public int Index { get; }
        public int Channel { get; }
        public MidiChannelDomain Domain { get; }
        private bool Percussive { get; }

        /// <summary>Per-voice override applied to this slot (default: include + program 0).</summary>
        public VoiceExportOverride Override { get; set; } = null!;

        public int PitchFor(NoteEvent note)
        {
            if (Percussive)
                return (int)Math.Clamp(Math.Round(note.InitialMidiNote), 0, 127);
            if (!double.IsFinite(note.InitialMidiNote))
                return 60;
            return (int)Math.Clamp((int)Math.Round(note.InitialMidiNote), 0, 127);
        }
    }
}
