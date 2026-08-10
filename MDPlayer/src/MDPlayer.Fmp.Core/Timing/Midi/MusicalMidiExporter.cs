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

    /// <summary>Semitones of the configured fixed pitch-bend range (RPN), default 24.
    /// No auto-expansion: an offset outside this range triggers a tick-domain
    /// re-anchor, and an offset that cannot be represented fails loudly.</summary>
    public int BendRangeSemitones { get; init; } = 24;

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
    /// The global minimal integer origin shift applied to every time-domain event
    /// (notes, bends, rhythm, markers, later tempo events) so no exported tick is
    /// negative (Patch C §21). Setup events — the first Set Tempo, Time Signature,
    /// RPN, program and bank — stay at their logical tick 0 and are NOT shifted.
    /// </summary>
    public long OriginShiftTicks { get; init; }
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

    /// <summary>Number of source voices that fell back to a placeholder track (Patch
    /// E.3). Recorded so the timing report surfaces genuine unknowns rather than
    /// silently collapsing them. Counts DISTINCT unresolved channelIds (TrackKeyFor
    /// runs in two passes, so per-call counting would over-report).</summary>
    private int _placeholderCount;

    private readonly HashSet<string> _placeholderChannels = new();

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
        long originShiftTicks = ComputeOriginShiftTicks(timeline);

        var conductor = new List<MidiEventBase>();
        BuildConductor(timeline, originShiftTicks, conductor);

        TrackAllocator allocator = BuildTracks(timeline);

        // Unpitched noise is excluded from the melodic export (no pitch exists);
        // surface it as a diagnostic so the exclusion is never silent.
        int unpitchedNoise = (timeline.Notes ?? Array.Empty<NoteEvent>()).Count(IsUnpitchedNoise);
        if (unpitchedNoise > 0 && Diagnostics is not null)
            Diagnostics.Warnings.Add(
                $"{unpitchedNoise} unpitched noise note(s) excluded from the melodic MIDI export " +
                "(SSG noise has no pitch to serialize)");

        var emittedNoteTicks = new HashSet<(MidiTrackKey Key, long Tick)>();
        var plannable = new List<PlannableNote>();
        foreach (NoteEvent note in (timeline.Notes ?? Array.Empty<NoteEvent>())
                     .Where(IsNoteEmitted)
                     .OrderBy(n => TrackKeyFor(n), Comparer<MidiTrackKey>.Create(CompareTrackKeys))
                     .ThenBy(n => n.StartSample)
                     .ThenByDescending(n => n.EndSample)
                     .ThenBy(n => n.InitialMidiNote))
        {
            MidiTrackKey key = TrackKeyFor(note);
            TrackSlot? slot = allocator.SlotFor(key);
            if (slot is null)
                continue;
            if (!emittedNoteTicks.Add((key, TimeTick(note.StartSample))))
                continue;
            plannable.Add(new PlannableNote(slot, note));
        }

        // Pitch/note planning happens once per note, in ENDPOINT order, so the
        // per-endpoint pitch-bend state (lastEncodedBend) is consistent and bend
        // resets are decided deterministically. This also steps through the MIDI
        // tick domain AFTER same-tick pitch collapse so re-anchors never fabricate
        // synthetic 1-tick internal notes.
        Dictionary<MidiEndpoint, int> lastBendByEndpoint = new();
        foreach (PlannableNote pn in plannable.OrderBy(p => p.Slot.Index).ThenBy(p => p.Note.StartSample))
        {
            EmitNote(pn.Slot, pn.Note, originShiftTicks, lastBendByEndpoint);
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
            long on = TimeTick(rhythm.SamplePosition) + originShiftTicks;
            AddTrackEvent(slot.Track, new MidiNoteEvent(on, slot.Index, slot.Channel, note, 100, NoteOn: true));
            AddTrackEvent(slot.Track, new MidiNoteEvent(on + ShortHitTicks, slot.Index, slot.Channel, note, 100, NoteOn: false));
        }

        // Fixed bounded pitch-bend-range RPN setup, emitted only on melodic tracks
        // that actually serialized a bend (Patch B): none on percussion or on tracks
        // whose notes required no bend infrastructure.
        EmitBendRangeSetup(allocator);

        ApplyQuantizationToGrid(allocator);

        // Endpoint uniqueness is enforced as a hard invariant across the exported
        // track set, in addition to the writer's own guard.
        var uniqueEndpoints = new HashSet<MidiEndpoint>();
        foreach (MidiTrack track in allocator.Tracks.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            if (!uniqueEndpoints.Add(track.Endpoint))
                throw new InvalidOperationException(
                    $"Duplicate MIDI endpoint ({track.Endpoint.Port}, {track.Endpoint.Channel}) allocated to more than one track.");
        }

        // Placeholder diagnostics (FR-12 / request 33): a single summary warning so
        // the report surfaces genuinely unresolved identities — the per-placeholder
        // warnings above already name the channelId.
        if (_placeholderCount > 0 && Diagnostics is not null)
            Diagnostics.Warnings.Add($"placeholder-track-count={_placeholderCount}");

        var tracks = allocator.Tracks.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();

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
            CanonicalizeEndpointPitchState(tracks);

            // Hard invariant: after canonicalization, per (endpoint, tick) there is
            // at most one PitchBend. Throws before any MIDI is serialized (FR-5).
            ValidateNoDuplicateEndpointTickBends(tracks);
        }

        var writer = new MidiFileWriter(_ppq);
        byte[] bytes = writer.Write(conductor, tracks);
        return new MusicalMidiExportResult
        {
            Bytes = bytes,
            Tracks = tracks,
            Diagnostics = Diagnostics ?? NullDiagnostics,
            OriginShiftTicks = originShiftTicks,
        };
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
            // ONE sort — the exact (Tick, Rank, SourceOrder) key AppendEvents uses,
            // so the writer's later sort is a stable no-op and same-tick collapse is
            // deterministic by SourceOrder, never insertion/iteration order.
            List<MidiEventBase> sorted = track.Events
                .OrderBy(e => e.Tick)
                .ThenBy(MidiEventOrder.Rank)
                .ThenBy(e => e.SourceOrder)
                .ToList();
            CanonicalizeSortCount++;

            var rebuilt = new List<MidiEventBase>(sorted.Count);
            int? lastEmittedBend = null;
            bool sensitivityDirty = false;

            // ONE grouped linear pass over consecutive same-tick runs.
            int i = 0;
            while (i < sorted.Count)
            {
                long tick = sorted[i].Tick;
                int runEnd = i;
                while (runEnd < sorted.Count && sorted[runEnd].Tick == tick)
                    runEnd++;

                // Same-tick run: bends are contiguous at Rank 3; keep the bend with
                // the greatest SourceOrder (first in sorted order on a tie).
                MidiPitchBendEvent? retained = null;
                bool bendRangeInRun = false;
                for (int j = i; j < runEnd; j++)
                {
                    switch (sorted[j])
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
                    if (ReferenceEquals(sorted[j], retained) || sorted[j] is not MidiPitchBendEvent)
                        rebuilt.Add(sorted[j]);
                }
                i = runEnd;
            }
            CanonicalizePassCount++;

            // Rebuild the track's event list in place (never re-link the track).
            track.Events.Clear();
            track.Events.AddRange(rebuilt);
        }
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
    /// non-finite initial note; any other note produces a single round-pitch note
    /// with no bend infrastructure.</summary>
    private bool ShouldFoldPitch(NoteEvent note)
    {
        if (!_options.EmitPitchBend)
            return false;
        if (note.Pitch is { Count: > 0 })
            return true;
        return !double.IsFinite(note.InitialMidiNote)
            || Math.Abs(note.InitialMidiNote - Math.Round(note.InitialMidiNote)) > 1e-6;
    }

    private void EmitNote(TrackSlot slot, NoteEvent note, long originShift,
        Dictionary<MidiEndpoint, int> lastBendByEndpoint)
    {
        VoiceExportOverride voiceOverride = _options.OverrideFor(note.ChannelId);
        int vel = Math.Clamp(voiceOverride.Velocity ?? _options.Velocity, 1, 127);
        int transpose = voiceOverride.TransposeSemitones;
        bool needsBend = ShouldFoldPitch(note);
        int bendRange = _options.BendRangeSemitones;
        // Validate the source pitch domain regardless of bend emission (§ B.3 /
        // cross-cutting fail-loudly): a note whose true pitch is outside MIDI 0..127
        // is rejected even when it needs no bend infrastructure.
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
            int pitch = Math.Clamp((int)Math.Round(note.InitialMidiNote) + transpose, 0, 127);
            ValidateNotePitch(note, pitch);
            var noBendEndpoint = slot.Track.Endpoint;
            if (lastBendByEndpoint.TryGetValue(noBendEndpoint, out int lastBend) && lastBend != 0)
            {
                AddTrackEvent(slot.Track, new MidiPitchBendEvent(on, slot.Index, slot.Channel, 0));
                lastBendByEndpoint[noBendEndpoint] = 0;
            }
            AddTrackEvent(slot.Track, new MidiNoteEvent(on, slot.Index, slot.Channel, pitch, vel, NoteOn: true));
            AddTrackEvent(slot.Track, new MidiNoteEvent(off, slot.Index, slot.Channel, pitch, vel, NoteOn: false));
            return;
        }

        // Build the pitch anchor list: folded-initial + causal changes, "final wins"
        // at the same sample, transposed exactly once, collapsed to same-tick.
        double initialSource = double.IsFinite(note.InitialMidiNote) ? note.InitialMidiNote : 60;
        var anchors = BuildPitchAnchors(note, initialSource, transpose);

        long startTick = TimeTick(note.StartSample);
        long endTick = TimeTick(note.EndSample);
        if (note.EndSample > note.StartSample && endTick == startTick)
            endTick = startTick + 1; // real collapsed note: minimum 1 tick (§29).

        int baseNote = SelectBaseNote(anchors[0].Target, bendRange, note);
        var pitchStates = new List<PlannedPitchState>();
        MidiEndpoint endpoint = slot.Track.Endpoint;

        // Re-anchor loop over the pitch states in tick order. Between the current
        // base and the next re-anchor, every state is encoded against the current
        // base. When a state exits baseNote +- range, re-anchor at that tick.
        long currentBaseFrom = startTick;
        int currentBase = baseNote;
        var reanchors = new List<(long Tick, int OldBase, int NewBase, int InitialBend)>();
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
            AddTrackEvent(slot.Track, new MidiPitchBendEvent(startTick + originShift, slot.Index, slot.Channel, initialBend));
        lastBendByEndpoint[endpoint] = initialBend;

        AddTrackEvent(slot.Track, new MidiNoteEvent(startTick + originShift, slot.Index, slot.Channel, baseNote, vel, NoteOn: true));

        // Topological emission: pitch states (incl. re-anchor initial bends) in
        // tick order; a re-anchor additionally notes-off the old base and notes-on
        // the new base at the re-anchor tick (order NoteOff, PitchBend, NoteOn via
        // the writer's rank).
        int reIdx = 0;
        foreach (PlannedPitchState state in pitchStates.OrderBy(s => s.Tick).ThenBy(s => s.EncodedBend))
        {
            long t = state.Tick;
            if (reIdx < reanchors.Count && reanchors[reIdx].Tick <= t)
            {
                var re = reanchors[reIdx];
                if (re.Tick == t)
                {
                    AddTrackEvent(slot.Track, new MidiNoteEvent(t + originShift, slot.Index, slot.Channel, re.OldBase, vel, NoteOn: false));
                    AddTrackEvent(slot.Track, new MidiPitchBendEvent(t + originShift, slot.Index, slot.Channel, re.InitialBend));
                    AddTrackEvent(slot.Track, new MidiNoteEvent(t + originShift, slot.Index, slot.Channel, re.NewBase, vel, NoteOn: true));
                    lastBendByEndpoint[endpoint] = re.InitialBend;
                    reIdx++;
                    continue;
                }
            }
            AddTrackEvent(slot.Track, new MidiPitchBendEvent(t + originShift, slot.Index, slot.Channel, state.EncodedBend));
            lastBendByEndpoint[endpoint] = state.EncodedBend;
        }

        // Final note-off on the current base.
        AddTrackEvent(slot.Track, new MidiNoteEvent(endTick + originShift, slot.Index, slot.Channel, currentBase, vel, NoteOn: false));
    }

    private List<(long Tick, double Target, int Order)> BuildPitchAnchors(
        NoteEvent note, double initialSource, int transpose)
    {
        ValidateSourcePitch(note);
        // Initial pitch first, then each causal finite change that occurs strictly
        // after the note start (at/after StartSample fold into the initial pitch)
        // and before/at the note end. Transpose applied exactly once.
        var pts = new List<(long Sample, double Target, int Order)>();
        pts.Add((note.StartSample, initialSource + transpose, -1));
        int seq = 0;
        foreach (PitchChange c in note.Pitch)
        {
            if (!double.IsFinite(c.MidiNote))
                continue;
            // Ignore changes before note start and at/after NoteOff.
            if (c.SamplePosition < note.StartSample || c.SamplePosition >= note.EndSample)
                continue;
            pts.Add((c.SamplePosition, c.MidiNote + transpose, seq++));
        }
        // Same-sample collapse: final (highest source order) wins, source order kept.
        List<(long Sample, double Target, int Order)> collapsed = pts
            .GroupBy(p => p.Sample)
            .Select(g => g.OrderBy(p => p.Order).Last())
            .OrderBy(p => p.Sample)
            .ThenBy(p => p.Order)
            .ToList();
        // Same-tick collapse in the MIDI tick domain: final (latest source order) wins.
        List<(long Tick, double Target, int Order)> byTick = collapsed
            .Select(p => (Tick: TimeTick(p.Sample), p.Target, p.Order))
            .OrderBy(p => p.Tick)
            .ThenBy(p => p.Order)
            .ToList();
        var collapsedByTick = byTick
            .GroupBy(p => p.Tick)
            .Select(g => g.OrderBy(p => p.Order).Last())
            .OrderBy(p => p.Tick)
            .ToList();
        return collapsedByTick;
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

    /// <summary>Appends an event to a track with a deterministic source-sequence key.</summary>
    private void AddTrackEvent(MidiTrack track, MidiEventBase evt)
    {
        evt.SourceOrder = _sourceOrder++;
        track.Events.Add(evt);
    }

    /// <summary>Sign-symmetric 14-bit bend for a semitone offset within the fixed range:
    /// negative offsets map to [0,-8192), positive to [0,+8191]. Non-finite or
    /// out-of-range offsets fail loudly — no Math.Clamp to hide a planner bug.</summary>
    private static int EncodeBend(double offset, int range, NoteEvent note)
    {
        if (!double.IsFinite(offset))
            throw new InvalidOperationException(
                $"Non-finite pitch-bend offset for source note '{note.ChannelId}': {offset}.");
        if (offset < -range || offset > range)
            throw new InvalidOperationException(
                $"Pitch offset {offset:0.###} exceeds the fixed bend range of {range} semitones for source note " +
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
        if (!double.IsFinite(note.InitialMidiNote) || note.InitialMidiNote is < 0 or > 127)
            throw new InvalidOperationException(
                $"Source note '{note.ChannelId}' at sample {note.StartSample} has invalid initial MIDI pitch " +
                $"'{note.InitialMidiNote}'. Expected a finite value in [0, 127].");
        if (note.Pitch is { Count: > 0 })
        {
            foreach (PitchChange change in note.Pitch)
            {
                if (!double.IsFinite(change.MidiNote) || change.MidiNote is < 0 or > 127)
                    throw new InvalidOperationException(
                        $"Source note '{note.ChannelId}' at sample {note.StartSample} has invalid pitch " +
                        $"'{change.MidiNote}' at sample {change.SamplePosition}. Expected a finite value in [0, 127].");
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

    /// <summary>
    /// Computes the minimal integer origin shift over every time-domain event that
    /// is actually emitted: notes, pitch bends, rhythm triggers, loop markers,
    /// SOURCE_START/FIRST_DOWNBEAT markers (when emitted), and later tempo events.
    /// Setup events (the first Set Tempo, Time Signature, RPN, program/bank) stay at
    /// their logical tick 0 and are not shifted. The shift is the smallest whole tick
    /// that makes every time-domain tick nonnegative — NOT aligned to quarter/bar.
    /// </summary>
    private long ComputeOriginShiftTicks(VisualizationTimeline timeline)
    {
        long minTick = TimeTick(_map.FirstSample);
        void Consider(long tick) { if (tick < minTick) minTick = tick; }

        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (!IsNoteEmitted(note))
                continue;
            Consider(TimeTick(note.StartSample));
            if (!ShouldFoldPitch(note))
                continue;
            foreach (PitchChange change in note.Pitch)
            {
                if (change is null || !double.IsFinite(change.MidiNote)) continue;
                Consider(TimeTick(change.SamplePosition));
            }
        }
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null || !IsRhythmEmitted(rhythm))
                continue;
            Consider(TimeTick(rhythm.SamplePosition));
        }
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

    private void BuildConductor(VisualizationTimeline timeline, long originShift, List<MidiEventBase> conductor)
    {
        // Track name + source metadata text.
        if (_options.EmitConductorMetadata)
        {
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, timeline.Source?.Title ?? "MDPlayer Export")));
            if (!string.IsNullOrWhiteSpace(timeline.Source?.SourceFormat))
                conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, $"src-format {timeline.Source.SourceFormat}")));
            conductor.Add(WithSourceOrder(new MidiMetaTextEvent(0, 0x01, $"sample-rate {timeline.SampleRate}")));
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
            foreach (LoopMarker loop in timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
            {
                string name = loop.Kind switch
                {
                    LoopMarkerKind.Start => "LOOP_START",
                    LoopMarkerKind.Restart => "LOOP_END",
                    _ => "LOOP_MARK",
                };
                conductor.Add(WithSourceOrder(new MidiMarkerEvent(TimeTick(loop.SamplePosition) + originShift, name)));
            }
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
            parts.Add($"selected-bpm={FormatDiagnosticDouble(diagnostics.SelectedBpm)}");
            parts.Add($"selected-score={FormatDiagnosticDouble(diagnostics.SelectedScore)}");
            parts.Add($"alternative-bpm={FormatDiagnosticDouble(diagnostics.AlternativeBpm)}");
            parts.Add($"alternative-score={FormatDiagnosticDouble(diagnostics.AlternativeScore)}");
            parts.Add($"alias-margin={FormatDiagnosticDouble(diagnostics.AliasMargin)}");
            parts.Add($"tempo-confidence={FormatDiagnosticDouble(diagnostics.TempoConfidence)}");
            parts.Add($"tempo-ambiguous={diagnostics.TempoAmbiguous.ToString().ToLowerInvariant()}");
            parts.Add($"phase-sample={diagnostics.PhaseSample?.ToString() ?? "none"}");
        }
        return "timing " + string.Join(";", parts);
    }

    /// <summary>Diagnostic doubles serialize as "0.###"; missing values as "none".</summary>
    private static string FormatDiagnosticDouble(double? value) =>
        value is double d ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "none";

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

        // Percussion naming needs the set of distinct rhythm identities per chip:
        // a chip with exactly one rhythm voice gets the clean "<CHIP> Rhythm" name,
        // and multiple voices are disambiguated by their short voice name.
        var rhythmCountByChip = keyOrder
            .Where(key => key.Instrument.Family == IdentityFamily.Rhythm)
            .GroupBy(key => key.Chip)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (MidiTrackKey key in keyOrder)
        {
            string channelId = representativeByKey[key];
            VoiceExportOverride voiceOverride = _options.OverrideFor(channelId);
            if (!voiceOverride.Include)
                continue; // excluded voice: no track, no notes.
            allocator.Add(key, index++, channelId, voiceOverride, _options, WithSourceOrder,
                rhythmCountByChip.GetValueOrDefault(key.Chip));
        }
        return allocator;
    }

    /// <summary>
    /// Emits the fixed pitch-bend-range RPN setup (Patch B) once per melodic track
    /// that actually emits bends, at logical tick 0. A track that emits no bends
    /// gets no RPN setup. The range is the configured fixed value (default 24) —
    /// never auto-expanded.
    /// </summary>
    private void EmitBendRangeSetup(TrackAllocator allocator)
    {
        int range = _options.BendRangeSemitones;
        if (range is < 1 or > 127)
            throw new ArgumentOutOfRangeException(nameof(_options), "Bend range must be in [1, 127].");
        foreach ((MidiTrackKey key, TrackSlot slot) in allocator.Slots.OrderBy(pair => pair.Value.Index))
        {
            if (slot.Percussive)
                continue;
            // Only emit the RPN setup on tracks that actually serialized a bend; a
            // constant-pitch melodic track needs no bend infrastructure.
            if (!slot.Track.Events.Any(e => e is MidiPitchBendEvent))
                continue;
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
        MidiTrackKey key;
        if (note.Domain is SourceDomainKey domain
            && InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity typedInstrument))
            key = new MidiTrackKey(domain.Device, domain.VoiceFamily, domain.Index, typedInstrument);
        else if (TryParseSourceDomain(note.ChannelId, out DeviceId device, out VoiceKind voice, out int sourceChannel)
                 && InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity instrument))
            key = new MidiTrackKey(device, voice, sourceChannel, instrument);
        else
            return PlaceholderKey(note.ChannelId);
        // SN76489 tone/noise is NOT an instrument (spec 30): PSG has no patch object
        // comparable to FM, so the track is keyed by source channel only (channels
        // are already split by VoiceKind.Psg index 0-2 / VoiceKind.Noise index 0).
        // TryParse still accepts sn76489:* so it never placeholders; the semantic
        // naming (SN76489 PSG CH1-3 / SN76489 Noise) carries the voice meaning.
        if (key.Device.Type == ChipType.Sn76489)
            key = key with { Instrument = InstrumentIdentity.Empty };
        return key;
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
        private readonly HashSet<MidiEndpoint> _usedEndpoints = new();
        private readonly Dictionary<MidiChannelDomain, int> _requestedChannelsByDomain = new();

        public void Add(MidiTrackKey key, int index, string channelId, VoiceExportOverride voiceOverride,
            MusicalMidiExportOptions options, Func<MidiEventBase, MidiEventBase> withOrder,
            int rhythmCountForChip = 0)
        {
            TrackSlot? existing = SlotFor(key);
            if (existing is not null)
                return; // key already allocated (defensive; BuildTracks dedupes).
            bool percussive = key.Instrument.Family == IdentityFamily.Rhythm;
            string name = IdentityNameFor(key, percussive, channelId, rhythmCountForChip);
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

    private readonly record struct MidiChannelDomain(
        DeviceId Device,
        VoiceKind VoiceFamily,
        int SourceChannel);

    /// <summary>A single source note ready for pitch/note planning against its slot.</summary>
    private readonly record struct PlannableNote(TrackSlot Slot, NoteEvent Note);

    /// <summary>A planned pitch event at an absolute MIDI tick: the target pitch and its
    /// encoded bend, resolved through tick-domain collapse and re-anchoring.</summary>
    private readonly record struct PlannedPitchState(long Tick, double Target, int EncodedBend);

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
        public bool Percussive { get; }

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
