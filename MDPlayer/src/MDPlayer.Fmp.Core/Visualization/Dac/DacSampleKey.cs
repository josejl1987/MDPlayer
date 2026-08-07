namespace Fmp.Core.Visualization;

/// <summary>
/// Canonical deduplication key for a DAC sample asset (spec §11). The identity
/// input is the PCM format plus the payload content hash and length. It must
/// never include source offset, data-block index, file path, playback rate,
/// event timing/duration, volume, pan, trigger order, or loop iteration.
/// </summary>
internal readonly record struct DacSampleKey(
    DacSampleFormat Format,
    int PayloadLength,
    DacHash256 ContentHash)
{
    public static DacSampleKey Create(DacSampleFormat format, ReadOnlyMemory<byte> payload)
        => new(format, payload.Length, DacHash256.Of(payload.Span));

    public override string ToString() =>
        $"{Format.StableKey}:{PayloadLength}:{ContentHash.Hex}";
}