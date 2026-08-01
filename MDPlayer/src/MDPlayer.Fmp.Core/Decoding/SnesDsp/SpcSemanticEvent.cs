namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>
/// Kinds of effective S-DSP transitions emitted by the native SPC core
/// (PR 2/3) and consumed by the managed timeline decoder (PR 4).
/// </summary>
internal enum SpcSemanticEventKind
{
    KeyOn,
    ReleaseStart,
    VoiceEnd,
    SourceLatched,
    PitchChanged,
    VolumeChanged,
    EnvelopeModeChanged,
    NoiseChanged,
    PitchModChanged,
    EchoSendChanged,
    SampleLooped,
    GlobalStateChanged,
}

/// <summary>
/// A single effective S-DSP state transition (§9.2/§12). The native core only
/// emits transitions that actually change the audible/musical state, so the
/// managed decoder treats each event as authoritative. <see cref="Value"/> and
/// <see cref="Value2"/> carry kind-specific payloads (source number, volume,
/// envelope mode, ...); <see cref="EffectivePitch"/> carries the 14-bit S-DSP
/// pitch register for pitch-bearing events.
/// </summary>
internal readonly record struct SpcSemanticEvent(
    long SamplePosition,
    int Voice,
    SpcSemanticEventKind Kind,
    int Value = 0,
    int Value2 = 0,
    ushort EffectivePitch = 0)
{
    /// <summary>Pitch register value that plays the source at its natural rate (§13.3).</summary>
    public const ushort UnityPitch = 0x1000;

    public static SpcSemanticEvent KeyOn(long samplePosition, int voice, int sourceNumber = 0, ushort effectivePitch = UnityPitch) =>
        new(samplePosition, voice, SpcSemanticEventKind.KeyOn, sourceNumber, 0, effectivePitch);

    public static SpcSemanticEvent ReleaseStart(long samplePosition, int voice) =>
        new(samplePosition, voice, SpcSemanticEventKind.ReleaseStart);

    public static SpcSemanticEvent VoiceEnd(long samplePosition, int voice) =>
        new(samplePosition, voice, SpcSemanticEventKind.VoiceEnd);

    public static SpcSemanticEvent SourceLatched(long samplePosition, int voice, int sourceNumber) =>
        new(samplePosition, voice, SpcSemanticEventKind.SourceLatched, sourceNumber);

    public static SpcSemanticEvent PitchChanged(long samplePosition, int voice, ushort effectivePitch) =>
        new(samplePosition, voice, SpcSemanticEventKind.PitchChanged, 0, 0, effectivePitch);

    public static SpcSemanticEvent VolumeChanged(long samplePosition, int voice, int volume) =>
        new(samplePosition, voice, SpcSemanticEventKind.VolumeChanged, volume);

    public static SpcSemanticEvent EnvelopeModeChanged(long samplePosition, int voice, int envelopeMode) =>
        new(samplePosition, voice, SpcSemanticEventKind.EnvelopeModeChanged, envelopeMode);

    public static SpcSemanticEvent GlobalStateChanged(long samplePosition, int stateMask) =>
        new(samplePosition, 0, SpcSemanticEventKind.GlobalStateChanged, stateMask);
}
