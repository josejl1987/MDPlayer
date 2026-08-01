namespace Fmp.Core.Rendering;

/// <summary>
/// Writes chunked mono PCM data for scope visualization.
/// Each chunk is independently addressable (time-aligned) so that
/// a scope renderer can seek to any playback position without
/// decoding the full file. Chunks are raw signed-16-bit mono PCM.
/// </summary>
internal class ScopePcmChunkWriter : IDisposable
{
    private readonly string _chunkDir;
    private readonly int _chunkSize;
    private readonly int _sampleRate;
    private readonly string _channelName;
    private short[]? _buffer;
    private int _bufferPos;
    private int _chunkIndex;
    private long _totalSamples;
    private bool _disposed;

    /// <summary>Total samples written so far.</summary>
    public long TotalSamples => _totalSamples;

    /// <summary>Number of chunk files written so far.</summary>
    public int ChunksWritten => _chunkIndex;

    /// <summary>
    /// Creates a chunk writer.
    /// </summary>
    /// <param name="chunkDir">Directory where chunk files (NNNN.pcm) will be written.</param>
    /// <param name="channelName">Channel name (for error messages).</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="chunkDurationSeconds">Target duration of each chunk (default 1.0s).</param>
    public ScopePcmChunkWriter(string chunkDir, string channelName, int sampleRate, double chunkDurationSeconds = 1.0)
    {
        _chunkDir = chunkDir;
        _channelName = channelName;
        _sampleRate = sampleRate;
        _chunkSize = (int)(sampleRate * chunkDurationSeconds);
        _buffer = new short[_chunkSize];
        _bufferPos = 0;
        _chunkIndex = 0;

        Directory.CreateDirectory(chunkDir);
    }

    /// <summary>
    /// Write a block of mono PCM samples.
    /// </summary>
    /// <param name="samples">Array of signed-16-bit mono samples.</param>
    /// <param name="offset">Starting offset in samples.</param>
    /// <param name="count">Number of samples to write.</param>
    public void Write(short[] samples, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScopePcmChunkWriter));
        if (_buffer == null) return; // already finalized

        int pos = offset;
        int remaining = count;

        while (remaining > 0)
        {
            int space = _chunkSize - _bufferPos;
            int take = Math.Min(space, remaining);

            Array.Copy(samples, pos, _buffer!, _bufferPos, take);
            _bufferPos += take;
            pos += take;
            remaining -= take;
            _totalSamples += take;

            if (_bufferPos >= _chunkSize)
                FlushChunk();
        }
    }

    /// <summary>
    /// Flush any buffered samples to a final partial chunk.
    /// </summary>
    public void Flush()
    {
        if (_disposed || _buffer == null) return;
        if (_bufferPos > 0)
            FlushChunk();
        _buffer = null; // mark finalized
    }

    private void FlushChunk()
    {
        string chunkPath = Path.Combine(_chunkDir, $"{_chunkIndex:D4}.pcm");
        byte[] raw = new byte[_bufferPos * 2];
        for (int i = 0; i < _bufferPos; i++)
        {
            short s = _buffer![i];
            raw[i * 2] = (byte)(s & 0xFF);
            raw[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        string partialPath = chunkPath + ".partial";
        File.WriteAllBytes(partialPath, raw);
        if (File.Exists(chunkPath))
            File.Delete(chunkPath);
        File.Move(partialPath, chunkPath);

        _bufferPos = 0;
        _chunkIndex++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
    }

    /// <summary>
    /// Get the PCM chunk file path for a specific sample position.
    /// Returns null if the chunk doesn't exist.
    /// </summary>
    public static string? GetChunkPath(string chunkDir, long samplePosition, int chunkSize)
    {
        int chunkIndex = (int)(samplePosition / chunkSize);
        string path = Path.Combine(chunkDir, $"{chunkIndex:D4}.pcm");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Read a PCM chunk file into a short array.
    /// </summary>
    public static short[] ReadChunk(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        short[] samples = new short[raw.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));
        }
        return samples;
    }
}
