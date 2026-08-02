using System.Collections.Frozen;
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
        NoteColorMode noteColorMode = NoteColorMode.Instrument,
        VisualizationPalette palette = null)
        => Build(
            timeline,
            layout,
            VisualizationTopologyBuilder.Build(timeline),
            metadata,
            samplesPerFrame,
            noteColorMode,
            palette);

    public static OverlayScene Build(
        VisualizationTimeline timeline,
        OverlayLayout layout,
        VisualizationTopology topology,
        VisualizationMetadata metadata = null,
        double samplesPerFrame = 0,
        NoteColorMode noteColorMode = NoteColorMode.Instrument,
        VisualizationPalette palette = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(topology);
        palette ??= VisualizationPalette.Default;

        timeline = VisualizationTimelineCompatibility.Upgrade(timeline);

        var instrumentById = timeline.Instruments.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var waveformById = timeline.Waveforms.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var sampleById = timeline.Samples.ToDictionary(value => value.Id, StringComparer.Ordinal);
        VisualizationTrackDescriptor[] descriptors = topology.Panels
            .Select(panel => CreateTrackDescriptor(panel, timeline))
            .ToArray();
        VisualizationSemanticScene semantic = VisualizationSemanticSceneBuilder.Build(
            timeline,
            descriptors);
        long[] compositionOnsets = BuildCompositionOnsets(timeline);
        var panels = new PreparedPanel[topology.Panels.Count];
        for (int index = 0; index < panels.Length; index++)
        {
            VisualizationPanel topologyPanel = topology.Panels[index];
            string id = topologyPanel.Id;
            var kind = topologyPanel.Kind;
            VisualizationTrackDescriptor track = descriptors[index];
            double pitchTolerance = PitchToleranceSemitones(layout, index, kind);
            IEnumerable<NoteEvent> sourceNotes = timeline.Notes
                .Where(n => topologyPanel.VoiceIds.Contains(n.ChannelId, StringComparer.Ordinal));
            if (track.PitchSystem is not PitchCoordinateSystem.None
                and not PitchCoordinateSystem.AbsoluteMidi)
                sourceNotes = sourceNotes.Select(note => ConvertPitchCoordinates(note, track));

            var mainNotes = (kind is PreparedPanelKind.Generic or PreparedPanelKind.Pitched or PreparedPanelKind.Fm3 or PreparedPanelKind.Ssg
                    or PreparedPanelKind.Wavetable or PreparedPanelKind.PcmVoice or PreparedPanelKind.Noise)
                ? ToPreparedNotes(
                    sourceNotes,
                    pitchTolerance,
                    samplesPerFrame,
                    timeline.SampleRate,
                    compositionOnsets,
                    index,
                    noteColorMode,
                    instrumentById,
                    palette)
                : Array.Empty<PreparedNote>();

            var operatorNotes = kind == PreparedPanelKind.Fm3
                ? Enumerable.Range(0, 4).Select(op =>
                    ToPreparedNotes(
                        ConvertPitchCoordinatesIfNeeded(
                            topologyPanel.OperatorVoiceIds.Count > op
                                ? timeline.Notes.Where(n => string.Equals(
                                    n.ChannelId,
                                    topologyPanel.OperatorVoiceIds[op],
                                    StringComparison.Ordinal))
                                : Enumerable.Empty<NoteEvent>(),
                            track),
                        pitchTolerance,
                        samplesPerFrame,
                        timeline.SampleRate,
                        compositionOnsets,
                        index,
                        noteColorMode,
                        instrumentById,
                        palette)
                ).ToArray()
                : Array.Empty<PreparedNote[]>();

            PreparedNote[] cameraNotes = kind == PreparedPanelKind.Fm3
                ? mainNotes.Concat(operatorNotes.SelectMany(notes => notes)).ToArray()
                : mainNotes;

            var rhythm = kind == PreparedPanelKind.Rhythm
                ? timeline.Rhythm
                    .Where(e => topologyPanel.VoiceIds.Any(id =>
                        VisualizationTimelineCompatibility.RhythmBelongsToVoice(timeline, e, id)))
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

            string[] voiceIds = topologyPanel.VoiceIds.Concat(topologyPanel.OperatorVoiceIds).ToArray();
            HashSet<string> voiceIdSet = voiceIds.ToHashSet(StringComparer.Ordinal);
            WaveformChangeEvent[] waveformChanges = timeline.WaveformChanges
                .Where(value => voiceIdSet.Contains(value.VoiceId)
                    && value.SamplePosition >= timeline.StartSample
                    && value.SamplePosition <= timeline.EndSample)
                .OrderBy(value => value.SamplePosition)
                .ThenBy(value => value.WaveformId, StringComparer.Ordinal)
                .ToArray();
            WaveformDefinition[] waveforms = waveformChanges
                .Where(value => waveformById.ContainsKey(value.WaveformId))
                .Select(value => waveformById[value.WaveformId])
                .DistinctBy(value => value.Id, StringComparer.Ordinal)
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();

            SamplePlaybackEvent[] samplePlayback = timeline.SamplePlayback
                .Where(value => voiceIdSet.Contains(value.VoiceId)
                    && value.EndSample > timeline.StartSample
                    && value.StartSample < timeline.EndSample
                    && value.EndSample > value.StartSample)
                .OrderBy(value => value.StartSample)
                .ThenBy(value => value.SampleId, StringComparer.Ordinal)
                .ToArray();
            SpcVoiceStateEvent[] spcVoiceStates = timeline.SpcVoiceStates
                .Where(value => voiceIdSet.Contains(value.VoiceId)
                    && value.SamplePosition >= timeline.StartSample
                    && value.SamplePosition <= timeline.EndSample)
                .OrderBy(value => value.SamplePosition)
                .ThenBy(value => value.State, StringComparer.Ordinal)
                .ToArray();
            SampleDefinition[] samples = samplePlayback
                .Where(value => sampleById.ContainsKey(value.SampleId))
                .Select(value => sampleById[value.SampleId])
                .DistinctBy(value => value.Id, StringComparer.Ordinal)
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();

            NoiseStateEvent[] noise = timeline.NoiseStates
                .Where(value => voiceIdSet.Contains(value.VoiceId)
                    && value.EndSample > timeline.StartSample
                    && value.StartSample < timeline.EndSample
                    && value.EndSample > value.StartSample)
                .OrderBy(value => value.StartSample)
                .ToArray();
            string[] noiseLabels = noise.Select(FormatNoiseLabel).ToArray();
            AggregateHitEvent[] aggregateHits = timeline.AggregateHits
                .Where(value => voiceIdSet.Contains(value.VoiceId)
                    && value.SamplePosition >= timeline.StartSample
                    && value.SamplePosition <= timeline.EndSample
                    && (topologyPanel.AggregateSubVoiceIds.Count == 0
                        || topologyPanel.AggregateSubVoiceIds.Contains(value.SubVoiceId, StringComparer.Ordinal)))
                .OrderBy(value => value.SamplePosition)
                .ThenBy(value => value.SubVoiceId, StringComparer.Ordinal)
                .ToArray();
            string[] aggregateSubVoices = (topologyPanel.AggregateSubVoiceIds.Count > 0
                    ? topologyPanel.AggregateSubVoiceIds
                    : aggregateHits.Select(value => value.SubVoiceId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(16)
                .ToArray();
            var aggregateLabels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string subVoice in aggregateSubVoices)
            {
                if (topologyPanel.AggregateSubVoiceLabels.TryGetValue(subVoice, out string label)
                    && !string.IsNullOrWhiteSpace(label))
                {
                    aggregateLabels[subVoice] = label;
                    continue;
                }

                aggregateLabels[subVoice] = aggregateHits
                    .Where(value => value.SubVoiceId == subVoice)
                    .Select(value => value.Label)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? subVoice;
            }

            // Compute pitch range from visible notes.
            var (minMidi, maxMidi) = ComputePitchRange(mainNotes);
            if (track.Kind == VisualizationTrackKind.Sample && mainNotes.Length > 0)
            {
                // Sample tracks remain event lanes when pitch is unknown, but
                // a decoder-provided finite MIDI note is a valid absolute
                // coordinate and may use the pitched sample presentation.
                track = track with { PitchSystem = PitchCoordinateSystem.AbsoluteMidi };
            }

            bool hasTrackEvents = kind switch
            {
                PreparedPanelKind.Fm3 =>
                    mainNotes.Length > 0 ||
                    Array.Exists(operatorNotes, notes => notes.Length > 0),
                PreparedPanelKind.Pitched or PreparedPanelKind.Ssg
                    or PreparedPanelKind.Wavetable =>
                    mainNotes.Length > 0,
                PreparedPanelKind.Rhythm =>
                    rhythm.Length > 0,
                PreparedPanelKind.PcmVoice =>
                    samplePlayback.Length > 0 || mainNotes.Length > 0,
                PreparedPanelKind.Noise => noise.Length > 0 || mainNotes.Length > 0,
                PreparedPanelKind.Aggregate => aggregateHits.Length > 0,
                PreparedPanelKind.Generic => mainNotes.Length > 0
                    || rhythm.Length > 0
                    || samplePlayback.Length > 0
                    || noise.Length > 0
                    || aggregateHits.Length > 0,
                PreparedPanelKind.Placeholder => false,
                _ => false,
            };

            panels[index] = new PreparedPanel
            {
                Index = index,
                Id = id,
                Label = topologyPanel.Label,
                Kind = kind,
                Content = topologyPanel.Content,
                Schema = topologyPanel.Schema == PanelPresentationSchema.Unknown
                    ? VisualizationTopologyBuilder.SchemaFor(kind)
                    : topologyPanel.Schema,
                Track = track,
                Rows = topologyPanel.Rows.ToArray(),
                MainNotes = mainNotes,
                UsesSsgModes = mainNotes.Any(note => note.Mode is
                    VisualizationNoteMode.SsgTone
                    or VisualizationNoteMode.SsgToneNoise
                    or VisualizationNoteMode.SsgNoise
                    or VisualizationNoteMode.SsgEnvelopeTone
                    or VisualizationNoteMode.SsgEnvelopeToneNoise
                    or VisualizationNoteMode.SsgEnvelopeNoise),
                InstrumentChanges = mainNotes.Where(note => note.HasInstrumentChange).ToArray(),
                OperatorNotes = operatorNotes,
                CameraNotes = cameraNotes,
                Rhythm = rhythm,
                Waveforms = waveforms,
                WaveformsById = waveformById.ToFrozenDictionary(StringComparer.Ordinal),
                WaveformChanges = waveformChanges,
                Samples = samples,
                SamplesById = sampleById.ToFrozenDictionary(StringComparer.Ordinal),
                SamplePlayback = samplePlayback,
                SpcVoiceStates = spcVoiceStates,
                Noise = noise,
                NoiseLabels = noiseLabels,
                AggregateHits = aggregateHits,
                AggregateSubVoices = aggregateSubVoices,
                AggregateLabels = aggregateLabels.ToFrozenDictionary(StringComparer.Ordinal),
                MinMidi = minMidi,
                MaxMidi = maxMidi,
                Accent = InstrumentColorResolver.ResolveChannelAccent(topologyPanel.Id),
                HasTrackEvents = hasTrackEvents,
            };
        }

        return new OverlayScene
        {
            Layout = layout,
            Topology = topology,
            Semantic = semantic,
            Panels = panels,
            Metadata = metadata ?? new VisualizationMetadata(),
            SampleRate = timeline.SampleRate,
            StartSample = timeline.StartSample,
            EndSample = timeline.EndSample,
        };
    }

    private static VisualizationTrackDescriptor CreateTrackDescriptor(
        VisualizationPanel panel,
        VisualizationTimeline timeline)
    {
        PanelPresentationSchema schema = panel.Schema == PanelPresentationSchema.Unknown
            ? VisualizationTopologyBuilder.SchemaFor(panel.Kind)
            : panel.Schema;
        VisualizationTrackKind kind = schema switch
        {
            PanelPresentationSchema.GenericLane => VisualizationTrackKind.Generic,
            PanelPresentationSchema.PitchedLane => VisualizationTrackKind.Pitched,
            PanelPresentationSchema.FmOperatorGroup => VisualizationTrackKind.FmOperatorGroup,
            PanelPresentationSchema.NoiseLane => VisualizationTrackKind.Noise,
            PanelPresentationSchema.PercussionRows => VisualizationTrackKind.Percussion,
            PanelPresentationSchema.SampleLane => VisualizationTrackKind.Sample,
            PanelPresentationSchema.WaveTableLane => VisualizationTrackKind.WaveTable,
            PanelPresentationSchema.AggregateActivity => VisualizationTrackKind.AggregateActivity,
            _ => VisualizationTrackKind.Unsupported,
        };

        PitchCoordinateSystem pitchSystem = kind is VisualizationTrackKind.Pitched
            or VisualizationTrackKind.FmOperatorGroup
            or VisualizationTrackKind.WaveTable
            ? ResolvePitchSystem(panel, timeline)
            : PitchCoordinateSystem.None;
        VisualizationRowKind rowKind = kind switch
        {
            VisualizationTrackKind.Percussion => VisualizationRowKind.Trigger,
            VisualizationTrackKind.Noise => VisualizationRowKind.Noise,
            VisualizationTrackKind.Sample => VisualizationRowKind.Sample,
            _ => VisualizationRowKind.Note,
        };

        bool supportsScope = timeline.Devices.Count == 0
            || panel.VoiceIds.Any(voiceId => timeline.Voices
                .Where(voice => string.Equals(voice.Id.ToString(), voiceId, StringComparison.Ordinal))
                .Join(
                    timeline.Devices,
                    voice => voice.DeviceId,
                    device => device.Id,
                    (_, device) => device.ScopeSupport)
                .Any(scope => scope != ScopeSupport.None));

        VoiceDescriptor primaryVoice = timeline.Voices.FirstOrDefault(voice =>
            panel.VoiceIds.Contains(voice.Id.ToString(), StringComparer.Ordinal));
        string[] sourceIds = panel.VoiceIds.Concat(panel.OperatorVoiceIds).ToArray();
        long activeSamples = timeline.Notes
            .Where(note => sourceIds.Contains(note.ChannelId, StringComparer.Ordinal))
            .Where(note => note.EndSample > note.StartSample)
            .Sum(note => note.EndSample - note.StartSample);
        int eventCount = timeline.Notes.Count(note =>
            sourceIds.Contains(note.ChannelId, StringComparer.Ordinal));
        double duration = Math.Max(1, timeline.EndSample - timeline.StartSample);
        double activeDuration = Math.Clamp(activeSamples / duration, 0, 1);
        double eventDensity = Math.Clamp(eventCount / 24.0, 0, 1);
        double leadConfidence = primaryVoice?.LeadRoleConfidence is double explicitConfidence
            ? Math.Clamp(explicitConfidence, 0, 1)
            : Math.Clamp(
                0.55 * activeDuration
                + 0.25 * eventDensity
                + (kind is VisualizationTrackKind.Pitched or VisualizationTrackKind.FmOperatorGroup ? 0.20 : 0),
                0,
                1);
        double salience = 1.0
            + 0.30 * activeDuration
            + 0.20 * eventDensity
            + 0.20 * (kind is VisualizationTrackKind.Pitched or VisualizationTrackKind.FmOperatorGroup ? 1 : 0)
            + 0.15 * leadConfidence
            + 0.15 * (supportsScope ? 0.5 : 0);

        return new VisualizationTrackDescriptor
        {
            Id = panel.Id,
            DisplayName = panel.Label,
            Kind = kind,
            GroupId = panel.Content.ToString(),
            DeviceId = timeline.Voices
                .FirstOrDefault(voice => panel.VoiceIds.Contains(
                    voice.Id.ToString(), StringComparer.Ordinal))?.DeviceId.ToString()
                ?? string.Empty,
            StableOrder = panel.Order,
            Priority = kind == VisualizationTrackKind.Pitched ? 0 : 1,
            LeadRoleConfidence = leadConfidence,
            SalienceScore = salience,
            PitchSystem = pitchSystem,
            PitchAnchorMidi = timeline.Voices
                .Where(voice => panel.VoiceIds.Contains(
                    voice.Id.ToString(), StringComparer.Ordinal)
                    || panel.OperatorVoiceIds.Contains(
                        voice.Id.ToString(), StringComparer.Ordinal))
                .Select(voice => voice.RelativePitchAnchorMidi)
                .FirstOrDefault(value => value is double),
            SupportsScope = supportsScope,
            SupportsEnergy = supportsScope,
            IsPercussion = kind == VisualizationTrackKind.Percussion,
            IsOptional = kind is VisualizationTrackKind.Noise
                or VisualizationTrackKind.Sample
                or VisualizationTrackKind.AggregateActivity,
            Rows = panel.Rows
                .Select((row, index) => new VisualizationRowDescriptor(
                    row.Id,
                    row.Label,
                    row.StableOrder == 0 && index > 0 ? index : row.StableOrder,
                    row.Kind == VisualizationRowKind.Other ? rowKind : row.Kind))
                .ToArray(),
            SourceVoiceIds = panel.VoiceIds.Concat(panel.OperatorVoiceIds).ToArray(),
            ScopeStemIds = panel.VoiceIds.ToArray(),
        };
    }

    private static NoteEvent ConvertPitchCoordinates(
        NoteEvent note,
        VisualizationTrackDescriptor track)
    {
        double initialValue = track.PitchSystem == PitchCoordinateSystem.FrequencyHz
            ? double.IsFinite(note.InitialFrequencyHz)
                ? note.InitialFrequencyHz
                : note.InitialMidiNote
            : note.InitialMidiNote;
        bool initialValid = PitchCoordinateConverter.TryConvertToMidi(
            track.PitchSystem,
            initialValue,
            track.PitchAnchorMidi,
            out double initialMidi);

        PitchChange[] points = (note.Pitch ?? Array.Empty<PitchChange>())
            .Select(point =>
            {
                double sourceValue = track.PitchSystem == PitchCoordinateSystem.FrequencyHz
                    ? double.IsFinite(point.FrequencyHz)
                        ? point.FrequencyHz
                        : point.MidiNote
                    : point.MidiNote;
                return PitchCoordinateConverter.TryConvertToMidi(
                    track.PitchSystem,
                    sourceValue,
                    track.PitchAnchorMidi,
                    out double midi)
                    ? new PitchChange(point.SamplePosition, point.FrequencyHz, midi)
                    : null;
            })
            .Where(point => point is not null)
            .Cast<PitchChange>()
            .ToArray();

        return note with
        {
            InitialMidiNote = initialValid ? initialMidi : double.NaN,
            Pitch = points,
        };
    }

    private static IEnumerable<NoteEvent> ConvertPitchCoordinatesIfNeeded(
        IEnumerable<NoteEvent> notes,
        VisualizationTrackDescriptor track)
        => track.PitchSystem is not PitchCoordinateSystem.None
            and not PitchCoordinateSystem.AbsoluteMidi
            ? notes.Select(note => ConvertPitchCoordinates(note, track))
            : notes;

    private static PitchCoordinateSystem ResolvePitchSystem(
        VisualizationPanel panel,
        VisualizationTimeline timeline)
    {
        PitchCoordinateSystem[] systems = timeline.Voices
            .Where(voice => panel.VoiceIds.Contains(
                voice.Id.ToString(), StringComparer.Ordinal)
                || panel.OperatorVoiceIds.Contains(
                    voice.Id.ToString(), StringComparer.Ordinal))
            .Where(voice => voice.SupportsPitch)
            .Select(voice => voice.PitchSystem)
            .Distinct()
            .ToArray();

        if (systems.Length == 0)
            return PitchCoordinateSystem.AbsoluteMidi;
        if (systems.Length == 1)
            return systems[0];

        // Normalized timeline note values are MIDI. Mixed absolute systems can
        // therefore share that normalized coordinate; relative systems remain
        // explicit so the planner can keep them out of an absolute roll.
        return systems.All(system => system is PitchCoordinateSystem.AbsoluteMidi
            or PitchCoordinateSystem.AbsoluteSemitone
            or PitchCoordinateSystem.FrequencyHz)
            ? PitchCoordinateSystem.AbsoluteMidi
            : PitchCoordinateSystem.None;
    }

    private static string FormatNoiseLabel(NoiseStateEvent value)
    {
        string descriptor = value.CentreFrequencyHz is > 0 and double frequency
            ? $"{frequency:0.#} Hz"
            : value.Mode.ToString().ToUpperInvariant();
        return $"{descriptor}  LVL {Math.Clamp(value.Level, 0, 1):0.00}";
    }

    /// <summary>
    /// Creates the immutable onset index used by all tracks for density
    /// classification. Density is a composition property, not a property of
    /// one small panel, so simultaneous events on different tracks suppress
    /// ornamental effects consistently.
    /// </summary>
    private static long[] BuildCompositionOnsets(VisualizationTimeline timeline)
    {
        var onsets = new List<long>(timeline.Notes.Count
            + timeline.Rhythm.Count
            + timeline.SamplePlayback.Length
            + timeline.NoiseStates.Length
            + timeline.AggregateHits.Length);
        onsets.AddRange(timeline.Notes.Where(IsNoteValid).Select(note => note.StartSample));
        onsets.AddRange(timeline.Rhythm.Select(value => value.SamplePosition));
        onsets.AddRange(timeline.SamplePlayback
            .Where(value => value.EndSample > value.StartSample)
            .Select(value => value.StartSample));
        onsets.AddRange(timeline.NoiseStates
            .Where(value => value.EndSample > value.StartSample)
            .Select(value => value.StartSample));
        onsets.AddRange(timeline.AggregateHits.Select(value => value.SamplePosition));
        onsets.Sort();
        return onsets.ToArray();
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
        IReadOnlyList<long> compositionOnsets,
        int panelIndex,
        NoteColorMode noteColorMode,
        IReadOnlyDictionary<string, InstrumentDefinition> instruments,
        VisualizationPalette palette)
    {
        NoteEvent[] ordered = source
            .Where(IsNoteValid)
            .OrderBy(n => n.StartSample)
            .ThenBy(n => n.EndSample)
            .ToArray();

        var prepared = new PreparedNote[ordered.Length];
        bool[] dense = ComputeDenseOnsets(ordered, sampleRate, compositionOnsets);
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
                instruments,
                palette);
        }
        return prepared;
    }

    /// <summary>
    /// Marks notes whose onset falls inside a 100 ms window containing more
    /// than 8 onsets (§9.3). A dense onset suppresses the ripple alpha and the
    /// active-flash size enlargement but never the onset caps. Uses a
    /// two-pointer sweep over the sorted composition onset array: a track note
    /// is dense when more than 8 semantic onsets start within
    /// <c>[onset, onset + 100ms)</c> across the visible composition.
    /// </summary>
    private static bool[] ComputeDenseOnsets(
        NoteEvent[] ordered,
        int sampleRate,
        IReadOnlyList<long> compositionOnsets)
    {
        int n = ordered.Length;
        var dense = new bool[n];
        if (n == 0 || sampleRate <= 0 || compositionOnsets.Count == 0)
            return dense;

        long windowSamples = (long)Math.Round(0.1 * sampleRate); // 100 ms
        int threshold = 8; // more than 8 onsets → dense
        var denseSamples = new HashSet<long>();
        int right = 0;
        for (int i = 0; i < compositionOnsets.Count; i++)
        {
            if (right < i)
                right = i;
            long windowEnd = compositionOnsets[i] + windowSamples;
            while (right < compositionOnsets.Count && compositionOnsets[right] < windowEnd)
                right++;
            int count = right - i;
            if (count > threshold)
            {
                for (int j = i; j < right; j++)
                    denseSamples.Add(compositionOnsets[j]);
            }
        }
        for (int i = 0; i < n; i++)
            dense[i] = denseSamples.Contains(ordered[i].StartSample);
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
        IReadOnlyDictionary<string, InstrumentDefinition> instruments = null,
        VisualizationPalette palette = null)
    {
        var fill = (palette ?? VisualizationPalette.Default).ResolveNote(
            noteColorMode,
            note.InstrumentId,
            note.ChannelId,
            note.InitialMidiNote);
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
    /// duplicates, then applies zero-order-hold-aware bounded-error
    /// simplification. It never replaces state changes with a linear ramp.
    /// </summary>
    private static PreparedPitchPoint[] BuildPitchPoints(NoteEvent note, double toleranceSemitones)
    {
        var valid = new List<PreparedPitchPoint>(Math.Min(note.Pitch.Count, 64));
        foreach (PitchChange p in note.Pitch)
        {
            if (!double.IsFinite(p.MidiNote) || p.MidiNote < 0
                || p.SamplePosition < note.StartSample
                || p.SamplePosition > note.EndSample)
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

        return SimplifyStepPitch(deduplicated.ToArray(), toleranceSemitones);
    }

    /// <summary>
    /// Simplifies a zero-order-hold contour without changing its interpolation
    /// model. An omitted point is guaranteed to differ from the currently held
    /// retained value by at most <paramref name="toleranceSemitones"/>. The
    /// final point is always retained so the note's terminal pitch is exact.
    /// </summary>
    private static PreparedPitchPoint[] SimplifyStepPitch(
        PreparedPitchPoint[] points,
        double toleranceSemitones)
    {
        if (points.Length <= 2 || toleranceSemitones <= 0)
            return points;

        var result = new List<PreparedPitchPoint>(points.Length);
        result.Add(points[0]);
        double held = points[0].MidiNote;
        for (int i = 1; i < points.Length - 1; i++)
        {
            if (Math.Abs(points[i].MidiNote - held) > toleranceSemitones)
            {
                result.Add(points[i]);
                held = points[i].MidiNote;
            }
        }
        result.Add(points[^1]);
        return result.ToArray();
    }

    /// <summary>
    /// Validates a note for preparation. Rejects notes with
    /// non-positive duration, invalid MIDI pitch, or non-finite values.
    /// </summary>
    private static bool IsNoteValid(NoteEvent note)
    {
        if (note.EndSample <= note.StartSample)
            return false;
        if (!double.IsFinite(note.InitialMidiNote)
            && (!double.IsFinite(note.InitialFrequencyHz) || note.InitialFrequencyHz <= 0))
            return false;
        // Invalid pitch samples are discarded during contour preparation; the
        // note remains usable at its finite initial pitch. This preserves a
        // valid semantic event when one decoder sample is corrupt.
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
