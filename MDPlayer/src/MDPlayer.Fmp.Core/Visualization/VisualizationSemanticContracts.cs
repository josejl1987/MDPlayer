namespace Fmp.Core.Visualization;

internal enum VisualizationTrackKind
{
    Pitched,
    FmOperatorGroup,
    Noise,
    Percussion,
    Sample,
    WaveTable,
    ParameterActivity,
    AggregateActivity,
    Unsupported,
}

internal enum PitchCoordinateSystem
{
    None,
    AbsoluteMidi,
    AbsoluteSemitone,
    RelativeSemitone,
    FrequencyHz,
}

internal static class PitchCoordinateConverter
{
    public static bool TryConvertToMidi(
        PitchCoordinateSystem system,
        double value,
        double? relativeAnchorMidi,
        out double midi)
    {
        midi = 0;
        if (!double.IsFinite(value))
            return false;

        switch (system)
        {
            case PitchCoordinateSystem.AbsoluteMidi:
            case PitchCoordinateSystem.AbsoluteSemitone:
                midi = value;
                return true;
            case PitchCoordinateSystem.FrequencyHz:
                if (value <= 0)
                    return false;
                midi = 69 + 12 * Math.Log2(value / 440.0);
                return double.IsFinite(midi);
            case PitchCoordinateSystem.RelativeSemitone:
                if (relativeAnchorMidi is not double anchor || !double.IsFinite(anchor))
                    return false;
                midi = anchor + value;
                return double.IsFinite(midi);
            default:
                return false;
        }
    }
}

internal enum VisualizationRowKind
{
    Note,
    Trigger,
    Noise,
    Sample,
    Parameter,
    Other,
}

internal sealed record VisualizationRowDescriptor(
    string Id,
    string Label,
    int StableOrder,
    VisualizationRowKind Kind);

/// <summary>
/// Renderer-neutral semantic track contract. Decoders/adapters populate this
/// descriptor; presentation code consumes it without chip or format checks.
/// </summary>
internal sealed record VisualizationTrackDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required VisualizationTrackKind Kind { get; init; }
    public string GroupId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public int StableOrder { get; init; }
    public int Priority { get; init; }
    public double LeadRoleConfidence { get; init; }
    public double SalienceScore { get; init; }
    public PitchCoordinateSystem PitchSystem { get; init; } = PitchCoordinateSystem.None;
    public double? PitchAnchorMidi { get; init; }
    public bool SupportsScope { get; init; }
    public bool SupportsEnergy { get; init; }
    public bool IsPercussion { get; init; }
    public bool IsOptional { get; init; }
    public IReadOnlyList<VisualizationRowDescriptor> Rows { get; init; }
        = Array.Empty<VisualizationRowDescriptor>();
    public IReadOnlyList<string> SourceVoiceIds { get; init; }
        = Array.Empty<string>();
    public IReadOnlyList<string> ScopeStemIds { get; init; }
        = Array.Empty<string>();
}

internal abstract record VisualizationSemanticEvent(long StartSample, long EndSample, double Confidence = 1.0);

internal sealed record PitchedNoteEvent(
    long Start,
    long End,
    double Midi,
    string InstrumentId,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, End, Confidence);

internal sealed record TriggerEvent(
    long Start,
    long End,
    string RowId,
    double Strength,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, End, Confidence);

internal sealed record SamplePlaybackSemanticEvent(
    long Start,
    long End,
    string SampleId,
    double? Midi,
    double Volume,
    double Pan,
    bool Loop,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, End, Confidence);

internal sealed record NoiseActivityEvent(
    long Start,
    long End,
    double Energy,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, End, Confidence);

internal sealed record ParameterChangeEvent(
    long Start,
    string ParameterId,
    double Value,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, Start, Confidence);

internal sealed record InstrumentChangeEvent(
    long Start,
    string InstrumentId,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, Start, Confidence);

internal sealed record PitchCurveEvent(
    long Start,
    long End,
    IReadOnlyList<(long Sample, double Midi)> Points,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, End, Confidence);

internal sealed record EnergyEnvelopeEvent(
    long Start,
    long End,
    IReadOnlyList<(long Sample, double Energy)> Points,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, End, Confidence);

internal sealed record SectionMarkerEvent(
    long Start,
    string Label,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, Start, Confidence);

internal sealed record BeatMarkerEvent(
    long Start,
    double BeatIndex,
    bool Authoritative,
    double Confidence = 1.0)
    : VisualizationSemanticEvent(Start, Start, Confidence);

internal sealed record VisualizationTrackActivityStatistics(
    int EventCount,
    long ActiveDurationSamples,
    double EventDensity,
    double? MinimumMidi,
    double? MaximumMidi,
    double AverageConfidence,
    int InstrumentChangeCount,
    int PercussionHitCount,
    bool ScopeEnergyAvailable);

/// <summary>
/// Normalized, immutable semantic content for one prepared presentation
/// track. Rendering code may choose a style for these events without knowing
/// which decoder or chip produced them.
/// </summary>
internal sealed record VisualizationSemanticTrack(
    VisualizationTrackDescriptor Descriptor,
    IReadOnlyList<VisualizationSemanticEvent> Events,
    VisualizationTrackActivityStatistics Activity);

/// <summary>
/// Immutable semantic scene prepared before layout and frame rendering. It is
/// the common contract shared by publishing and diagnostic compositions.
/// </summary>
internal sealed record VisualizationSemanticScene(
    IReadOnlyList<VisualizationSemanticTrack> Tracks,
    IReadOnlyList<SectionMarkerEvent> Sections,
    IReadOnlyList<BeatMarkerEvent> Beats);

internal static class VisualizationSemanticSceneBuilder
{
    public static VisualizationSemanticScene Build(
        VisualizationTimeline timeline,
        IReadOnlyList<VisualizationTrackDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(descriptors);

        var tracks = new VisualizationSemanticTrack[descriptors.Count];
        for (int index = 0; index < descriptors.Count; index++)
        {
            VisualizationTrackDescriptor descriptor = descriptors[index];
            HashSet<string> sourceIds = descriptor.SourceVoiceIds
                .ToHashSet(StringComparer.Ordinal);
            var events = new List<VisualizationSemanticEvent>();

            foreach (NoteEvent note in timeline.Notes
                .Where(value => sourceIds.Contains(value.ChannelId)
                    && value.EndSample > value.StartSample
                    && CanAttemptPitch(descriptor, value)))
            {
                bool noise = note.Mode is VisualizationNoteMode.SsgNoise
                    or VisualizationNoteMode.SsgEnvelopeNoise;
                if (noise)
                {
                    events.Add(new NoiseActivityEvent(
                        note.StartSample,
                        note.EndSample,
                        1.0));
                }
                else if (TryConvertPitch(
                    descriptor,
                    note.InitialFrequencyHz,
                    note.InitialMidiNote,
                    out double initialMidi))
                {
                    events.Add(new PitchedNoteEvent(
                        note.StartSample,
                        note.EndSample,
                        initialMidi,
                        note.InstrumentId));
                }

                var pitchPoints = new List<PitchCurvePoint>();
                foreach (PitchChange point in note.Pitch ?? [])
                {
                    if (point.SamplePosition < note.StartSample
                        || point.SamplePosition > note.EndSample
                        || !TryConvertPitch(
                            descriptor,
                            point.FrequencyHz,
                            point.MidiNote,
                            out double midi))
                        continue;
                    pitchPoints.Add(new PitchCurvePoint(point.SamplePosition, midi));
                }
                if (pitchPoints.Count > 0)
                {
                    events.Add(new PitchCurveEvent(
                        note.StartSample,
                        note.EndSample,
                        pitchPoints.Select(point => (point.Sample, point.Midi)).ToArray()));
                }
            }

            foreach (SamplePlaybackEvent sample in timeline.SamplePlayback
                .Where(value => sourceIds.Contains(value.VoiceId)
                    && value.EndSample > value.StartSample))
            {
                double? midi = sample.MidiPitch is double value
                    && double.IsFinite(value) ? value : null;
                events.Add(new SamplePlaybackSemanticEvent(
                    sample.StartSample,
                    sample.EndSample,
                    sample.SampleId,
                    midi,
                    sample.Gain,
                    sample.Pan,
                    sample.Looping));
            }

            foreach (NoiseStateEvent noise in timeline.NoiseStates
                .Where(value => sourceIds.Contains(value.VoiceId)
                    && value.EndSample > value.StartSample))
            {
                events.Add(new NoiseActivityEvent(
                    noise.StartSample,
                    noise.EndSample,
                    Math.Clamp(noise.Level, 0, 1)));
            }

            foreach (AggregateHitEvent hit in timeline.AggregateHits
                .Where(value => sourceIds.Contains(value.VoiceId)))
            {
                events.Add(new TriggerEvent(
                    hit.SamplePosition,
                    hit.SamplePosition,
                    hit.SubVoiceId,
                    Math.Clamp(hit.Strength, 0, 1)));
            }

            if (descriptor.IsPercussion)
            {
                foreach (RhythmEvent hit in timeline.Rhythm.Where(value =>
                    sourceIds.Contains(value.ChannelId)
                    || string.Equals(value.ParentVoiceId, descriptor.Id, StringComparison.Ordinal)))
                {
                    events.Add(new TriggerEvent(
                        hit.SamplePosition,
                        hit.SamplePosition,
                        hit.Voice,
                        Math.Clamp(hit.Strength, 0, 1)));
                }
            }

            events.Sort(CompareEvents);
            tracks[index] = new VisualizationSemanticTrack(
                descriptor,
                events.ToArray(),
                GetStatistics(descriptor, events));
        }

        SectionMarkerEvent[] sections = Array.Empty<SectionMarkerEvent>();
        BeatMarkerEvent[] beats = timeline.Beats
            .Where(value => value.SamplePosition >= timeline.StartSample
                && value.SamplePosition <= timeline.EndSample
                && double.IsFinite(value.BeatIndex))
            .OrderBy(value => value.SamplePosition)
            .Select(value => new BeatMarkerEvent(
                value.SamplePosition,
                value.BeatIndex,
                Authoritative: true))
            .ToArray();
        return new VisualizationSemanticScene(tracks, sections, beats);
    }

    private static VisualizationTrackActivityStatistics GetStatistics(
        VisualizationTrackDescriptor descriptor,
        IReadOnlyList<VisualizationSemanticEvent> events)
    {
        double[] pitches = events
            .OfType<PitchedNoteEvent>()
            .SelectMany(note => new[] { note.Midi })
            .Concat(events.OfType<PitchCurveEvent>().SelectMany(curve =>
                curve.Points.Select(point => point.Midi)))
            .Where(double.IsFinite)
            .ToArray();
        long active = events
            .Where(value => value.EndSample > value.StartSample)
            .Sum(value => Math.Max(0, value.EndSample - value.StartSample));
        int instrumentChanges = events.OfType<InstrumentChangeEvent>().Count();
        int percussionHits = events.OfType<TriggerEvent>().Count();
        double confidence = events.Count == 0
            ? 0
            : events.Average(value => Math.Clamp(value.Confidence, 0, 1));
        double density = active > 0
            ? events.Count / (double)active
            : events.Count;
        return new VisualizationTrackActivityStatistics(
            events.Count,
            active,
            density,
            pitches.Length == 0 ? null : pitches.Min(),
            pitches.Length == 0 ? null : pitches.Max(),
            confidence,
            instrumentChanges,
            percussionHits,
            descriptor.SupportsEnergy);
    }

    private static int CompareEvents(
        VisualizationSemanticEvent left,
        VisualizationSemanticEvent right)
    {
        int compare = left.StartSample.CompareTo(right.StartSample);
        if (compare != 0)
            return compare;
        compare = string.CompareOrdinal(left.GetType().Name, right.GetType().Name);
        if (compare != 0)
            return compare;
        compare = left.EndSample.CompareTo(right.EndSample);
        if (compare != 0)
            return compare;

        return (left, right) switch
        {
            (PitchedNoteEvent a, PitchedNoteEvent b) =>
                Compare(a.Midi, b.Midi, a.InstrumentId, b.InstrumentId),
            (TriggerEvent a, TriggerEvent b) =>
                Compare(a.Strength, b.Strength, a.RowId, b.RowId),
            (SamplePlaybackSemanticEvent a, SamplePlaybackSemanticEvent b) =>
                Compare(a.Midi ?? double.NegativeInfinity, b.Midi ?? double.NegativeInfinity,
                    a.SampleId, b.SampleId),
            (NoiseActivityEvent a, NoiseActivityEvent b) => a.Energy.CompareTo(b.Energy),
            _ => 0,
        };
    }

    private static int Compare(
        double leftValue,
        double rightValue,
        string leftText,
        string rightText)
    {
        int compare = leftValue.CompareTo(rightValue);
        return compare != 0 ? compare : string.CompareOrdinal(leftText, rightText);
    }

    private static bool TryConvertPitch(
        VisualizationTrackDescriptor descriptor,
        double frequencyHz,
        double coordinateValue,
        out double midi)
    {
        PitchCoordinateSystem system = descriptor.PitchSystem == PitchCoordinateSystem.None
            && descriptor.Kind is (VisualizationTrackKind.Pitched
                or VisualizationTrackKind.FmOperatorGroup
                or VisualizationTrackKind.WaveTable)
            ? PitchCoordinateSystem.AbsoluteMidi
            : descriptor.PitchSystem;
        double value = system == PitchCoordinateSystem.FrequencyHz
            ? double.IsFinite(frequencyHz) ? frequencyHz : coordinateValue
            : coordinateValue;
        return PitchCoordinateConverter.TryConvertToMidi(
            system,
            value,
            descriptor.PitchAnchorMidi,
            out midi)
            && midi >= 0;
    }

    private static bool CanAttemptPitch(
        VisualizationTrackDescriptor descriptor,
        NoteEvent note)
    {
        PitchCoordinateSystem system = descriptor.PitchSystem == PitchCoordinateSystem.None
            && descriptor.Kind is (VisualizationTrackKind.Pitched
                or VisualizationTrackKind.FmOperatorGroup
                or VisualizationTrackKind.WaveTable)
            ? PitchCoordinateSystem.AbsoluteMidi
            : descriptor.PitchSystem;
        return system == PitchCoordinateSystem.FrequencyHz
            ? double.IsFinite(note.InitialFrequencyHz) && note.InitialFrequencyHz > 0
            : double.IsFinite(note.InitialMidiNote);
    }

    private readonly record struct PitchCurvePoint(long Sample, double Midi);
}
