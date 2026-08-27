using System.Text.Json.Serialization;

namespace Fmp.Core.Visualization;

/// <summary>
/// Internal normalization boundary for YM2612 DAC playback analysis.
///
/// The VGM-specific decoder translates low-level DAC operations into this
/// compact operation set; the <see cref="DacPlaybackTracker"/> consumes it and
/// reconstructs semantically meaningful playback sessions. Timestamps are
/// expressed in the project's established time unit (audio sample positions).
/// </summary>
internal abstract record DacOperation(long Timestamp)
{
    internal sealed record DacSourceDefined(
        long Timestamp,
        int SourceId,
        ReadOnlyMemory<byte> Data,
        DacSampleFormat Format)
        : DacOperation(Timestamp);

    internal sealed record DacSourcePositionChanged(
        long Timestamp,
        int SourceId,
        long Position)
        : DacOperation(Timestamp);

    internal sealed record DacPlaybackStarted(
        long Timestamp,
        int SourceId,
        long Position,
        long? DeclaredLength,
        double? RateHz)
        : DacOperation(Timestamp);

    internal sealed record DacByteConsumed(
        long Timestamp,
        int SourceId,
        long Position,
        byte Value,
        long? SourceOffset = null)
        : DacOperation(Timestamp);

    internal sealed record DacRateChanged(
        long Timestamp,
        double RateHz)
        : DacOperation(Timestamp);

    internal sealed record DacPlaybackStopped(
        long Timestamp,
        DacStopReason Reason)
        : DacOperation(Timestamp);

    internal sealed record DacDecoderReset(long Timestamp)
        : DacOperation(Timestamp);
}

/// <summary>Sample-data encoding families understood by DAC playback analysis.</summary>
internal enum DacEncoding
{
    Pcm,
}

/// <summary>Whether PCM payload bytes represent signed or unsigned amplitude.</summary>
internal enum DacSignedness
{
    Unsigned,
    Signed,
}

/// <summary>
/// The explicit interpretation of an extracted PCM payload. Two payloads are
/// only the same sample asset when both bytes and this interpretation match.
/// </summary>
internal sealed record DacSampleFormat(
    DacEncoding Encoding,
    int BitsPerSample,
    DacSignedness Signedness,
    int ChannelCount)
{
    public static readonly DacSampleFormat DefaultYm2612 = new(
        DacEncoding.Pcm,
        BitsPerSample: 8,
        DacSignedness.Unsigned,
        ChannelCount: 1);

    public string StableKey => $"{Encoding}:{BitsPerSample}:{Signedness}:{ChannelCount}";
}

/// <summary>Ends an individual DAC playback instance.</summary>
internal enum DacStopReason
{
    NaturalEnd,
    ExplicitStop,
    Retriggered,
    SourceChanged,
    SourceDiscontinuity,
    DecoderReset,
    EndOfStream,
    TruncatedInput,
}

/// <summary>A single playback-rate observation at an exact timestamp.</summary>
internal sealed record DacRatePoint(
    long Timestamp,
    double RateHz);
