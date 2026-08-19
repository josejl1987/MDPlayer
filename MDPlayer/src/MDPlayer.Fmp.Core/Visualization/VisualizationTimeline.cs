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

/// <summary>Authoritative source ownership. It is never inferred from display names.</summary>
internal readonly record struct SourceDomainKey(DeviceId Device, VoiceKind VoiceFamily, int Index)
{
    public override string ToString() => $"{Device}.{VoiceFamily}:{Index}";
}

internal sealed record NoteEvent(
    [property: JsonPropertyName("voiceId")] string ChannelId,
    long StartSample,
    long EndSample,
    double InitialFrequencyHz,
    double InitialMidiNote,
    string InstrumentId,
    VisualizationNoteMode Mode,
    bool IsRetrigger,
    IReadOnlyList<PitchChange> Pitch)
{
    public SourceDomainKey? Domain { get; init; }
}

internal sealed record RhythmEvent(
    string Voice,
    [property: JsonPropertyName("voiceId")] string ChannelId,
    long SamplePosition,
    float Strength,
    float Pan,
    [property: JsonPropertyName("parentVoiceId")] string ParentVoiceId = null,
    string InstrumentId = "")
{
    public SourceDomainKey? Domain { get; init; }
}

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

/// <summary>Generic cyclic waveform asset used by wavetable presentations.</summary>
internal sealed record WaveformDefinition(
    string Id,
    string Family,
    int SourceLength,
    float[] Preview,
    string DisplayName);

internal sealed record WaveformChangeEvent(
    string VoiceId,
    long SamplePosition,
    string WaveformId);

internal enum SampleLoopMode
{
    None,
    Forward,
    PingPong,
    Unknown,
}

internal enum AssetIdentityKind
{
    ContentHash,
    BankAndSlot,
    RuntimeHandle,
}

internal sealed record WaveformEnvelopePoint(float Minimum, float Maximum);

/// <summary>Generic bounded sample asset. Raw sample bytes never enter a timeline.</summary>
internal sealed record SampleDefinition(
    string Id,
    string Family,
    int SourceLengthSamples,
    int? NativeSampleRate,
    int? LoopStart,
    int? LoopEnd,
    SampleLoopMode LoopMode,
    WaveformEnvelopePoint[] Preview,
    string DisplayName)
{
    public AssetIdentityKind IdentityKind { get; init; } = AssetIdentityKind.ContentHash;
}

internal sealed record SamplePlaybackEvent(
    string VoiceId,
    long StartSample,
    long EndSample,
    string SampleId,
    double? MidiPitch,
    double PlaybackRate,
    float Gain,
    float Pan,
    bool Retrigger,
    bool Looping);

/// <summary>Authoritative S-DSP voice-state transition retained for SPC panels.</summary>
internal sealed record SpcVoiceStateEvent(
    string VoiceId,
    long SamplePosition,
    string State,
    int Value,
    int Value2);

internal enum NoiseMode
{
    White,
    Periodic,
    ShortPeriod,
    LongPeriod,
    HardwareDefined,
    Unknown,
}

internal sealed record NoiseStateEvent(
    string VoiceId,
    long StartSample,
    long EndSample,
    double? CentreFrequencyHz,
    double? Period,
    float Level,
    NoiseMode Mode);

internal sealed record AggregateHitEvent(
    string VoiceId,
    string SubVoiceId,
    string Label,
    long SamplePosition,
    float Strength,
    float Pan,
    string AssetId);

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

/// <summary>
/// A driver-observed beat at an absolute sample. The <c>BeatIndex</c> unit is the
/// MIDI quarter note (locked producer-boundary convention, FR-009): an increment
/// of 1.0 equals exactly one quarter note, scaled by
/// <c>MusicalTimeMapOptions.QuartersPerBeat</c> (default 1.0) when the driver beat
/// unit differs. The scale is applied exactly once — in
/// <c>MusicalTimeMapBuilder.BuildAnchors</c> (quarter = BeatIndex * QuartersPerBeat);
/// MIDI serialization performs no further conversion. Fractional (non-integer)
/// BeatIndex values are legal; non-finite values are filtered at the boundary.
/// Serialized as <c>{"sample": N, "beat": I}</c>. Producers:
/// <c>TimelineBuilder.AddBeat</c> (programmatic) and <c>TimelineBuilder.Merge</c>
/// (serialized-JSON replay — clock-normalized, BeatIndex preserved); tests
/// construct these records directly as fixtures.
/// </summary>
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
    public WaveformDefinition[] Waveforms { get; init; } = [];
    public SampleDefinition[] Samples { get; init; } = [];
    public WaveformChangeEvent[] WaveformChanges { get; init; } = [];
    public SamplePlaybackEvent[] SamplePlayback { get; init; } = [];
    public SpcVoiceStateEvent[] SpcVoiceStates { get; init; } = [];
    public NoiseStateEvent[] NoiseStates { get; init; } = [];
    public AggregateHitEvent[] AggregateHits { get; init; } = [];
    public DriverTimingEvent[] Timing { get; init; } = [];
    public BeatEvent[] Beats { get; init; } = [];
    public LoopMarker[] LoopMarkers { get; init; } = [];
    public IReadOnlyList<InstrumentDefinition> Instruments { get; init; } = Array.Empty<InstrumentDefinition>();
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
