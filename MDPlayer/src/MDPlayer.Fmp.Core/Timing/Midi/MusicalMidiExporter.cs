#nullable enable

using System.Diagnostics;
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

    /// <summary>Floor semitones of the pitch-bend range (RPN), default 24, CLI
    /// --bend-range. The configured value is a FLOOR: each melodic domain
    /// auto-expands its emitted range to cover the pitch excursions its notes
    /// actually need (boundary excursions beyond MIDI 0..127 included), capped
    /// at 127. An offset beyond the effective range triggers a tick-domain
    /// re-anchor, and an offset that cannot be represented fails loudly.</summary>
    public int BendRangeSemitones { get; init; } = 24;

    /// <summary>Pitch-normalization mode (D11): Fidelity (default) subtracts the
    /// accepted per-domain tuning bias and restores it via RPN channel tuning at
    /// tick 0 — bends carry only expressive deviation; DawFriendly snaps small
    /// biases to equal temperament and emits no tuning; Off reproduces the legacy
    /// byte stream exactly.</summary>
    public PitchNormalizationMode PitchNormalizationMode { get; init; } = PitchNormalizationMode.Fidelity;

    /// <summary>Configurable pitch-normalization thresholds (FR-6, instrumentation
    /// first): the conservative defaults are calibration placeholders meant to be
    /// replaced from the first --pitch-report corpus runs. Null = defaults.</summary>
    public PitchNormalizationThresholds? PitchNormalizationThresholds { get; init; }

    public bool EmitInstrumentMetadata { get; init; } = true;

    /// <summary>Emit loop/section markers on the conductor track.</summary>
    public bool EmitMarkers { get; init; } = true;

    /// <summary>Emit conductor track name / source metadata / timing-confidence text.</summary>
    public bool EmitConductorMetadata { get; init; } = true;

    /// <summary>Default note velocity (1–127) when a voice override does not set one.</summary>
    public int Velocity { get; init; } = 90;

    /// <summary>Per-voice transforms keyed by <c>ChannelId</c>. A missing entry keeps defaults.</summary>
    public IReadOnlyList<VoiceExportOverride> VoiceOverrides { get; init; } = Array.Empty<VoiceExportOverride>();

    /// <summary>Enables the opt-in Phase 2 work receipt.</summary>
    public bool EnablePerformanceMetrics { get; init; }

    /// <summary>Optional symbolic-tempo counters collected by the application
    /// boundary around map construction.</summary>
    public TempoInferenceCounters TempoInferenceCounters { get; init; }

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
    /// Per-domain pitch-normalization statistics (FR-6 / D12): attacks, raw pitch
    /// samples, residual mode, stable-residual MAD, baseline confidence, pipeline
    /// transition counts, accepted tuning and warnings. Surfaced by --pitch-report.
    /// </summary>
    public required PitchNormalizationDiagnostics PitchDiagnostics { get; init; }

    /// <summary>
    /// The global minimal integer origin shift applied to every time-domain event
    /// (notes, bends, rhythm, markers, later tempo events) so no exported tick is
    /// negative (Patch C §21). Setup events — the first Set Tempo, Time Signature,
    /// RPN, program and bank — stay at their logical tick 0 and are NOT shifted.
    /// </summary>
    public long OriginShiftTicks { get; init; }

    /// <summary>Nested stage timings and operation counts when requested.</summary>
    public MidiPerformanceSnapshot? Performance { get; set; }
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
    private MidiPerformanceMetrics? _performance;
    private long _allocatedBefore;

    /// <summary>Deterministic monotonic source-sequence key assigned to every exported
    /// event (conductor, program, note, bend, …) so the writer's equal-tick/equal-rank
    /// sort can be fully deterministic via <see cref="MidiEventBase.SourceOrder"/>.</summary>
    private int _sourceOrder;

    /// <summary>Number of source voices that fell back to a placeholder track (Patch
    /// E.3). Recorded so the timing report surfaces genuine unknowns rather than
    /// silently collapsing them. Counts DISTINCT unresolved channelIds (TrackKeyFor
    /// runs in two passes, so per-call counting would over-report).</summary>
    private int _placeholderCount;

    private readonly HashSet<string> _placeholderChannels = new();
    private Dictionary<NoteEvent, MidiTrackKey> _noteTrackKeyCache =
        new(ReferenceEqualityComparer.Instance);
    private Dictionary<RhythmEvent, MidiTrackKey> _rhythmTrackKeyCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<(long Position, double Target, int Order)> _pitchAnchorScratch = new();
    private readonly List<PlannedPitchState> _pitchStateScratch = new();
    private readonly List<(long Tick, int OldBase, int NewBase, int InitialBend)> _reanchorScratch = new();

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

    /// <summary>
    /// Optional structural analysis (bar labels / sections / fundamental loop)
    /// from <see cref="MusicalStructureAnalyzer.Analyze"/>. When set and
    /// <see cref="MusicalMidiExportOptions.EmitMarkers"/> is enabled, the conductor
    /// carries SECTION_* markers at section boundaries and STRUCT_LOOP_* markers at
    /// the fundamental loop bounds. Computed upstream so the exporter only encodes.
    /// </summary>
    public MusicalStructure? Structure { get; set; }

    public MusicalMidiExportResult Export(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return Export(SourceTimeline.Create(timeline));
    }

    internal MusicalMidiExportResult Export(SourceTimeline sourceTimeline)
    {
        ArgumentNullException.ThrowIfNull(sourceTimeline);
        VisualizationTimeline timeline = sourceTimeline.Timeline;

        _noteTrackKeyCache = new Dictionary<NoteEvent, MidiTrackKey>(
            timeline.Notes?.Count ?? 0, ReferenceEqualityComparer.Instance);
        _rhythmTrackKeyCache = new Dictionary<RhythmEvent, MidiTrackKey>(
            timeline.Rhythm?.Count ?? 0, ReferenceEqualityComparer.Instance);
        _performance = _options.EnablePerformanceMetrics ? new MidiPerformanceMetrics() : null;
        _performance?.SetTempoInferenceCounters(_options.TempoInferenceCounters);
        _allocatedBefore = _performance is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
        if (_performance is not null)
        {
            _performance.SourceEvents = (timeline.Notes?.Count ?? 0)
                + (timeline.Rhythm?.Count ?? 0)
                + (timeline.Timing?.Length ?? 0)
                + (timeline.Beats?.Length ?? 0);
        }

        // Pitch-normalization stage (FR-1): runs BEFORE the origin shift so the
        // shift covers the NORMALIZED event set (the set the exporter actually
        // serializes). Every pitch consumer reads the normalized model. Detector
        // rejections surface as Diagnostics warnings (low-confidence domains).
        long stageStart = _performance?.StartStage(MidiPerformanceStage.Normalize) ?? 0;
        var pitchWarnings = new List<string>();
        PitchNormalizationModel pitchModel = PitchNormalizationStage.Normalize(
            timeline,
            _options.PitchNormalizationMode,
            _options.PitchNormalizationThresholds ?? PitchNormalizationThresholds.Default,
            TrackKeyFor,
            pitchWarnings);
        if (pitchWarnings.Count > 0 && Diagnostics is not null)
            Diagnostics.Warnings.AddRange(pitchWarnings);
        _performance?.StopStage(MidiPerformanceStage.Normalize, stageStart);

        stageStart = _performance?.StartStage(MidiPerformanceStage.DomainAnalysis) ?? 0;
        SourceEventIndex sourceIndex = BuildSourceEventIndex(timeline, pitchModel);
        long originShiftTicks = ComputeOriginShiftTicks(sourceIndex, timeline);
        var conductor = new List<MidiEventBase>();
        BuildConductor(timeline, originShiftTicks, conductor);

        PreparePitchScratch(sourceIndex);
        TrackAllocator allocator = BuildTracks(sourceIndex);
        // Per-domain bend-range resolution runs BEFORE note planning: EmitNote
        // encodes every bend against the slot's effective range, and the RPN setup
        // emits the same value (Patch B + per-domain auto-expansion).
        ComputeEffectiveBendRanges(sourceIndex, pitchModel, allocator);
        _performance?.StopStage(MidiPerformanceStage.DomainAnalysis, stageStart);

        // Unpitched noise is excluded from the melodic export (no pitch exists);
        // surface it as a diagnostic so the exclusion is never silent.
        int unpitchedNoise = (timeline.Notes ?? Array.Empty<NoteEvent>()).Count(IsUnpitchedNoise);
        if (unpitchedNoise > 0 && Diagnostics is not null)
            Diagnostics.Warnings.Add(
                $"{unpitchedNoise} unpitched noise note(s) excluded from the melodic MIDI export " +
                "(SSG noise has no pitch to serialize)");

        stageStart = _performance?.StartStage(MidiPerformanceStage.EventGeneration) ?? 0;
        var emittedNoteTicks = new HashSet<(MidiTrackKey Key, long Tick)>(sourceIndex.Notes.Count);
        var plannable = new List<PlannableNote>(sourceIndex.Notes.Count);
        foreach (IndexedNote indexedNote in sourceIndex.Notes)
        {
            NoteEvent note = indexedNote.Note;
            MidiTrackKey key = indexedNote.Key;
            TrackSlot? slot = allocator.SlotFor(key);
            if (slot is null)
                continue;
            plannable.Add(new PlannableNote(slot, note, key));
        }
        plannable.Sort(static (a, b) =>
        {
            int c = a.Slot.Index.CompareTo(b.Slot.Index);
            if (c != 0) return c;
            c = a.Note.StartSample.CompareTo(b.Note.StartSample);
            if (c != 0) return c;
            c = b.Note.EndSample.CompareTo(a.Note.EndSample);
            return c != 0 ? c : a.Note.InitialMidiNote.CompareTo(b.Note.InitialMidiNote);
        });
        int plannableWrite = 0;
        for (int index = 0; index < plannable.Count; index++)
        {
            PlannableNote candidate = plannable[index];
            MidiTrackKey key = candidate.Key;
            if (emittedNoteTicks.Add((key, TimeTick(candidate.Note.StartSample)))
                && plannableWrite < plannable.Count)
            {
                plannable[plannableWrite++] = candidate;
            }
            else if (_performance is not null)
            {
                _performance.SuppressedMidiEvents++;
            }
        }
        if (plannableWrite < plannable.Count)
            plannable.RemoveRange(plannableWrite, plannable.Count - plannableWrite);
        if (_performance is not null)
        {
            _performance.TimelineSorts++;
            _performance.TemporaryCollections++;
        }

        // Pitch/note planning happens once per note, in ENDPOINT order, so the
        // per-endpoint pitch-bend state (lastEncodedBend) is consistent and bend
        // resets are decided deterministically. This also steps through the MIDI
        // tick domain AFTER same-tick pitch collapse so re-anchors never fabricate
        // synthetic 1-tick internal notes.
        Dictionary<MidiEndpoint, int> lastBendByEndpoint =
            new(allocator.Tracks.Count);
        foreach (PlannableNote pn in plannable)
        {
            EmitNote(pn.Slot, pn.Note, pitchModel, originShiftTicks, lastBendByEndpoint);
        }
        _performance?.StopStage(MidiPerformanceStage.EventGeneration, stageStart);

        // Rhythm voices → percussion pitches (Batch 4 drum allocation). Each rhythm
        // track is keyed by its instrument identity (R9), and the percussion pitch is
        // allocated per identity so distinct instruments get distinct drum notes.
        var drumNoteByIdentity = new Dictionary<MidiTrackKey, int>(sourceIndex.Rhythms.Count);
        int nextDrum = _options.PercussionNoteBase;
        foreach (IndexedRhythm indexedRhythm in sourceIndex.Rhythms)
        {
            RhythmEvent rhythm = indexedRhythm.Rhythm;
            MidiTrackKey rhythmKey = indexedRhythm.Key;
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
            long on = TimeTick(rhythm.SamplePosition) + originShiftTicks;
            AddTrackEvent(slot.Track, PackedMidiEvent.Note(on, slot.Index, slot.Channel, note, 100, noteOn: true));
            AddTrackEvent(slot.Track, PackedMidiEvent.Note(
                on + ShortHitTicks, slot.Index, slot.Channel, note, 100, noteOn: false));
        }

        // Fixed bounded pitch-bend-range RPN setup, emitted only on melodic tracks
        // that actually serialized a bend (Patch B): none on percussion or on tracks
        // whose notes required no bend infrastructure. Fidelity mode additionally
        // emits the per-domain tuning RPN (FR-5).
        EmitBendRangeSetup(allocator, pitchModel);

        ApplyQuantizationToGrid(allocator);

        // Endpoint uniqueness is enforced as a hard invariant across the exported
        // track set, in addition to the writer's own guard.
        var uniqueEndpoints = new HashSet<MidiEndpoint>();
        for (int index = 1; index <= allocator.Tracks.Count; index++)
        {
            MidiTrack track = allocator.Tracks[index];
            if (!uniqueEndpoints.Add(track.Endpoint))
                throw new InvalidOperationException(
                    $"Duplicate MIDI endpoint ({track.Endpoint.Port}, {track.Endpoint.Channel}) allocated to more than one track.");
        }

        // Placeholder diagnostics (FR-12 / request 33): a single summary warning so
        // the report surfaces genuinely unresolved identities — the per-placeholder
        // warnings above already name the channelId.
        if (_placeholderCount > 0 && Diagnostics is not null)
            Diagnostics.Warnings.Add($"placeholder-track-count={_placeholderCount}");

        stageStart = _performance?.StartStage(MidiPerformanceStage.TrackConstruction) ?? 0;
        var tracks = new List<MidiTrack>(allocator.Tracks.Count);
        for (int index = 1; index <= allocator.Tracks.Count; index++)
            tracks.Add(allocator.Tracks[index]);
        _performance?.StopStage(MidiPerformanceStage.TrackConstruction, stageStart);
        stageStart = _performance?.StartStage(MidiPerformanceStage.EventNormalization) ?? 0;
        foreach (MidiTrack track in tracks)
        {
            // Canonicalize once here; MidiFileWriter can stream planner-owned
            // tracks without materializing a second sorted copy.
            if (track.UsesPackedEvents)
            {
                track.PackedEvents.Sort(ComparePackedMidiEvents);
            }
            else
            {
                track.Events.Sort((a, b) =>
                {
                    int c = a.Tick.CompareTo(b.Tick);
                    if (c != 0) return c;
                    c = MidiEventOrder.Rank(a).CompareTo(MidiEventOrder.Rank(b));
                    return c != 0 ? c : a.SourceOrder.CompareTo(b.SourceOrder);
                });
            }
            track.HasCanonicalEventOrder = true;
            if (_performance is not null)
            {
                _performance.TimelineSorts++;
                _performance.TimelineScans++;
            }
        }

        // Endpoint-level pitch-bend canonicalization (Patch 1): after ALL musical
        // events are planned, collapse multiple bends on the same endpoint at the
        // same absolute tick to the single final effective bend, and suppress
        // consecutive identical bends with no bend-range change in between. This
        // closes the note-boundary handoff bug where an outgoing note's final bend
        // and the incoming note's initial bend both land on the boundary tick.
        // Removal only — never changes tick assignment and never re-links tracks.
        if (SkipEndpointCanonicalization)
        {
            // Test-only comparison path: emit the pre-canonicalization state so the
            // Smash Up acceptance check can quantify what the pass removes. The hard
            // invariant below intentionally does NOT run here (it would throw on
            // exactly the duplicates this path is meant to observe).
        }
        else
        {
            int before = tracks.Sum(TrackEventCount);
            CanonicalizeEndpointPitchState(tracks);
            if (_performance is not null)
            {
                int after = tracks.Sum(TrackEventCount);
                _performance.BendEventsSuppressed += Math.Max(0, before - after);
                _performance.TimelineSorts += CanonicalizeSortCount;
                _performance.TimelineScans += CanonicalizePassCount;
            }

            // Hard invariant: after canonicalization, per (endpoint, tick) there is
            // at most one PitchBend. Throws before any MIDI is serialized (FR-5).
            ValidateNoDuplicateEndpointTickBends(tracks);

            // Tuning/RPN invariants (D13b): per endpoint at most one MidiTuningEvent
            // AND at most one MidiBendRangeEvent — ONE domain → ONE active tuning
            // state is structural, never duplicated.
            ValidateTuningSetupCounts(tracks);
        }
        _performance?.StopStage(MidiPerformanceStage.EventNormalization, stageStart);

        stageStart = _performance?.StartStage(MidiPerformanceStage.SmfSerialization) ?? 0;
        var writer = new MidiFileWriter(_ppq);
        byte[] bytes = writer.Write(conductor, tracks);
        _performance?.StopStage(MidiPerformanceStage.SmfSerialization, stageStart);

        if (_performance is not null)
        {
            _performance.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocatedBefore;
            _performance.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
        }

        var pitchDiagnostics = new PitchNormalizationDiagnostics();
        pitchDiagnostics.Domains.AddRange(pitchModel.Domains.Values
            .OrderBy(d => d.Key.ToString(), StringComparer.Ordinal));
        pitchDiagnostics.Warnings.AddRange(pitchWarnings);
        return new MusicalMidiExportResult
        {
            Bytes = bytes,
            Tracks = tracks,
            Diagnostics = Diagnostics ?? NullDiagnostics,
            PitchDiagnostics = pitchDiagnostics,
            OriginShiftTicks = originShiftTicks,
            Performance = _performance?.Snapshot(),
        };
    }

    /// <summary>Hard invariant (D13b): an endpoint carries at most one tuning event
    /// and at most one bend-range event, so the tuning state on a channel is never
    /// ambiguous. Throws before any MIDI is serialized.</summary>
    private static void ValidateTuningSetupCounts(IReadOnlyList<MidiTrack> tracks)
    {
        var counts = new Dictionary<MidiEndpoint, (int Tuning, int Range)>();
        foreach (MidiTrack track in tracks)
        {
            (int Tuning, int Range) c = counts.GetValueOrDefault(track.Endpoint);
            if (track.UsesPackedEvents)
            {
                foreach (PackedMidiEvent evt in track.PackedEvents)
                {
                    c.Tuning += evt.Kind == PackedMidiEventKind.Tuning ? 1 : 0;
                    c.Range += evt.Kind == PackedMidiEventKind.BendRange ? 1 : 0;
                }
            }
            else
            {
                c.Tuning += track.Events.Count(e => e is MidiTuningEvent);
                c.Range += track.Events.Count(e => e is MidiBendRangeEvent);
            }
            counts[track.Endpoint] = c;
        }
        foreach ((MidiEndpoint endpoint, (int Tuning, int Range) c) in counts)
        {
            if (c.Tuning > 1 || c.Range > 1)
                throw new InvalidOperationException(
                    $"MIDI endpoint ({endpoint.Port}, {endpoint.Channel}) carries {c.Tuning} tuning and " +
                    $"{c.Range} bend-range RPN setups — at most one of each is allowed (D13b).");
        }
    }

    private static int ComparePackedMidiEvents(PackedMidiEvent left, PackedMidiEvent right)
    {
        int compare = left.Tick.CompareTo(right.Tick);
        if (compare != 0)
            return compare;
        compare = MidiEventOrder.Rank(left).CompareTo(MidiEventOrder.Rank(right));
        return compare != 0 ? compare : left.SourceOrder.CompareTo(right.SourceOrder);
    }

    private readonly TimingDiagnostics NullDiagnostics = new()
    {
        AnchorCount = 0,
        TempoSource = TimingSource.DriverBeatAnchors,
        PhaseSource = TimingSource.DriverBeatAnchors,
    };

    /// <summary>Test-only escape hatch used by the Smash Up acceptance check to
    /// compare pre/post-canonicalization output. Default false — the canonicalization
    /// pass always runs in production.</summary>
    internal bool SkipEndpointCanonicalization { get; set; }

    /// <summary>Test-observable counters for SC-19: the canonicalization pass must
    /// perform exactly ONE sort and ONE grouped linear pass per track. Reset at the
    /// start of <see cref="CanonicalizeEndpointPitchState"/>; instance-scoped so
    /// parallel tests never share state.</summary>
    internal int CanonicalizeSortCount;
    internal int CanonicalizePassCount;

    /// <summary>
    /// Endpoint-level pitch-bend canonicalization (Patch 1, FR-1..FR-4). Runs once
    /// after all musical events are planned and before validation/serialization.
    /// Groups events semantically by <see cref="MidiEndpoint"/> (the endpoint
    /// uniqueness invariant already guarantees one track per endpoint, so keying on
    /// track.Endpoint is equivalent) and:
    /// 1. Same-tick collapse (FR-2): per absolute tick, zero bends → nothing; one →
    ///    retain; N → keep ONLY the bend with the greatest SourceOrder (later source
    ///    state wins; on a tie the first in sorted order is kept — ties are
    ///    impossible in practice because SourceOrder is globally monotonic).
    /// 2. Consecutive-identical suppression (FR-4): a retained bend equal to the
    ///    previous retained bend on the endpoint is dropped iff no MidiBendRangeEvent
    ///    (RPN sensitivity change) occurred between them; the flag resets after each
    ///    retained bend. NoteOff/NoteOn never touch the flag — NoteOff does not reset
    ///    pitch bend.
    /// Cross-tick bends are NEVER merged (FR-3). The pass only REMOVES events;
    /// tick assignment is baked at event creation and unchanged (D4), and tracks
    /// are never re-linked. Deterministic by SourceOrder, never collection iteration
    /// order (FR-16). O(events log n): one sort + one grouped pass per track (FR-17).
    /// </summary>
    internal void CanonicalizeEndpointPitchState(IReadOnlyList<MidiTrack> tracks)
    {
        CanonicalizeSortCount = 0;
        CanonicalizePassCount = 0;
        foreach (MidiTrack track in tracks)
        {
            if (track.UsesPackedEvents)
            {
                CanonicalizePackedEndpointPitchState(track);
                continue;
            }

            // Export has already applied this exact ordering in the preceding
            // EventNormalization stage. Direct test/diagnostic callers may pass
            // unsorted tracks, so retain the defensive in-place sort for them.
            // This removes a complete duplicate sort and temporary list from the
            // production export path.
            if (!track.HasCanonicalEventOrder)
            {
                track.Events.Sort(static (a, b) =>
                {
                    int c = a.Tick.CompareTo(b.Tick);
                    if (c != 0) return c;
                    c = MidiEventOrder.Rank(a).CompareTo(MidiEventOrder.Rank(b));
                    return c != 0 ? c : a.SourceOrder.CompareTo(b.SourceOrder);
                });
                CanonicalizeSortCount++;
            }

            List<MidiEventBase> events = track.Events;
            int? lastEmittedBend = null;
            bool sensitivityDirty = false;

            // ONE grouped linear pass over consecutive same-tick runs.
            int i = 0;
            int write = 0;
            while (i < events.Count)
            {
                long tick = events[i].Tick;
                int runEnd = i;
                while (runEnd < events.Count && events[runEnd].Tick == tick)
                    runEnd++;

                // Same-tick run: bends are contiguous at Rank 3; keep the bend with
                // the greatest SourceOrder (first in sorted order on a tie).
                MidiPitchBendEvent? retained = null;
                bool bendRangeInRun = false;
                for (int j = i; j < runEnd; j++)
                {
                    switch (events[j])
                    {
                        case MidiBendRangeEvent:
                            bendRangeInRun = true;
                            break;
                        case MidiPitchBendEvent bend when retained is null || bend.SourceOrder > retained.SourceOrder:
                            retained = bend;
                            break;
                    }
                }
                // A bend-range event sorts before PitchBend within the tick, so one
                // sharing this tick counts as "occurred between" the previous
                // retained bend and this one — the sensitivity flag must be set
                // before the suppression decision.
                if (bendRangeInRun)
                    sensitivityDirty = true;

                if (retained is not null)
                {
                    if (!sensitivityDirty && lastEmittedBend is int last && retained.Bend == last)
                    {
                        // Consecutive-identical duplicate: dropped; the flag is NOT
                        // reset (no retained bend was emitted).
                        retained = null;
                    }
                    else
                    {
                        lastEmittedBend = retained.Bend;
                        sensitivityDirty = false;
                    }
                }

                for (int j = i; j < runEnd; j++)
                {
                    if (ReferenceEquals(events[j], retained) || events[j] is not MidiPitchBendEvent)
                        events[write++] = events[j];
                }
                i = runEnd;
            }
            CanonicalizePassCount++;

            // Compact the existing list in place (never re-link the track).
            if (write < events.Count)
                events.RemoveRange(write, events.Count - write);
        }
    }

    private void CanonicalizePackedEndpointPitchState(MidiTrack track)
    {
        List<PackedMidiEvent> events = track.PackedEvents;
        if (!track.HasCanonicalEventOrder)
        {
            events.Sort(ComparePackedMidiEvents);
            CanonicalizeSortCount++;
        }

        int? lastEmittedBend = null;
        bool sensitivityDirty = false;
        int write = 0;
        for (int i = 0; i < events.Count;)
        {
            long tick = events[i].Tick;
            int runEnd = i;
            while (runEnd < events.Count && events[runEnd].Tick == tick)
                runEnd++;

            int retainedIndex = -1;
            bool bendRangeInRun = false;
            for (int j = i; j < runEnd; j++)
            {
                PackedMidiEvent evt = events[j];
                if (evt.Kind == PackedMidiEventKind.BendRange)
                    bendRangeInRun = true;
                else if (evt.Kind == PackedMidiEventKind.PitchBend
                    && (retainedIndex < 0 || evt.SourceOrder > events[retainedIndex].SourceOrder))
                    retainedIndex = j;
            }
            if (bendRangeInRun)
                sensitivityDirty = true;

            if (retainedIndex >= 0)
            {
                PackedMidiEvent retained = events[retainedIndex];
                if (!sensitivityDirty && lastEmittedBend is int last && retained.A == last)
                {
                    retainedIndex = -1;
                }
                else
                {
                    lastEmittedBend = retained.A;
                    sensitivityDirty = false;
                }
            }

            for (int j = i; j < runEnd; j++)
            {
                PackedMidiEvent evt = events[j];
                if (j == retainedIndex || evt.Kind != PackedMidiEventKind.PitchBend)
                    events[write++] = evt;
            }
            i = runEnd;
        }
        CanonicalizePassCount++;
        if (write < events.Count)
            events.RemoveRange(write, events.Count - write);
    }

    /// <summary>
    /// Hard invariant (FR-5): after canonicalization, per (endpoint, tick) there is
    /// at most one <see cref="MidiPitchBendEvent"/>. Any violation throws
    /// <see cref="InvalidOperationException"/> naming the endpoint, tick, bend
    /// values, track name and SourceOrder values — before any MIDI is serialized.
    /// </summary>
    internal static void ValidateNoDuplicateEndpointTickBends(IReadOnlyList<MidiTrack> tracks)
    {
        foreach (MidiTrack track in tracks)
        {
            if (track.UsesPackedEvents)
            {
                long currentTick = long.MinValue;
                bool seenBend = false;
                for (int index = 0; index < track.PackedEvents.Count; index++)
                {
                    PackedMidiEvent evt = track.PackedEvents[index];
                    if (evt.Kind != PackedMidiEventKind.PitchBend)
                        continue;
                    if (evt.Tick == currentTick && seenBend)
                    {
                        throw new InvalidOperationException(
                            $"Duplicate pitch-bend events on endpoint ({track.Endpoint.Port}, {track.Endpoint.Channel}) " +
                            $"at tick {evt.Tick}: packed events remain after canonicalization.");
                    }
                    currentTick = evt.Tick;
                    seenBend = true;
                }
                continue;
            }

            foreach (IGrouping<long, MidiPitchBendEvent> group in track.Events
                         .OfType<MidiPitchBendEvent>()
                         .GroupBy(b => b.Tick))
            {
                MidiPitchBendEvent[] bends = group.ToArray();
                if (bends.Length <= 1)
                    continue;
                throw new InvalidOperationException(
                    $"Duplicate pitch-bend events on endpoint ({track.Endpoint.Port}, {track.Endpoint.Channel}) " +
                    $"at tick {group.Key}: bend values [{string.Join(", ", bends.Select(b => b.Bend))}], " +
                    $"track '{track.Name}', " +
                    $"SourceOrder values [{string.Join(", ", bends.Select(b => b.SourceOrder))}].");
            }
        }
    }

    private static int TrackEventCount(MidiTrack track) =>
        track.UsesPackedEvents ? track.PackedEvents.Count : track.Events.Count;

    /// <summary>The unshifted absolute MIDI tick of a source sample (may be negative).</summary>
    private long TimeTick(long sample) => _map.SampleToTick(sample, _ppq);

    private int ShortHitTicks => Math.Max(1, _ppq / 32);

    /// <summary>Single source of truth for whether a note is actually emitted (§21).
    /// Unpitched noise (SSG noise-only, the intentional -1 sentinel) is excluded:
    /// it has no pitch to serialize as a melodic MIDI note, and fabricating one
    /// would corrupt fidelity. Such notes are counted and surfaced as a diagnostic.
    /// </summary>
    private bool IsNoteEmitted(NoteEvent note) =>
        note is not null && note.EndSample > note.StartSample
        && !IsUnpitchedNoise(note)
        && _options.OverrideFor(note.ChannelId).Include;

    /// <summary>True for the decoder's intentional unpitched-noise notes (SSG
    /// noise-only modes). These carry the <c>-1</c> "Unpitched" sentinel and are
    /// rendered as fixed noise rows by the visualization; MIDI has no pitch to
    /// express them, so they are excluded from the melodic export.</summary>
    private static bool IsUnpitchedNoise(NoteEvent note) =>
        note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise;

    /// <summary>Single source of truth for whether a rhythm trigger is emitted (§21).</summary>
    private bool IsRhythmEmitted(RhythmEvent rhythm) =>
        rhythm is not null && _options.OverrideFor(rhythm.ChannelId).Include;

    /// <summary>Single source of truth for whether a note serializes a pitch-bend
    /// family (§21). Used by BOTH EmitNote (to decide bend emission) and
    /// ComputeOriginShiftTicks (to decide whether the note's pitch changes may
    /// contribute to the origin), so the two can never diverge. A note emits bends
    /// only when EmitPitchBend is set AND it has pitch changes OR a fractional /
    /// non-finite initial note OR an exactly-integer pitch outside [0, 127]
    /// (a boundary base note plus a bend residual is the only lossless encoding —
    /// a bare key would silently clamp the source pitch, violating the decoded
    /// pitch = source pitch invariant); any other note produces a single round-pitch
    /// note with no bend infrastructure. Operates on the NORMALIZED view (FR-1) — a
    /// note whose normalized pitch is exactly integer and whose changes were
    /// compressed away needs no bend infrastructure.</summary>
    private bool ShouldFoldPitch(NormalizedNoteView view)
    {
        if (!_options.EmitPitchBend)
            return false;
        if (view.Changes is { Count: > 0 })
            return true;
        return !double.IsFinite(view.InitialMidiNote)
            || Math.Abs(view.InitialMidiNote - Math.Round(view.InitialMidiNote)) > 1e-6
            || Math.Round(view.InitialMidiNote) is < 0 or > 127;
    }

    private void EmitNote(TrackSlot slot, NoteEvent note, PitchNormalizationModel model, long originShift,
        Dictionary<MidiEndpoint, int> lastBendByEndpoint)
    {
        VoiceExportOverride voiceOverride = _options.OverrideFor(note.ChannelId);
        int vel = Math.Clamp(voiceOverride.Velocity ?? _options.Velocity, 1, 127);
        int transpose = voiceOverride.TransposeSemitones;
        NormalizedNoteView view = model.Views[note];
        bool needsBend = ShouldFoldPitch(view);
        int bendRange = slot.EffectiveBendRange;
        // Validate the source pitch domain regardless of bend emission (§ B.3 /
        // cross-cutting fail-loudly): the source pitch must be FINITE and never an
        // unpitched-noise sentinel. Continuous source pitch may legitimately sit
        // outside MIDI 0..127 — the [0, 127] bound applies to the ENCODED key,
        // which ValidateNotePitch enforces at the point of emission.
        ValidateSourcePitch(note);

        if (!needsBend)
        {
            // Single round-pitch note, no bend infrastructure. The note-on must
            // still sound at its true (integer) pitch, so reset any residual bend
            // left by a prior note on this endpoint back to 0 (never assume NoteOff
            // restores 0 — Patch B.8).
            long on = TimeTick(note.StartSample) + originShift;
            long off = TimeTick(note.EndSample) + originShift;
            if (off <= on)
                off = on + 1;
            // The non-fold contract is "the note-on sounds at its TRUE (integer)
            // pitch". With bends disabled an out-of-range pitch (source or after
            // transpose) has no legal key to sound at — clamping would silently
            // lose pitch, so fail loudly instead (§ B.3 / cross-cutting fail-loudly).
            int roundedPitch = (int)Math.Round(view.InitialMidiNote) + transpose;
            if (roundedPitch is < 0 or > 127)
                throw new InvalidOperationException(
                    $"Source note '{note.ChannelId}' at sample {note.StartSample} needs MIDI pitch " +
                    $"{roundedPitch}, which no legal key in [0, 127] represents without pitch-bend " +
                    "infrastructure; enable pitch-bend emission (EmitPitchBend) to encode boundary excursions.");
            int pitch = roundedPitch;
            ValidateNotePitch(note, pitch);
            var noBendEndpoint = slot.Track.Endpoint;
            if (lastBendByEndpoint.TryGetValue(noBendEndpoint, out int lastBend) && lastBend != 0)
            {
                AddTrackEvent(slot.Track, PackedMidiEvent.PitchBend(on, slot.Index, slot.Channel, 0));
                lastBendByEndpoint[noBendEndpoint] = 0;
            }
            AddTrackEvent(slot.Track, PackedMidiEvent.Note(on, slot.Index, slot.Channel, pitch, vel, noteOn: true));
            AddTrackEvent(slot.Track, PackedMidiEvent.Note(off, slot.Index, slot.Channel, pitch, vel, noteOn: false));
            return;
        }

        // Build the pitch anchor list: folded-initial + causal changes, "final wins"
        // at the same sample, transposed exactly once, collapsed to same-tick.
        double initialSource = double.IsFinite(view.InitialMidiNote) ? view.InitialMidiNote : 60;
        long pitchStageStart = _performance?.StartStage(MidiPerformanceStage.PitchAnalysis) ?? 0;
        var anchors = BuildPitchAnchors(view, initialSource, transpose);
        _performance?.StopStage(MidiPerformanceStage.PitchAnalysis, pitchStageStart);

        long startTick = TimeTick(note.StartSample);
        long endTick = TimeTick(note.EndSample);
        if (note.EndSample > note.StartSample && endTick == startTick)
            endTick = startTick + 1; // real collapsed note: minimum 1 tick (§29).

        int baseNote = SelectBaseNote(anchors[0].Target, bendRange, note);
        List<PlannedPitchState> pitchStates = _pitchStateScratch;
        pitchStates.Clear();
        MidiEndpoint endpoint = slot.Track.Endpoint;

        // Re-anchor loop over the pitch states in tick order. Between the current
        // base and the next re-anchor, every state is encoded against the current
        // base. When a state exits baseNote +- range, re-anchor at that tick.
        long currentBaseFrom = startTick;
        int currentBase = baseNote;
        List<(long Tick, int OldBase, int NewBase, int InitialBend)> reanchors = _reanchorScratch;
        reanchors.Clear();
        for (int i = 1; i < anchors.Count; i++)
        {
            (long tick, double target, _) = anchors[i];
            if (tick < currentBaseFrom)
                continue; // defensive; anchors are sorted ascending
            double offset = target - currentBase;
            if (offset > bendRange || offset < -bendRange)
            {
                int newBase = SelectBaseNote(target, bendRange, note);
                int rebend = EncodeBend(target - newBase, bendRange, note);
                reanchors.Add((tick, currentBase, newBase, rebend));
                // The re-anchor's initial bend is the effective bend at that tick.
                pitchStates.Add(new PlannedPitchState(tick, target, rebend));
                currentBase = newBase;
                currentBaseFrom = tick;
                continue;
            }
            int bend = EncodeBend(offset, bendRange, note);
            // Dedup: skip a consecutive bend equal to the previous state's bend.
            if (pitchStates.Count > 0 && pitchStates[^1].EncodedBend == bend && pitchStates[^1].Tick < tick)
                continue;
            pitchStates.Add(new PlannedPitchState(tick, target, bend));
        }

        // Initial bend for the note-on (base note at the folded-initial target).
        // Always relative to the note's STARTING base (baseNote), never the current
        // re-anchored base — the note begins sounding on baseNote.
        int initialBend = EncodeBend(anchors[0].Target - baseNote, bendRange, note);
        // Replace any state sitting at the note-start tick with the initial bend
        // (a change at StartSample was already folded into the initial pitch).
        pitchStates.RemoveAll(s => s.Tick == startTick);

        // Per-endpoint bend reset: before the note-on, ensure the current bend
        // equals this note's required initial bend (never assume NoteOff restores 0).
        int beforeOn = lastBendByEndpoint.TryGetValue(endpoint, out int last) ? last : 0;
        if (beforeOn != initialBend)
            AddTrackEvent(slot.Track, PackedMidiEvent.PitchBend(
                startTick + originShift, slot.Index, slot.Channel, initialBend));
        lastBendByEndpoint[endpoint] = initialBend;

        AddTrackEvent(slot.Track, PackedMidiEvent.Note(
            startTick + originShift, slot.Index, slot.Channel, baseNote, vel, noteOn: true));

        // Topological emission: pitch states (incl. re-anchor initial bends) in
        // tick order; a re-anchor additionally notes-off the old base and notes-on
        // the new base at the re-anchor tick (order NoteOff, PitchBend, NoteOn via
        // the writer's rank).
        int reIdx = 0;
        // BuildPitchAnchors returns monotonic MIDI ticks, and this loop appends
        // states in anchor order. Re-sorting the states here was redundant work
        // on every bend-bearing note and could only preserve the existing order.
        foreach (PlannedPitchState state in pitchStates)
        {
            long t = state.Tick;
            if (reIdx < reanchors.Count && reanchors[reIdx].Tick <= t)
            {
                var re = reanchors[reIdx];
                if (re.Tick == t)
                {
                    AddTrackEvent(slot.Track, PackedMidiEvent.Note(
                        t + originShift, slot.Index, slot.Channel, re.OldBase, vel, noteOn: false));
                    AddTrackEvent(slot.Track, PackedMidiEvent.PitchBend(
                        t + originShift, slot.Index, slot.Channel, re.InitialBend));
                    AddTrackEvent(slot.Track, PackedMidiEvent.Note(
                        t + originShift, slot.Index, slot.Channel, re.NewBase, vel, noteOn: true));
                    lastBendByEndpoint[endpoint] = re.InitialBend;
                    reIdx++;
                    continue;
                }
            }
            AddTrackEvent(slot.Track, PackedMidiEvent.PitchBend(
                t + originShift, slot.Index, slot.Channel, state.EncodedBend));
            lastBendByEndpoint[endpoint] = state.EncodedBend;
        }

        // Final note-off on the current base.
        AddTrackEvent(slot.Track, PackedMidiEvent.Note(
            endTick + originShift, slot.Index, slot.Channel, currentBase, vel, noteOn: false));
    }

    private List<(long Position, double Target, int Order)> BuildPitchAnchors(
        NormalizedNoteView view, double initialSource, int transpose)
    {
        NoteEvent note = view.Source;
        ValidateSourcePitch(note);
        // Initial pitch first, then each causal finite change that occurs strictly
        // after the note start (at/after StartSample fold into the initial pitch)
        // and before/at the note end. Transpose applied exactly once. Changes come
        // from the NORMALIZED view (FR-1) — the same set the origin shift covered.
        List<(long Position, double Target, int Order)> pts = _pitchAnchorScratch;
        pts.Clear();
        pts.Add((note.StartSample, initialSource + transpose, -1));
        int seq = 0;
        bool pointsOrdered = true;
        long lastSample = note.StartSample;
        int lastOrder = -1;
        foreach (NormalizedPitchChange c in view.Changes ?? Array.Empty<NormalizedPitchChange>())
        {
            if (_performance is not null)
                _performance.PitchCalculations++;
            if (!double.IsFinite(c.MidiNote))
                continue;
            // Ignore changes before note start and at/after NoteOff.
            if (c.SamplePosition < note.StartSample || c.SamplePosition >= note.EndSample)
                continue;
            int order = seq++;
            if (c.SamplePosition < lastSample
                || c.SamplePosition == lastSample && order < lastOrder)
            {
                pointsOrdered = false;
            }
            lastSample = c.SamplePosition;
            lastOrder = order;
            pts.Add((c.SamplePosition, c.MidiNote + transpose, order));
        }
        // Same-sample collapse: final (highest source order) wins. Explicit
        // in-place sorting avoids the GroupBy/Select/OrderBy chain and its
        // iterator/group allocations on every pitch-bearing note.
        if (!pointsOrdered)
        {
            pts.Sort(static (a, b) =>
            {
                int c = a.Position.CompareTo(b.Position);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            if (_performance is not null)
                _performance.TimelineSorts++;
        }
        int write = 0;
        for (int read = 0; read < pts.Count;)
        {
            int end = read + 1;
            while (end < pts.Count && pts[end].Position == pts[read].Position)
                end++;
            pts[write++] = pts[end - 1];
            read = end;
        }
        if (write < pts.Count)
            pts.RemoveRange(write, pts.Count - write);

        // Convert the compacted sample positions to MIDI ticks in place. The
        // tuple layout is unchanged, so a second per-note anchor list would only
        // duplicate storage before the same-tick collapse.
        bool anchorsOrdered = true;
        long lastTick = long.MinValue;
        int lastTickOrder = int.MinValue;
        for (int index = 0; index < pts.Count; index++)
        {
            (long sample, double target, int order) = pts[index];
            long tick = TimeTick(sample);
            if (tick < lastTick || tick == lastTick && order < lastTickOrder)
                anchorsOrdered = false;
            lastTick = tick;
            lastTickOrder = order;
            pts[index] = (tick, target, order);
        }
        if (!anchorsOrdered)
        {
            pts.Sort(static (a, b) =>
            {
                int c = a.Position.CompareTo(b.Position);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            if (_performance is not null)
                _performance.TimelineSorts++;
        }
        write = 0;
        for (int read = 0; read < pts.Count;)
        {
            int end = read + 1;
            while (end < pts.Count && pts[end].Position == pts[read].Position)
                end++;
            pts[write++] = pts[end - 1];
            read = end;
        }
        if (write < pts.Count)
            pts.RemoveRange(write, pts.Count - write);
        return pts;
    }

    private int SelectBaseNote(double target, int range, NoteEvent note)
    {
        int lo = (int)Math.Ceiling(target - range);
        int hi = (int)Math.Floor(target + range);
        lo = Math.Max(0, lo);
        hi = Math.Min(127, hi);
        if (lo > hi)
            throw new InvalidOperationException(
                $"No legal MIDI base note represents target pitch {target:0.###} within bend range {range} for " +
                $"source note '{note.ChannelId}' at sample {note.StartSample}.");
        int rounded = (int)Math.Round(target, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, lo, hi);
    }

    /// <summary>Assigns the next deterministic source-sequence key and returns the event.</summary>
    private MidiEventBase WithSourceOrder(MidiEventBase evt)
    {
        evt.SourceOrder = _sourceOrder++;
        return evt;
    }

    private PackedMidiEvent WithPackedSourceOrder(PackedMidiEvent evt)
    {
        evt.SourceOrder = _sourceOrder++;
        return evt;
    }

    /// <summary>Appends an event to a track with a deterministic source-sequence key.</summary>
    private void AddTrackEvent(MidiTrack track, PackedMidiEvent evt)
    {
        evt.SourceOrder = _sourceOrder++;
        track.AddPacked(evt);
        if (_performance is not null)
        {
            _performance.GeneratedMidiEvents++;
            if (evt.Kind == PackedMidiEventKind.PitchBend)
            {
                _performance.BendEventsEmitted++;
                _performance.PitchCalculations++;
            }
        }
    }

    /// <summary>Sign-symmetric 14-bit bend for a semitone offset within the effective
    /// range: negative offsets map to [0,-8192), positive to [0,+8191]. Non-finite
    /// or out-of-range offsets fail loudly — no Math.Clamp to hide a planner bug.</summary>
    private static int EncodeBend(double offset, int range, NoteEvent note)
    {
        if (!double.IsFinite(offset))
            throw new InvalidOperationException(
                $"Non-finite pitch-bend offset for source note '{note.ChannelId}': {offset}.");
        if (offset < -range || offset > range)
            throw new InvalidOperationException(
                $"Pitch offset {offset:0.###} exceeds the effective bend range of {range} semitones for source note " +
                $"'{note.ChannelId}'; expected a tick-domain re-anchor to resolve this.");
        double scaled = offset < 0 ? offset / range * 8192.0 : offset / range * 8191.0;
        int bend = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (bend < -8192 || bend > 8191)
            throw new InvalidOperationException(
                $"Encoded bend {bend} is outside [-8192, 8191] for source note '{note.ChannelId}'.");
        return bend;
    }

    /// <summary>Sign-symmetric bend decode (inverse of <see cref="EncodeBend"/>): negative
    /// bend / 8192 * range, positive bend / 8191 * range.</summary>
    private static double DecodeBend(int bend, int range) =>
        bend < 0 ? bend / 8192.0 * range : bend / 8191.0 * range;

    private static void ValidateNotePitch(NoteEvent note, int midiNote)
    {
        if (midiNote < 0 || midiNote > 127)
            throw new InvalidOperationException(
                $"Source note '{note.ChannelId}' at sample {note.StartSample} maps to invalid MIDI note {midiNote}.");
    }

    private static void ValidateSourcePitch(NoteEvent note)
    {
        // Defensive gate: the intentional unpitched-noise sentinel never reaches the
        // melodic planner (IsNoteEmitted excludes it), but if one ever slips through
        // it must fail loudly — the -1 sentinel is finite and would otherwise pass
        // the finite-only check below with no pitch to serialize.
        if (IsUnpitchedNoise(note))
            throw new InvalidOperationException(
                $"Source note '{note.ChannelId}' at sample {note.StartSample} is unpitched noise " +
                "and has no MIDI pitch to serialize.");
        // Continuous source pitch may legitimately sit outside [0, 127] (e.g. an
        // AY8910 PSG note near the top of the range encodes its true pitch as a
        // boundary base note plus a pitch-bend excursion). Only FINITENESS is
        // required here; the [0, 127] bound applies to the ENCODED MIDI key, which
        // ValidateNotePitch enforces at the point of emission.
        if (!double.IsFinite(note.InitialMidiNote))
            throw new InvalidOperationException(
                $"Source note '{note.ChannelId}' at sample {note.StartSample} has invalid initial MIDI pitch " +
                $"'{note.InitialMidiNote}'. Expected a finite value.");
        if (note.Pitch is { Count: > 0 })
        {
            foreach (PitchChange change in note.Pitch)
            {
                if (!double.IsFinite(change.MidiNote))
                    throw new InvalidOperationException(
                        $"Source note '{note.ChannelId}' at sample {note.StartSample} has invalid pitch " +
                        $"'{change.MidiNote}' at sample {change.SamplePosition}. Expected a finite value.");
            }
        }
    }

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
        for (int index = 1; index <= allocator.Tracks.Count; index++)
        {
            MidiTrack track = allocator.Tracks[index];
            if (track.UsesPackedEvents)
            {
                for (int eventIndex = 0; eventIndex < track.PackedEvents.Count; eventIndex++)
                {
                    PackedMidiEvent evt = track.PackedEvents[eventIndex];
                    if (evt.Kind != PackedMidiEventKind.NoteOn)
                        continue;
                    // Quantize start to the nearest grid line; keep end duration.
                    long group = (evt.Tick + gridTicks / 2) / gridTicks * gridTicks;
                    evt.Tick = Math.Max(0, group);
                    track.PackedEvents[eventIndex] = evt;
                }
            }
            else
            {
                foreach (MidiEventBase evt in track.Events)
                {
                    if (evt is not MidiNoteEvent note || !note.NoteOn)
                        continue;
                    // Quantize start to the nearest grid line; keep end duration.
                    long group = (note.Tick + gridTicks / 2) / gridTicks * gridTicks;
                    note.Tick = Math.Max(0, group);
                }
            }
        }
    }

    /// <summary>
    /// Computes the minimal integer origin shift over every time-domain event that
    /// is actually emitted: notes, pitch bends, rhythm triggers, loop markers,
    /// SOURCE_START/FIRST_DOWNBEAT markers (when emitted), and later tempo events.
    /// Setup events (the first Set Tempo, Time Signature, RPN, program/bank) stay at
    /// their logical tick 0 and are not shifted. The shift is the smallest whole tick
    /// that makes every time-domain tick nonnegative — NOT aligned to quarter/bar.
    /// </summary>
    private long ComputeOriginShiftTicks(SourceEventIndex sourceIndex, VisualizationTimeline timeline)
    {
        long minTick = sourceIndex.MinimumTick;
        void Consider(long tick) { if (tick < minTick) minTick = tick; }
        if (_options.EmitMarkers)
        {
            foreach (LoopMarker loop in timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            {
                if (loop is null) continue;
                Consider(TimeTick(loop.SamplePosition));
            }
            Consider(TimeTick(timeline.StartSample)); // SOURCE_START
            if (_map.FirstDownbeatQuarter is double downbeat)
                Consider(_map.QuarterPositionToTick(downbeat, _ppq));
            if (Structure is MusicalStructure structure)
            {
                foreach (MusicalSection section in structure.Sections)
                    Consider(StructureBarTick(structure, section.StartBar));
                if (structure.PrimaryLoop is RepeatedBlock loop)
                {
                    Consider(StructureBarTick(structure, loop.StartBar));
                    Consider(StructureBarTick(structure, loop.StartBar + loop.LengthBars));
                }
            }
        }
        // Later tempo events at shifted segment start ticks (the first tempo is the
        // setup event at logical tick 0 so its segment start is not covered here).
        if (_map.Segments.Count > 1)
        {
            for (int i = 1; i < _map.Segments.Count; i++)
                Consider(TimeTick(_map.Segments[i].StartSample));
        }
        return Math.Max(0, -minTick);
    }

    /// <summary>Maps a structure bar index to an absolute pre-shift tick via the
    /// bar's quarter-start position. Section and loop markers share this mapping.</summary>
    private long StructureBarTick(MusicalStructure structure, int barIndex)
        => _map.QuarterPositionToTick(structure.Bars[barIndex].QuarterStart, _ppq);

    private void BuildConductor(VisualizationTimeline timeline, long originShift, List<MidiEventBase> conductor)
    {
        // Track name + source metadata text.
        if (_options.EmitConductorMetadata)
        {
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, timeline.Source?.Title ?? "MDPlayer Export")));
            if (!string.IsNullOrWhiteSpace(timeline.Source?.SourceFormat))
                conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, $"src-format {timeline.Source.SourceFormat}")));
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, $"sample-rate {timeline.SampleRate}")));
            // Build provenance (FR-15): attributes uploaded MIDIs to the exact
            // exporter binary. git-commit is omitted when the build had no
            // resolvable SHA — never "git-commit=unknown".
            AddBuildProvenance(conductor, WithSourceOrder, BuildMetadata.Version, BuildMetadata.GitCommit);
        }

        // First Set Tempo at tick 0 UNCONDITIONALLY (so no leading ticks run under an
        // implicit 120 BPM). Later tempo events sit at their shifted segment-start
        // tick; adjacent segments whose µs/qn match yield one event (§19).
        if (_map.Segments.Count == 0)
            throw new InvalidOperationException("Cannot build a conductor without any tempo segments.");
        conductor.Add(WithSourceOrder(new MidiTempoEvent(0, _map.Segments[0].MicrosecondsPerQuarter)));
        int? lastUsPerQuarter = _map.Segments[0].MicrosecondsPerQuarter;
        for (int i = 1; i < _map.Segments.Count; i++)
        {
            TempoSegment segment = _map.Segments[i];
            int us = segment.MicrosecondsPerQuarter;
            if (us == lastUsPerQuarter)
                continue;
            lastUsPerQuarter = us;
            conductor.Add(WithSourceOrder(new MidiTempoEvent(TimeTick(segment.StartSample) + originShift, us)));
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
            conductor.Add(WithSourceOrder(new MidiMarkerEvent(TimeTick(timeline.StartSample) + originShift, "SOURCE_START")));
            if (_map.FirstDownbeatQuarter is double downbeat)
            {
                long downbeatTick = _map.QuarterPositionToTick(downbeat, _ppq) + originShift;
                conductor.Add(WithSourceOrder(new MidiMarkerEvent(downbeatTick, "FIRST_DOWNBEAT")));
            }
            // Structural markers (sections + fundamental loop) from the upstream
            // MusicalStructureAnalyzer — distinct from the driver-parsed loop state
            // below. SECTION_* marks labeled section boundaries; STRUCT_LOOP_* marks
            // the fundamental repeated block detected from note content.
            if (Structure is MusicalStructure structure)
            {
                foreach (MusicalSection section in structure.Sections)
                {
                    conductor.Add(WithSourceOrder(new MidiMarkerEvent(
                        StructureBarTick(structure, section.StartBar) + originShift,
                        "SECTION_" + section.Label)));
                }
                if (structure.PrimaryLoop is RepeatedBlock loop)
                {
                    conductor.Add(WithSourceOrder(new MidiMarkerEvent(
                        StructureBarTick(structure, loop.StartBar) + originShift,
                        "STRUCT_LOOP_START")));
                    conductor.Add(WithSourceOrder(new MidiMarkerEvent(
                        StructureBarTick(structure, loop.StartBar + loop.LengthBars) + originShift,
                        "STRUCT_LOOP_END")));
                }
            }
            foreach (LoopMarker loop in timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            {
                long tick = TimeTick(loop.SamplePosition) + originShift;
                // A Restart boundary is simultaneously the end of the previous pass
                // and the start of the next: emit LOOP_END then LOOP_START at the
                // same tick, describing the structure actually in the MIDI.
                if (loop.Kind == LoopMarkerKind.Restart)
                    conductor.Add(WithSourceOrder(new MidiMarkerEvent(tick, "LOOP_START")));
                string name = loop.Kind switch
                {
                    LoopMarkerKind.Start => "LOOP_START",
                    LoopMarkerKind.Restart => "LOOP_END",
                    _ => "LOOP_MARK",
                };
                conductor.Add(WithSourceOrder(new MidiMarkerEvent(tick, name)));
            }
            // RENDER_END marks the true render end (the last sample of the captured
            // audio, loops + fade/tail included) — the final marker in the file.
            conductor.Add(WithSourceOrder(new MidiMarkerEvent(
                TimeTick(timeline.EndSample) + originShift, "RENDER_END")));
        }

        // Timing-confidence text so the DAW/user sees what was inferred.
        // Conductor metadata exposes the decoded physical topology. This is
        // intentionally derived from the timeline, never inferred from tracks.
        if (_options.EmitConductorMetadata)
        {
            string chips = string.Join(",",
                timeline.Devices
                    .Select(device => $"{device.Id.Type}:{device.Id.Instance}")
                    .OrderBy(value => value, StringComparer.Ordinal));
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(
                TimeTick(_map.FirstSample) + originShift,
                0x01,
                $"source-chips={chips}")));
        }

        // Timing-confidence text so the DAW/user sees what was inferred.
        if (_options.EmitConductorMetadata)
        {
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(
                TimeTick(_map.FirstSample) + originShift,
                0x01,
                TimingConfidenceText(Diagnostics))));
        }
    }

    private string TimingConfidenceText(TimingDiagnostics? diagnostics)
    {
        var parts = new List<string>
        {
            $"tempo-source={_map.Segments[0].Source}",
            $"segments={_map.Segments.Count}",
            _map.Meter is not null ? $"meter={_map.Meter}" : "meter=unknown",
            _map.FirstDownbeatQuarter is not null ? "downbeat=known" : "downbeat=unknown",
            $"sample0-quarter={_map.SampleToQuarterPosition(_map.StartSample):0.###}",
        };
        // FR-9: expose the alias/confidence diagnostics when symbolic inference was
        // actually used. Source of truth is TimingDiagnostics — no recompute here
        // (DRY). NullDiagnostics / driver path keeps only the structural fields.
        // Missing values serialize as "none"; no fabricated 1.0 (request 21).
        if (diagnostics is not null && diagnostics.TempoInferred)
        {
            parts.Add($"bpm={FormatDiagnosticDouble(diagnostics.SelectedBpm)}");
            parts.Add($"selected-bpm={FormatDiagnosticDouble(diagnostics.SelectedBpm)}");
            parts.Add($"tempo-us-per-quarter={diagnostics.TempoMicrosecondsPerQuarter?.ToString() ?? "none"}");
            parts.Add($"selected-score={FormatDiagnosticDouble(diagnostics.SelectedScore)}");
            parts.Add($"alternative-bpm={FormatDiagnosticDouble(diagnostics.AlternativeBpm)}");
            parts.Add($"alternative-score={FormatDiagnosticDouble(diagnostics.AlternativeScore)}");
            parts.Add($"alias-margin={FormatDiagnosticDouble(diagnostics.AliasMargin)}");
            parts.Add($"tempo-confidence={FormatDiagnosticDouble(diagnostics.TempoConfidence)}");
            parts.Add($"tempo-ambiguous={diagnostics.TempoAmbiguous.ToString().ToLowerInvariant()}");
            parts.Add($"phase-sample={diagnostics.PhaseSample?.ToString() ?? "none"}");
            parts.Add($"tatum={FormatDiagnosticDouble(diagnostics.TatumDurationSamples)}");
            parts.Add($"tatums-per-beat={diagnostics.TatumsPerBeat?.ToString() ?? "none"}");
            parts.Add($"beat={FormatDiagnosticDouble(diagnostics.BeatDurationSamples)}");
            parts.Add($"beat-phase={diagnostics.BeatPhaseSample?.ToString() ?? "none"}");
            parts.Add($"metrical-confidence={FormatDiagnosticDouble(diagnostics.MetricalConfidence)}");
            parts.Add($"downbeat-phase={diagnostics.DownbeatPhase?.ToString() ?? "unknown"}");
        }
        return "timing " + string.Join(";", parts);
    }

    /// <summary>Diagnostic doubles serialize as "0.###"; missing values as "none".</summary>
    private static string FormatDiagnosticDouble(double? value) =>
        value is double d ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "none";

    /// <summary>
    /// Build-provenance metadata events (FR-15): mdplayer-version is always
    /// emitted; git-commit only when a short SHA is available (never
    /// "git-commit=unknown"). Test-visible so the omission path is covered without
    /// re-running a build.
    /// </summary>
    internal static void AddBuildProvenance(List<MidiEventBase> conductor,
        Func<MidiEventBase, MidiEventBase> withOrder, string? version, string? gitCommit)
    {
        conductor.Add(withOrder(new MidiMetaTextEvent(0, 0x01, $"mdplayer-version {version}")));
        if (!string.IsNullOrWhiteSpace(gitCommit))
            conductor.Add(withOrder(new MidiMetaTextEvent(0, 0x01, $"git-commit {gitCommit}")));
    }

    private SourceEventIndex BuildSourceEventIndex(
        VisualizationTimeline timeline, PitchNormalizationModel model)
    {
        var notes = new List<IndexedNote>(timeline.Notes?.Count ?? 0);
        long minimumTick = TimeTick(_map.FirstSample);
        int maximumPitchChanges = 0;
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (_performance is not null)
                _performance.SourceEventsProcessed++;
            if (note is null || !IsNoteEmitted(note))
            {
                if (_performance is not null)
                    _performance.SourceEventsSkipped++;
                continue;
            }
            int eventCapacity = 2;
            int pitchChangeCount = 0;
            if (model.Views.TryGetValue(note, out NormalizedNoteView view) && ShouldFoldPitch(view))
            {
                pitchChangeCount = view.Changes?.Count ?? 0;
                maximumPitchChanges = Math.Max(maximumPitchChanges, pitchChangeCount);
                // A pitch change can remain a bend or become a three-event
                // re-anchor (NoteOff, bend, NoteOn). This is an upper bound,
                // computed before emission, so the packed track never grows in
                // the source-event loop.
                eventCapacity = 5 + 3 * pitchChangeCount;
            }
            notes.Add(new IndexedNote(note, TrackKeyFor(note), eventCapacity));
            minimumTick = Math.Min(minimumTick, TimeTick(note.StartSample));
            if (pitchChangeCount > 0)
            {
                foreach (NormalizedPitchChange change in view.Changes ?? Array.Empty<NormalizedPitchChange>())
                {
                    if (double.IsFinite(change.MidiNote))
                        minimumTick = Math.Min(minimumTick, TimeTick(change.SamplePosition));
                }
            }
        }

        var rhythms = new List<IndexedRhythm>(timeline.Rhythm?.Count ?? 0);
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (_performance is not null)
                _performance.SourceEventsProcessed++;
            if (rhythm is null || !IsRhythmEmitted(rhythm))
            {
                if (_performance is not null)
                    _performance.SourceEventsSkipped++;
                continue;
            }
            rhythms.Add(new IndexedRhythm(rhythm, RhythmKeyFor(rhythm)));
            minimumTick = Math.Min(minimumTick, TimeTick(rhythm.SamplePosition));
        }
        return new SourceEventIndex(notes, rhythms, minimumTick, maximumPitchChanges);
    }

    private void PreparePitchScratch(SourceEventIndex sourceIndex)
    {
        // BuildSourceEventIndex already inspected every normalized note while
        // calculating its emission capacity. Reuse that result instead of
        // walking the complete note index a second time merely to size scratch
        // buffers.
        int maximumChanges = sourceIndex.MaximumPitchChanges;
        _pitchAnchorScratch.EnsureCapacity(maximumChanges + 1);
        _pitchStateScratch.EnsureCapacity(maximumChanges);
        _reanchorScratch.EnsureCapacity(maximumChanges);
    }

    private TrackAllocator BuildTracks(SourceEventIndex sourceIndex)
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
        var eventCapacityByKey = new Dictionary<MidiTrackKey, int>();
        var seen = new HashSet<MidiTrackKey>();
        void AddKey(MidiTrackKey key, string channelId)
        {
            if (seen.Add(key))
            {
                keyOrder.Add(key);
                representativeByKey[key] = channelId;
            }
        }
        foreach (IndexedNote indexedNote in sourceIndex.Notes)
        {
            AddKey(indexedNote.Key, indexedNote.Note.ChannelId);
            eventCapacityByKey[indexedNote.Key] =
                eventCapacityByKey.GetValueOrDefault(indexedNote.Key) + indexedNote.EventCapacity;
        }
        foreach (IndexedRhythm indexedRhythm in sourceIndex.Rhythms)
        {
            AddKey(indexedRhythm.Key, indexedRhythm.Rhythm.ChannelId);
            eventCapacityByKey[indexedRhythm.Key] =
                eventCapacityByKey.GetValueOrDefault(indexedRhythm.Key) + 2;
        }
        keyOrder.Sort(CompareTrackKeys);

        ValidateChannelState(keyOrder, representativeByKey);

        // Percussion naming needs the set of distinct rhythm identities per chip:
        // a chip with exactly one rhythm voice gets the clean "<CHIP> Rhythm" name,
        // and multiple voices are disambiguated by their short voice name.
        var rhythmCountByChip = new Dictionary<ChipType, int>();
        foreach (MidiTrackKey key in keyOrder)
        {
            if (key.Instrument.Family != IdentityFamily.Rhythm)
                continue;
            rhythmCountByChip[key.Chip] = rhythmCountByChip.GetValueOrDefault(key.Chip) + 1;
        }

        foreach (MidiTrackKey key in keyOrder)
        {
            string channelId = representativeByKey[key];
            VoiceExportOverride voiceOverride = _options.OverrideFor(channelId);
            if (!voiceOverride.Include)
                continue; // excluded voice: no track, no notes.
            allocator.Add(key, index++, channelId, voiceOverride, _options, WithPackedSourceOrder,
                rhythmCountByChip.GetValueOrDefault(key.Chip),
                eventCapacityByKey.GetValueOrDefault(key) + 2);
        }
        return allocator;
    }

    /// <summary>
    /// Emits the pitch-bend-range RPN setup (Patch B) once per melodic track that
    /// actually emits bends, at logical tick 0. A track that emits no bends gets
    /// no RPN setup. The range is the slot's EFFECTIVE range: max(configured
    /// floor, per-domain required excursion), widened to the channel-wide maximum
    /// when melodic domains share a MIDI channel (RPN is per-channel), capped at
    /// 127 — see <see cref="ComputeEffectiveBendRanges"/>. Fidelity mode (FR-5)
    /// additionally emits one channel tuning RPN per TUNED melodic domain
    /// (accepted bias), before the bend-range setup (smaller SourceOrder; both
    /// setups are null-RPN-terminated so their order is semantics-independent).
    /// Tuning is emitted even when the domain's normalized notes are all integer
    /// (no bends): without it those notes would play at equal temperament instead
    /// of source pitch, violating FR-5's played-pitch = source-pitch contract (D9).
    /// </summary>
    private void EmitBendRangeSetup(TrackAllocator allocator, PitchNormalizationModel model)
    {
        if (_options.BendRangeSemitones is < 1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(_options), "Bend range must be in [1, 127].");
        var orderedSlots = new List<KeyValuePair<MidiTrackKey, TrackSlot>>(allocator.Slots.Count);
        foreach (KeyValuePair<MidiTrackKey, TrackSlot> pair in allocator.Slots)
            orderedSlots.Add(pair);
        orderedSlots.Sort(static (a, b) => a.Value.Index.CompareTo(b.Value.Index));

        for (int index = 0; index < orderedSlots.Count; index++)
        {
            MidiTrackKey key = orderedSlots[index].Key;
            TrackSlot slot = orderedSlots[index].Value;
            if (slot.Percussive)
                continue;
            // Only emit the RPN setup on tracks that actually serialized a bend; a
            // constant-pitch melodic track needs no bend infrastructure.
            bool hasBend = false;
            for (int eventIndex = 0; eventIndex < slot.Track.PackedEvents.Count; eventIndex++)
            {
                if (slot.Track.PackedEvents[eventIndex].Kind == PackedMidiEventKind.PitchBend)
                {
                    hasBend = true;
                    break;
                }
            }
            if (!hasBend)
                continue;
            AddTrackEvent(slot.Track, PackedMidiEvent.BendRange(0, slot.Index, slot.Channel, slot.EffectiveBendRange));
        }

        if (_options.PitchNormalizationMode != PitchNormalizationMode.Fidelity)
            return; // DAW-friendly snaps (no tuning events); Off emits legacy bytes.
        for (int index = 0; index < orderedSlots.Count; index++)
        {
            MidiTrackKey key = orderedSlots[index].Key;
            TrackSlot slot = orderedSlots[index].Value;
            if (slot.Percussive)
                continue;
            if (!model.Domains.TryGetValue(key, out DomainPitchStats? stats) || !stats.Accepted)
                continue;
            double bias = stats.TuningCents!.Value;
            // Base-100 IR decomposition (D10): coarse semitones + fine cents. The
            // split is FIXED at 100 because the RPN fine range is ±100c by spec —
            // it is not a configurable threshold. The detector's residual is folded
            // mod-100c (|TuningCents| ≤ 50c), so the coarse branch never engages
            // from the stage; it exists so the IR/writer support a multi-semitone
            // bias should the residual model ever change.
            int coarse = (int)Math.Truncate(bias / 100.0);
            int fine = (int)Math.Round(bias - coarse * 100.0);
            if (coarse == 0 && fine == 0)
                continue; // in-tune domain: bias is exactly 0, nothing to restore
            AddTrackEvent(slot.Track, PackedMidiEvent.Tuning(0, slot.Index, slot.Channel, coarse, fine));
        }
    }

    /// <summary>
    /// Resolves the effective pitch-bend range (RPN) every melodic slot encodes
    /// against, run once before note planning. Per source domain the REQUIRED range
    /// is the minimal whole-semitone range that keeps every effective pitch point of
    /// every note in the domain representable as (legal base note + bend); the
    /// emitted range is max(configured floor, ceil(required)), capped at 127. MIDI
    /// pitch-bend range is per-CHANNEL (RPN), so when two melodic domains share a
    /// channel number (forced voice-override channels, or port rollover beyond 16
    /// tracks) the channel-wide maximum is applied to every slot on the channel —
    /// the encoding in <see cref="EmitNote"/> and the RPN setup must agree or the
    /// decoded pitch drifts.
    /// </summary>
    private void ComputeEffectiveBendRanges(SourceEventIndex sourceIndex, PitchNormalizationModel model,
        TrackAllocator allocator)
    {
        int floor = _options.BendRangeSemitones;
        var requiredByDomain = new Dictionary<SourceDomainKey, double>();
        foreach (IndexedNote indexed in sourceIndex.Notes)
        {
            TrackSlot? slot = allocator.SlotFor(indexed.Key);
            if (slot is null || slot.Percussive)
                continue;
            double required = RequiredExcursion(indexed.Note, model, slot.Override.TransposeSemitones);
            SourceDomainKey domain = slot.Domain.Source;
            requiredByDomain[domain] = Math.Max(requiredByDomain.GetValueOrDefault(domain), required);
        }

        var effectiveByDomain = new Dictionary<SourceDomainKey, int>();
        foreach ((SourceDomainKey domain, double required) in requiredByDomain)
            effectiveByDomain[domain] = Math.Min(127, Math.Max(floor, (int)Math.Ceiling(required)));

        var channelMax = new Dictionary<int, int>();
        foreach (KeyValuePair<MidiTrackKey, TrackSlot> pair in allocator.Slots)
        {
            TrackSlot slot = pair.Value;
            if (slot.Percussive)
                continue;
            int effective = effectiveByDomain.GetValueOrDefault(slot.Domain.Source, floor);
            channelMax[slot.Channel] = Math.Max(channelMax.GetValueOrDefault(slot.Channel), effective);
        }
        foreach (KeyValuePair<MidiTrackKey, TrackSlot> pair in allocator.Slots)
        {
            TrackSlot slot = pair.Value;
            if (!slot.Percussive)
                slot.EffectiveBendRange = channelMax.GetValueOrDefault(slot.Channel, floor);
        }
    }

    /// <summary>Minimal bend-range excursion (semitones) ONE note needs to represent
    /// every point of its effective pitch trajectory. The trajectory mirrors
    /// <see cref="BuildPitchAnchors"/> exactly: folded initial pitch (non-finite
    /// falls back to 60) + every finite normalized change in [StartSample,
    /// EndSample), transpose applied once per point. The per-point excursion is
    /// abs(pitch - clamp(round(pitch), 0, 127)) — the distance to the legal base
    /// note the encoder actually picks: boundary-pinned at 0/127, exact-rounded
    /// inside. That base is range-independent (for any range that can represent the
    /// point, <see cref="SelectBaseNote"/> returns exactly it), which resolves the
    /// "base depends on range, range depends on base" circularity without
    /// iteration.</summary>
    private double RequiredExcursion(NoteEvent note, PitchNormalizationModel model, int transpose)
    {
        NormalizedNoteView view = model.Views[note];
        double initial = double.IsFinite(view.InitialMidiNote) ? view.InitialMidiNote : 60;
        double required = Excursion(initial + transpose);
        if (view.Changes is { Count: > 0 })
        {
            foreach (NormalizedPitchChange change in view.Changes)
            {
                if (!double.IsFinite(change.MidiNote))
                    continue;
                if (change.SamplePosition < note.StartSample || change.SamplePosition >= note.EndSample)
                    continue;
                required = Math.Max(required, Excursion(change.MidiNote + transpose));
            }
        }
        return required;
    }

    private static double Excursion(double pitch) =>
        Math.Abs(pitch - Math.Clamp((int)Math.Round(pitch, MidpointRounding.AwayFromZero), 0, 127));

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
        if (_noteTrackKeyCache.TryGetValue(note, out MidiTrackKey cached))
            return cached;

        MidiTrackKey key;
        if (note.Domain is SourceDomainKey domain
            && InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity typedInstrument))
            key = new MidiTrackKey(domain.Device, domain.VoiceFamily, domain.Index, typedInstrument);
        else if (TryParseSourceDomain(note.ChannelId, out DeviceId device, out VoiceKind voice, out int sourceChannel)
                 && InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity instrument))
            key = new MidiTrackKey(device, voice, sourceChannel, instrument);
        else
        {
            key = PlaceholderKey(note.ChannelId);
            _noteTrackKeyCache[note] = key;
            return key;
        }
        // SN76489 tone/noise is NOT an instrument (spec 30): PSG has no patch object
        // comparable to FM, so the track is keyed by source channel only (channels
        // are already split by VoiceKind.Psg index 0-2 / VoiceKind.Noise index 0).
        // TryParse still accepts sn76489:* so it never placeholders; the semantic
        // naming (SN76489 PSG CH1-3 / SN76489 Noise) carries the voice meaning.
        if (key.Device.Type == ChipType.Sn76489)
            key = key with { Instrument = InstrumentIdentity.Empty };
        _noteTrackKeyCache[note] = key;
        return key;
    }

    /// <summary>The MIDI track key owning a rhythm trigger, keyed by the rhythm
    /// instrument identity (R9). The exporter works from the identity — never from
    /// the raw voice-name string — so a future rename cannot change grouping.</summary>
    private MidiTrackKey RhythmKeyFor(RhythmEvent rhythm)
    {
        if (_rhythmTrackKeyCache.TryGetValue(rhythm, out MidiTrackKey cached))
            return cached;

        if (rhythm.Domain is SourceDomainKey domain)
        {
            string domainInstrument = string.IsNullOrWhiteSpace(rhythm.InstrumentId)
                ? $"rhythm:{rhythm.Voice.ToLowerInvariant()}" : rhythm.InstrumentId;
            if (!InstrumentIdentity.TryParse(domainInstrument, out InstrumentIdentity domainIdentity))
                domainIdentity = new InstrumentIdentity(IdentityFamily.Rhythm, 0, $"rhythm:{domainInstrument}");
            MidiTrackKey key = new(domain.Device, domain.VoiceFamily, domain.Index, domainIdentity);
            _rhythmTrackKeyCache[rhythm] = key;
            return key;
        }
        if (!TryParseSourceDomain(rhythm.ChannelId, out DeviceId device, out VoiceKind voice, out int sourceChannel))
        {
            MidiTrackKey key = PlaceholderKey(rhythm.ChannelId);
            _rhythmTrackKeyCache[rhythm] = key;
            return key;
        }
        string normalized = string.IsNullOrWhiteSpace(rhythm.InstrumentId)
            ? $"rhythm:{rhythm.Voice.ToLowerInvariant()}"
            : rhythm.InstrumentId;
        if (!InstrumentIdentity.TryParse(normalized, out InstrumentIdentity instrument))
            instrument = new InstrumentIdentity(IdentityFamily.Rhythm, 0, $"rhythm:{normalized}");
        MidiTrackKey result = new(device, voice, sourceChannel, instrument);
        _rhythmTrackKeyCache[rhythm] = result;
        return result;
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
        if (int.TryParse(suffix, out int n) && n >= 1)
        {
            sourceChannel = n - 1;
            return true;
        }
        // Patch E.2: a KNOWN voice family with a NON-NUMERIC (named) voice token
        // (e.g. ym2608.0.rhythm.top) is real source-voice identity — never a
        // placeholder. Derive a stable per-(device, kind, name) index from the token
        // so distinct named voices stay distinct tracks while remaining valid.
        if (voice is VoiceKind.Rhythm or VoiceKind.Pcm or VoiceKind.Adpcm or VoiceKind.Ssg
            or VoiceKind.Psg or VoiceKind.MidiChannel)
        {
            sourceChannel = StableIndex(suffix);
            return true;
        }
        return false;
    }

    /// <summary>Collapses notes whose instrument identity could not be resolved to a
    /// single per-source-channel placeholder track (R11). The key is stable within a
    /// ChannelId and otherwise unique, so all placeholder notes on one channel share
    /// exactly one track and never leak per-instrument tracks. Records the fallback
    /// in the exporter's placeholder diagnostics (Patch E.3).</summary>
    private MidiTrackKey PlaceholderKey(string channelId)
    {
        if (_placeholderChannels.Add(channelId))
        {
            _placeholderCount++;
            if (Diagnostics is not null)
                Diagnostics.Warnings.Add($"voice identity unresolved; collapsed to placeholder track: '{channelId}'");
        }
        return new(new DeviceId(ChipType.Unknown, StableIndex(channelId)), VoiceKind.Pcm,
            StableIndex(channelId), InstrumentIdentity.Empty);
    }

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
        private readonly Dictionary<SourceDomainKey, MidiVoiceDomain> _domainsBySource = new();
        private readonly HashSet<MidiEndpoint> _usedEndpoints = new();
        private readonly Dictionary<SourceDomainKey, int> _requestedChannelsByDomain = new();

        public void Add(MidiTrackKey key, int index, string channelId, VoiceExportOverride voiceOverride,
            MusicalMidiExportOptions options, Func<PackedMidiEvent, PackedMidiEvent> withOrder,
            int rhythmCountForChip = 0, int initialEventCapacity = 0)
        {
            TrackSlot? existing = SlotFor(key);
            if (existing is not null)
                return; // key already allocated (defensive; BuildTracks dedupes).
            bool percussive = key.Instrument.Family == IdentityFamily.Rhythm;
            string name = IdentityNameFor(key, percussive, channelId, rhythmCountForChip);
            MidiEndpoint endpoint = ResolveEndpoint(key, percussive, options, voiceOverride);
            var track = new MidiTrack(initialEventCapacity) { Name = name, Endpoint = endpoint };
            Tracks[index] = track;
            int channel = endpoint.Channel;
            MidiVoiceDomain domain = new(key.Device, key.VoiceFamily, key.SourceChannel,
                endpoint.Channel, voiceOverride.Program, voiceOverride.Bank,
                options.BendRangeSemitones, endpoint.Port);
            if (_domainsBySource.TryGetValue(domain.Source, out MidiVoiceDomain existingDomain))
                existingDomain.EnsureCompatible(domain);
            _domainsBySource[domain.Source] = domain;
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
                    track.AddPacked(withOrder(PackedMidiEvent.Bank(0, index, channel, bank)));
                if (voiceOverride.Program is int program)
                    track.AddPacked(withOrder(PackedMidiEvent.Program(0, index, channel, program)));
            }
        }

        private MidiEndpoint ResolveEndpoint(MidiTrackKey key, bool percussive,
            MusicalMidiExportOptions options, VoiceExportOverride voiceOverride)
        {
            SourceDomainKey domain = new(key.Device, key.VoiceFamily, key.SourceChannel);
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
            for (int port = 0; port <= byte.MaxValue; port++)
            {
                if (requestedChannel is int requested)
                {
                    var endpoint = new MidiEndpoint((byte)port, requested);
                    if (_usedEndpoints.Add(endpoint))
                        return endpoint;
                    continue;
                }

                if (percussive)
                {
                    var endpoint = new MidiEndpoint((byte)port, 9);
                    if (_usedEndpoints.Add(endpoint))
                        return endpoint;
                    continue;
                }

                for (int channel = 0; channel < 16; channel++)
                {
                    if (channel == 9)
                        continue;
                    var endpoint = new MidiEndpoint((byte)port, channel);
                    if (_usedEndpoints.Add(endpoint))
                        return endpoint;
                }
            }
            throw new InvalidOperationException(
                $"MIDI port exhaustion: source track '{key}' cannot be assigned a unique endpoint (maximum port is 255).");
        }

        private static string IdentityNameFor(MidiTrackKey key, bool percussive, string channelId, int rhythmCountForChip)
        {
            // Placeholder / unresolved instruments keep the per-channel name UNLESS
            // the source domain is known — then the semantic source-domain name is
            // used (FR-12: known domains never show a raw channelId unnecessarily).
            if (key.Instrument.IsEmpty)
                return SourceDomainDisplayName(key) ?? channelId;
            if (percussive)
            {
                // Semantic percussion name: "<CHIP> Rhythm" — the source "CH<n>"
                // prefix is meaningless for a rhythm voice, and the raw rhythm:xxx
                // canonical is noise. A chip carrying several distinct rhythm
                // identities is disambiguated by the short voice name (e.g. top).
                string chip = key.Chip == ChipType.Unknown ? "CH" : ChipPrefix(key.Chip);
                string baseName = $"{chip} Rhythm";
                return rhythmCountForChip > 1 ? $"{baseName} - {ShortRhythmName(key.Instrument)}" : baseName;
            }
            string instrument = key.Instrument.DisplayName;
            // Deterministic, source-channel-aware prefix: "<CHIP> CH<n>" where n is
            // the 1-based source channel. Distinct (chip, channel, instrument) keys
            // therefore always get distinct names.
            string chipName = key.Chip == ChipType.Unknown ? "CH" : $"{ChipPrefix(key.Chip)} CH";
            return $"{chipName}{key.SourceChannel + 1} - {instrument}";
        }

        /// <summary>
        /// Semantic display name for a source domain WITHOUT an instrument identity
        /// (FR-12 / request 31): "&lt;CHIP&gt; {word} CH{n}" or "&lt;CHIP&gt; Noise" —
        /// e.g. "SN76489 PSG CH2", "SNES DSP Voice 1", "OKIM6295 Voice 1",
        /// "YM2608 SSG CH1". Returns null for unknown/placeholder chips so the
        /// caller falls back to the raw channelId. Used ONLY when the instrument is
        /// Empty; with-instrument naming keeps the locked "&lt;CHIP&gt; CH&lt;n&gt; -
        /// &lt;DisplayName&gt;" composition (test-locked hyphen convention, D14).
        /// </summary>
        private static string SourceDomainDisplayName(MidiTrackKey key)
        {
            if (key.Chip == ChipType.Unknown)
                return null;
            string chip = ChipPrefix(key.Chip);
            if (key.VoiceFamily == VoiceKind.Noise)
                return $"{chip} Noise";
            string word = key.VoiceFamily switch
            {
                VoiceKind.Fm or VoiceKind.Fm3Operator => "FM",
                VoiceKind.Ssg => "SSG",
                VoiceKind.Psg => "PSG",
                VoiceKind.PcmVoice => $"Voice {key.SourceChannel + 1}",
                VoiceKind.Adpcm => $"Voice {key.SourceChannel + 1}",
                VoiceKind.MidiChannel => "CH",
                VoiceKind.Rhythm => "Rhythm",
                VoiceKind.Pcm => "PCM",
                _ => key.VoiceFamily.ToString(),
            };
            return $"{chip} {word} CH{key.SourceChannel + 1}";
        }

        /// <summary>Short voice name of a rhythm identity ("rhythm:top" → "top").</summary>
        private static string ShortRhythmName(InstrumentIdentity instrument)
        {
            string canonical = instrument.Canonical ?? string.Empty;
            return canonical.StartsWith("rhythm:", StringComparison.Ordinal)
                ? canonical["rhythm:".Length..]
                : canonical;
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
            ChipType.Sn76489 => "SN76489",
            ChipType.SnesDsp => "SNES DSP",
            _ => chip.ToString().ToUpperInvariant(),
        };

        public TrackSlot? SlotFor(MidiTrackKey key) =>
            _slots.TryGetValue(key, out TrackSlot? slot) ? slot : null;

        public IReadOnlyDictionary<MidiTrackKey, TrackSlot> Slots => _slots;

        public IEnumerable<MidiTrack> Values => Tracks.Values;
    }

    private sealed record SourceEventIndex(
        List<IndexedNote> Notes,
        List<IndexedRhythm> Rhythms,
        long MinimumTick,
        int MaximumPitchChanges);

    private readonly record struct IndexedNote(NoteEvent Note, MidiTrackKey Key, int EventCapacity);
    private readonly record struct IndexedRhythm(RhythmEvent Rhythm, MidiTrackKey Key);

    /// <summary>A single source note ready for pitch/note planning against its slot.</summary>
    private readonly record struct PlannableNote(TrackSlot Slot, NoteEvent Note, MidiTrackKey Key);

    /// <summary>A planned pitch event at an absolute MIDI tick: the target pitch and its
    /// encoded bend, resolved through tick-domain collapse and re-anchoring.</summary>
    private readonly record struct PlannedPitchState(long Tick, double Target, int EncodedBend);

    private sealed class TrackSlot
    {
        private readonly TrackAllocator _owner;

        public TrackSlot(TrackAllocator owner, MidiTrack track, int index, int channel, bool percussive,
            MidiVoiceDomain domain)
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
        public MidiVoiceDomain Domain { get; }
        public bool Percussive { get; }

        /// <summary>Effective pitch-bend range (RPN) this slot encodes against:
        /// max(configured floor, per-domain required excursion), widened to the
        /// channel-wide maximum when melodic domains share the channel. Resolved by
        /// <see cref="MusicalMidiExporter.ComputeEffectiveBendRanges"/> before note
        /// planning; 0 until then.</summary>
        public int EffectiveBendRange { get; set; }

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
