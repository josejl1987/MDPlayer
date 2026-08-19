namespace Fmp.Core.Visualization;

/// <summary>
/// Identifies the logical DAC sample source (e.g. a VGM data block or an
/// implicit register-stream byte source) that produced a playback session.
/// </summary>
internal sealed record DacSourceReference(
    int SourceId,
    long StartPosition,
    long EndPosition);

/// <summary>
/// A completed playback session reduced to a candidate sample payload plus the
/// metadata needed for canonical deduplication. The payload contains only the
/// PCM bytes actually consumed by that session; it never includes rate, timing,
/// volume, offsets, container headers, or VGM command bytes.
/// </summary>
internal sealed record DacSampleCandidate(
    ReadOnlyMemory<byte> Payload,
    DacSampleFormat Format,
    long FirstTimestamp,
    DacSourceReference Source,
    long? DeclaredLength,
    bool WasTruncated);

/// <summary>
/// One semantic playback event produced for a single DAC trigger. Every
/// trigger yields exactly one of these, regardless of whether it reuses the
/// payload and identity of an earlier sample.
///
/// <paramref name="SampleId"/> is the stable canonical identity assigned by the
/// sample catalog in a later phase; <see cref="StartSample"/> and
/// <see cref="EndSample"/> use the project's established timeline time unit.
/// </summary>
internal sealed record DacPlaybackEvent(
    long InstanceId,
    int? AssetId,
    string SampleId,
    long StartSample,
    long EndSample,
    DacStopReason StopReason,
    int BytesConsumed,
    bool WasTruncated,
    double? InitialRateHz,
    IReadOnlyList<DacRatePoint> RatePoints,
    double? Gain,
    double? Pan,
    bool WasImplicit = false);

/// <summary>A structured diagnostic for malformed or unsupported DAC input.</summary>
internal sealed record DacDiagnostic(
    long Timestamp,
    string Code,
    string Message)
{
    public override string ToString() => $"{Code} @ {Timestamp}: {Message}";
}

/// <summary>
/// A canonical, deduplicated DAC sample asset (spec §12). One instance exists
/// per unique (format, payload) pair; playback events reference it by
/// <see cref="DacPlaybackEvent.AssetId"/> rather than retaining a PCM copy.
/// </summary>
internal sealed record DacSampleAsset
{
    public required int AssetId { get; init; }
    public required string StableName { get; init; }
    public required DacSampleFormat Format { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public required DacHash256 ContentHash { get; init; }

    public required long FirstUseTimestamp { get; init; }
    public required int FirstUseSequence { get; init; }

    public required int DisplayBank { get; init; }
    public required int DisplayNote { get; init; }

    public int TriggerCount { get; init; }
    public IReadOnlyList<DacSourceReference> Sources { get; init; }
        = Array.Empty<DacSourceReference>();

    /// <summary>Timeline <see cref="SampleDefinition.Id"/> key for this asset.</summary>
    public string TimelineSampleId => $"dac:{AssetId}";
}
