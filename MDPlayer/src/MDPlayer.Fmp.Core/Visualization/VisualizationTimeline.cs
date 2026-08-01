using System.Text.Json.Serialization;

namespace Fmp.Core.Visualization;

internal enum VisualizationNoteMode
{
    Fm,
    Fm3Operator,
    SsgTone,
    SsgToneNoise,
    SsgNoise,
    SsgEnvelopeTone,
    SsgEnvelopeToneNoise,
    SsgEnvelopeNoise,
    Pcm,
    Midi,
}

internal sealed record PitchChange(
    [property: JsonPropertyName("sample")] long SamplePosition,
    double FrequencyHz,
    double MidiNote);

internal sealed record NoteEvent(
    [property: JsonPropertyName("voiceId")] string ChannelId,
    long StartSample,
    long EndSample,
    double InitialFrequencyHz,
    double InitialMidiNote,
    string InstrumentId,
    VisualizationNoteMode Mode,
    bool IsRetrigger,
    IReadOnlyList<PitchChange> Pitch);

internal sealed record RhythmEvent(
    string Voice,
    [property: JsonPropertyName("voiceId")] string ChannelId,
    long SamplePosition,
    float Strength,
    float Pan);

internal sealed record Ppz8Event(
    int Channel,
    long StartSample,
    long EndSample,
    int? Bank,
    int? SampleNumber,
    double? FrequencyHz,
    double? MidiNote,
    float Volume,
    float Pan,
    bool IsRetrigger);

internal sealed record AdpcmBEvent(
    long StartSample,
    long EndSample,
    int StartAddress,
    int EndAddress,
    int DeltaN,
    double? FrequencyHz,
    float Level,
    float Pan,
    bool IsRetrigger);

internal enum LoopMarkerKind
{
    Start,
    Restart,
}

internal sealed record DriverTimingEvent(
    long SamplePosition,
    int TimerBValue,
    double? ValidatedBpm);

internal sealed record TimingEvent(
    long SamplePosition,
    int TimerB,
    double? BeatsPerMinute);

internal sealed record BeatEvent(
    [property: JsonPropertyName("sample")] long SamplePosition,
    [property: JsonPropertyName("beat")] double BeatIndex);

internal sealed record LoopEvent(
    [property: JsonPropertyName("sample")] long SamplePosition,
    LoopMarkerKind Kind,
    int Iteration);

internal sealed record LoopMarker(
    long SamplePosition,
    LoopMarkerKind Kind,
    int Iteration);

internal sealed record FmOperatorDefinition(
    int AttackRate,
    int DecayRate,
    int SustainRate,
    int ReleaseRate,
    int SustainLevel,
    int TotalLevel,
    int KeyScale,
    int Multiple,
    int Detune,
    bool AmplitudeModulation,
    int SsgEnvelope);

internal record InstrumentDefinition(
    string Id,
    string Kind,
    int? Algorithm,
    int? Feedback,
    int? Ams,
    int? Fms,
    IReadOnlyList<FmOperatorDefinition> Operators);

internal sealed class VisualizationTimeline
{
    public int SchemaVersion { get; init; } = 2;
    public int SampleRate { get; init; }
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public TrackMetadata Source { get; init; }
    public string StopReason { get; init; } = "";
    public IReadOnlyList<DeviceDescriptor> Devices { get; init; } = Array.Empty<DeviceDescriptor>();
    public IReadOnlyList<VoiceDescriptor> Voices { get; init; } = Array.Empty<VoiceDescriptor>();
    public IReadOnlyList<NoteEvent> Notes { get; init; } = Array.Empty<NoteEvent>();
    public IReadOnlyList<RhythmEvent> Rhythm { get; init; } = Array.Empty<RhythmEvent>();
    public Ppz8Event[] Ppz8 { get; init; } = [];
    public AdpcmBEvent[] AdpcmB { get; init; } = [];
    public DriverTimingEvent[] Timing { get; init; } = [];
    public BeatEvent[] Beats { get; init; } = [];
    public LoopMarker[] LoopMarkers { get; init; } = [];
    public IReadOnlyList<InstrumentDefinition> Instruments { get; init; } = Array.Empty<InstrumentDefinition>();
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
