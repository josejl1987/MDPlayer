using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Core.Decoding.SnesDsp;

namespace Fmp.Core.Visualization;

/// <summary>
/// Chip families understood by the visualization layer. Format and driver
/// names deliberately do not appear here: a decoder is selected by the
/// resulting device, not by the input extension.
/// </summary>
internal enum ChipType
{
    Unknown,
    Ym2203,
    Ym2608,
    Ym2610,
    Ym2612,
    Ym2151,
    Ym2413,
    Ym3526,
    Ym3812,
    Y8950,
    Ymf262,
    Ymf278b,
    Ymz280b,
    SegaPcm,
    Rf5c68,
    Rf5c164,
    C140,
    C352,
    K054539,
    Ga20,
    Okim6258,
    Okim6295,
    MultiPcm,
    Sn76489,
    Ay8910,
    Dmg,
    NesApu,
    K051649,
    Huc6280,
    Midi,
    Ppz8,
    Pcm,
    SnesDsp,
}

internal readonly record struct DeviceId(ChipType Type, int Instance)
{
    public override string ToString() =>
        $"{Type.ToString().ToLowerInvariant()}.{Instance}";

    public static bool TryParse(string value, out DeviceId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !Enum.TryParse(parts[0], ignoreCase: true, out ChipType type)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int instance)
            || instance < 0)
            return false;

        id = new DeviceId(type, instance);
        return true;
    }
}

internal enum VoiceKind
{
    Fm,
    Fm3Operator,
    Ssg,
    Psg,
    Rhythm,
    Adpcm,
    Pcm,
    MidiChannel,
    Pulse,
    Triangle,
    Noise,
    Wavetable,
    Dpcm,
    Aggregate,
    PcmVoice,
}

internal readonly record struct VoiceId(
    DeviceId Device,
    VoiceKind Kind,
    int Index,
    int SubIndex = 0,
    string Name = null)
{
    public override string ToString()
    {
        if (Kind == VoiceKind.Rhythm && string.Equals(Name, "rhythm", StringComparison.Ordinal))
            return $"{Device}.rhythm";
        if (Kind == VoiceKind.Adpcm && string.Equals(Name, "adpcm-b", StringComparison.Ordinal))
            return $"{Device}.adpcm-b";
        if (Kind == VoiceKind.Pcm
            && Device.Type == ChipType.Ppz8
            && string.Equals(Name, "ppz8", StringComparison.Ordinal))
            return Device.ToString();

        string kind = Kind switch
        {
            VoiceKind.Fm3Operator => "fm3.op",
            VoiceKind.MidiChannel => "channel",
            _ => Kind.ToString().ToLowerInvariant(),
        };

        string suffix = string.IsNullOrEmpty(Name)
            ? (Index + 1).ToString(CultureInfo.InvariantCulture)
            : Name;
        return $"{Device}.{kind}.{suffix}";
    }
}

[Flags]
internal enum DeviceCapabilities
{
    None = 0,
    Notes = 1 << 0,
    ContinuousPitch = 1 << 1,
    Instruments = 1 << 2,
    PercussionVoices = 1 << 3,
    VoiceMasking = 1 << 4,
    NativeVoiceAudio = 1 << 5,
    ParallelSynthesis = 1 << 6,
    SampleIdentity = 1 << 7,
    Pan = 1 << 8,
    Expression = 1 << 9,
}

internal enum VoicePresentationKind
{
    Pitched,
    Fm,
    Fm3,
    Psg,
    Ssg = Psg,
    Noise,
    Percussion,
    Rhythm = Percussion,
    Pcm,
    PcmVoice = Pcm,
    Midi,
    Wavetable,
    Aggregate,
}

internal enum ScopeSupport
{
    None,
    Master,
    Device,
    Channel,
}

internal sealed record DeviceDescriptor(
    DeviceId Id,
    string DisplayName,
    long ClockHz,
    DeviceCapabilities Capabilities,
    ScopeSupport ScopeSupport = ScopeSupport.None)
{
    public ChipType Type => Id.Type;
    public int Instance => Id.Instance;
}

internal sealed record VoiceDescriptor(
    VoiceId Id,
    string DisplayName,
    VoicePresentationKind Presentation,
    int Order,
    bool IsPercussion,
    bool IsNoise,
    bool SupportsPitch,
    IReadOnlyList<VisualizationRowDescriptor> Rows = null)
{
    public DeviceId DeviceId => Id.Device;
    public VoiceKind Kind => Id.Kind;
    public int Index => Id.Index;
    public string Label => DisplayName;

    /// <summary>
    /// Coordinate system supplied by the decoder or presentation adapter.
    /// Normal pitched voices are normalized to absolute MIDI by default; a
    /// relative-only voice must opt in explicitly so it cannot accidentally be
    /// merged into an absolute unified roll.
    /// </summary>
    public PitchCoordinateSystem PitchSystem { get; init; }
        = SupportsPitch
            ? PitchCoordinateSystem.AbsoluteMidi
            : PitchCoordinateSystem.None;

    /// <summary>
    /// Optional anchor for relative pitch systems. It is kept on the semantic
    /// descriptor rather than inferred by rendering code.
    /// </summary>
    public double? RelativePitchAnchorMidi { get; init; }

    /// <summary>
    /// Optional decoder confidence for a lead-role hint. When absent, the
    /// presentation builder derives a bounded salience score from activity.
    /// </summary>
    public double? LeadRoleConfidence { get; init; }
}

internal sealed record TrackMetadata(
    string SourceFormat,
    string Title = "",
    string Backend = "",
    string SourceFile = "");

internal readonly record struct CaptureTimingInfo(
    int SampleRate,
    int TimingResolutionSamples)
{
    public CaptureTimingInfo Validate()
    {
        if (SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (TimingResolutionSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(TimingResolutionSamples));
        return this;
    }
}

internal readonly record struct TimedChipWrite(
    long SamplePosition,
    DeviceId Device,
    int Port,
    int Address,
    int Data,
    long? DacSourceOffset = null)
{
    public TimedChipWrite Validate()
    {
        if (SamplePosition < 0)
            throw new ArgumentOutOfRangeException(nameof(SamplePosition));
        if (Port < 0)
            throw new ArgumentOutOfRangeException(nameof(Port));
        if (Address < 0)
            throw new ArgumentOutOfRangeException(nameof(Address));
        if (Data < 0 || (Device.Type != ChipType.Ppz8 && Data > 0xFFFF))
            throw new ArgumentOutOfRangeException(nameof(Data));
        return this;
    }
}

internal enum Ym2612DacStreamControlKind
{
    Start,
    Stop,
    NaturalEnd,
    Retrigger,
    RateChanged,
}

internal readonly record struct TimedYm2612DacStreamControl(
    long SamplePosition,
    DeviceId Device,
    byte StreamId,
    Ym2612DacStreamControlKind Kind,
    double? RateHz = null);

internal enum MidiMessageType
{
    NoteOn,
    NoteOff,
    ControlChange,
    ProgramChange,
    PitchBend,
    ChannelPressure,
    PolyphonicAftertouch,
    SysEx,
}

internal readonly record struct TimedMidiMessage(
    long SamplePosition,
    DeviceId Device,
    int Port,
    int Channel,
    MidiMessageType Type,
    int Data1,
    int Data2)
{
    public TimedMidiMessage Validate()
    {
        if (SamplePosition < 0)
            throw new ArgumentOutOfRangeException(nameof(SamplePosition));
        if (Channel is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(Channel));
        return this;
    }
}

internal enum AssetKind
{
    SampleBank,
    Pcm,
    Adpcm,
    AdpcmA,
    AdpcmB,
    DriverData,
}

internal readonly record struct TimedSampleAssetEvent(
    long SamplePosition,
    DeviceId Device,
    string AssetId,
    AssetKind Kind,
    long SizeBytes);

internal readonly record struct TimedLoopBoundary(
    long SamplePosition,
    int LoopIndex);

internal interface IPlaybackEventSink
{
    void OnDevice(in DeviceDescriptor device);
    void OnChipWrite(in TimedChipWrite write);
    void OnYm2612DacStreamControl(in TimedYm2612DacStreamControl control) { }
    void OnMidi(in TimedMidiMessage message);
    void OnSampleAsset(in TimedSampleAssetEvent asset);
    void OnLoopBoundary(in TimedLoopBoundary loop);

    /// <summary>Optional decoded sample-bank snapshot for generic asset extraction.</summary>
    void OnSampleBank(DeviceId device, int bank, ReadOnlyMemory<byte>[] samples, long samplePosition) { }

    /// <summary>
    /// SPC-only semantic event stream (§9.2): the native S-DSP core emits
    /// effective transitions (KEY_ON, RELEASE, PITCH, SOURCE, ...) that the
    /// <see cref="SnesDspTimelineDecoder"/> consumes directly. Default no-op
    /// so non-SPC sinks and adapters are unaffected.
    /// </summary>
    void OnSpcEvent(in SpcSemanticEvent @event) { }

    /// <summary>
    /// SPC-only instrument/root snapshot (§25.3): per-source BRR root estimates
    /// resolved at open time, consumed by <see cref="SnesDspTimelineDecoder"/>
    /// to place notes at their sounding pitch. Default no-op.
    /// </summary>
    void OnSpcInstruments(IReadOnlyList<SpcSourceRootInfo> sources) { }

    /// <summary>SPC-only decoded BRR assets for generic PCM visualization.</summary>
    void OnSpcSamples(IReadOnlyList<SpcSampleEntry> samples) { }

    /// <summary>Optional grouped hit event for aggregate presentations.</summary>
    void OnAggregateHit(in AggregateHitEvent hit) { }
}

/// <summary>
/// Per-source BRR root estimate (§25.3). A null <see cref="EstimatedRootHz"/>
/// means the sample is unpitched or was not estimated; consumers then fall
/// back to the A4-relative anchor.
/// </summary>
internal sealed record SpcSourceRootInfo(
    int SourceNumber,
    double? EstimatedRootHz,
    double Confidence,
    string Accuracy);

internal interface IChipTimelineDecoder
{
    ChipType ChipType { get; }

    void Initialize(DeviceDescriptor device, TimelineBuilder timeline);
    void Process(in TimedChipWrite write);
    void Complete(long endSample);
}

internal interface IMidiTimelineDecoder
{
    void Initialize(DeviceDescriptor device, TimelineBuilder timeline);
    void Process(in TimedMidiMessage message);
    void Complete(long endSample);
}

internal sealed record RequiredAsset(
    string Name,
    AssetKind Kind,
    bool Required,
    IReadOnlyList<string> SearchNames);

internal sealed record PlaybackProbeResult(
    bool Supported,
    string Format,
    IReadOnlyList<RequiredAsset> RequiredAssets,
    IReadOnlyList<string> MissingAssets,
    IReadOnlyList<string> Warnings)
{
    public bool Visualizable { get; init; }
    public bool Portable { get; init; }
    public PlaybackAvailability Availability { get; init; } = PlaybackAvailability.Unavailable;

    /// <summary>Native sample rate of the backend's output (0 or default = the
    /// playback environment sample rate). The generic CLI uses this to size
    /// the timeline timebase (§2.3).</summary>
    public int NativeSampleRate { get; init; }
}

internal enum PlaybackAvailability
{
    Unavailable,
    AssetMissing,
    PlatformSpecific,
    Available,
}

internal sealed record PlaybackEnvironment(
    IReadOnlyList<string> SearchPaths,
    bool OfflineOnly = true,
    int SampleRate = 44_100);

/// <summary>
/// SPC pitch handling mode (§25.3). <see cref="Estimate"/> (default) runs the
/// PR 9 BRR root estimator so instruments carry an estimated musical root;
/// <see cref="Relative"/> is a DIAGNOSTIC option that skips root estimation —
/// instruments keep only the relative S-DSP pitch (no EstimatedRootHz).
/// Non-SPC backends ignore the value; the renderer never branches on it (§4).
/// </summary>
internal enum SpcPitchMode
{
    Estimate = 0,
    Relative = 1,
}

internal sealed record PlaybackOptions(
    int LoopCount = 2,
    double FadeSeconds = 5.0,
    double TailSeconds = 0.5,
    double? MaxDurationSeconds = null,
    string OutputAudioPath = null,
    int SampleRate = 44_100,
    bool WriteSpcStems = false,
    SpcPitchMode SpcPitchMode = SpcPitchMode.Estimate,
    double SsgGainDb = 0);

/// <summary>
/// Backend boundary. Implementations know formats, drivers and asset lookup;
/// the visualization model only receives normalized playback events.
/// </summary>
internal interface IPlaybackBackend
{
    string Id { get; }

    PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment);

    IPlaybackCaptureSession Open(
        FileInfo input,
        PlaybackOptions options,
        IPlaybackEventSink eventSink);
}

internal interface IPlaybackCaptureSession : IDisposable
{
    CaptureTimingInfo Timing { get; }
    IReadOnlyList<DeviceDescriptor> Devices { get; }
    long SamplePosition { get; }
    bool IsComplete { get; }

    void Run(CancellationToken cancellationToken = default);
    void Stop();
}

/// <summary>
/// JSON uses the stable string form for IDs while the in-memory model keeps
/// strongly typed device and voice identity.
/// </summary>
internal sealed class DeviceIdJsonConverter : JsonConverter<DeviceId>
{
    public override DeviceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !DeviceId.TryParse(reader.GetString(), out DeviceId id))
            throw new JsonException("Invalid device id.");
        return id;
    }

    public override void Write(Utf8JsonWriter writer, DeviceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class VoiceIdJsonConverter : JsonConverter<VoiceId>
{
    public override VoiceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Voice id must be a string.");
        string value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("Voice id is empty.");

        string[] parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !DeviceId.TryParse(parts[0] + "." + parts[1], out DeviceId device))
            throw new JsonException("Invalid voice id.");

        if (parts.Length == 2 && device.Type == ChipType.Ppz8)
            return new VoiceId(device, VoiceKind.Pcm, 0, 0, "ppz8");
        if (parts.Length < 3)
            throw new JsonException("Invalid voice id.");

        string kindText = parts[2];
        string suffix = parts.Length >= 4 ? parts[^1] : kindText;
        VoiceKind kind = kindText switch
        {
            "fm3" when parts.Length >= 5 && parts[3] == "op" => VoiceKind.Fm3Operator,
            "channel" => VoiceKind.MidiChannel,
            "rhythm" => VoiceKind.Rhythm,
            "adpcm-b" => VoiceKind.Adpcm,
            "pcm" => VoiceKind.Pcm,
            _ when Enum.TryParse(kindText, ignoreCase: true, out VoiceKind parsed) => parsed,
            _ => throw new JsonException("Invalid voice kind."),
        };

        int index = 0;
        string name = null;
        if (int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            index = Math.Max(0, numeric - 1);
        else
            name = suffix;
        return new VoiceId(device, kind, index, 0, name);
    }

    public override void Write(Utf8JsonWriter writer, VoiceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
