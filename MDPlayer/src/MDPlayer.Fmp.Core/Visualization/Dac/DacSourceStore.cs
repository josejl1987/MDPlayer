namespace Fmp.Core.Visualization;

/// <summary>
/// Reusable source-range representation for DAC sample extraction. Owns the
/// canonical PCM bytes for a DAC source and exposes range reads with checked,
/// overflow-safe arithmetic. Extraction never concatenates per-byte arrays.
/// </summary>
internal sealed class DacSourceStore
{
    private readonly byte[] _data;

    public DacSourceStore(int sourceId, ReadOnlyMemory<byte> data, DacSampleFormat format)
    {
        SourceId = sourceId;
        _data = data.ToArray();
        Format = format;
    }

    public int SourceId { get; }
    public int Length => _data.Length;
    public DacSampleFormat Format { get; }

    /// <summary>
    /// Returns the payload range [start, start+length) as a memory slice.
    /// Returns false when the range is outside the available data, so callers
    /// can emit a truncation diagnostic and preserve whatever partial bytes are
    /// actually available.
    /// </summary>
    public bool TryReadRange(long start, long length, out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        if (start < 0 || length < 0 || start > int.MaxValue)
            return false;
        if (length == 0)
        {
            payload = ReadOnlyMemory<byte>.Empty;
            return true;
        }
        // checked arithmetic: start + length must not overflow and must fit
        if (length > int.MaxValue - start)
            return false;

        int startOffset = (int)start;
        int count = (int)length;
        if (startOffset < 0 || count < 0 || startOffset > _data.Length - count)
            return false;

        payload = new ReadOnlyMemory<byte>(_data, startOffset, count);
        return true;
    }

    /// <summary>Number of bytes that exist in the source at and after <paramref name="start"/>.</summary>
    public int AvailableFrom(long start)
    {
        if (start < 0 || start >= _data.Length)
            return 0;
        return _data.Length - (int)start;
    }
}
