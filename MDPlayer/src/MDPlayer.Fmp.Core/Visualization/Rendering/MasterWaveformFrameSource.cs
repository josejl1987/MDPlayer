using System.Buffers.Binary;
using System.IO;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Internal scope grid source that draws the captured master waveform into
/// every scope cell. It is used when the external Corrscope/Python bridge is
/// unavailable, so that final video, GUI preview and visual review all render
/// identical scope regions. Previously the FFmpeg-only master-waveform overlay
/// covered final output while preview left the scope holes transparent — a
/// preview/final parity violation.
///
/// The frame's audio window (one video frame at the overlay's fractional frame
/// rate) is read once from the master WAV and each grid cell renders a
/// min/max column-waveform from it, matching the FFmpeg showwaves cline look
/// that the branch previously used as its fallback.
/// </summary>
internal sealed class MasterWaveformFrameSource : IScopeFrameSource
{
    // Same color the branch used for the FFmpeg master-waveform fallback.
    private static readonly byte[] Color = { 0x7A, 0xA4, 0xFF, 0x70 };

    private readonly PanelOverlayRenderer _overlay;
    private readonly int _sampleRate;
    private readonly int _fpsNumerator;
    private readonly int _fpsDenominator;
    private readonly int _channels;
    private readonly long _dataStart;
    private readonly long _totalSamples;
    private readonly int _gridWidth;
    private readonly int _gridHeight;
    private readonly FileStream _stream;
    private readonly int _bucketSize;
    private readonly int[] _minimums;
    private readonly int[] _maximums;

    private MasterWaveformFrameSource(
        PanelOverlayRenderer overlay,
        FileStream stream,
        int sampleRate,
        int fpsNumerator,
        int fpsDenominator,
        int channels,
        long dataStart,
        long totalSamples,
        int gridWidth,
        int gridHeight)
    {
        _overlay = overlay;
        _sampleRate = sampleRate;
        _fpsNumerator = fpsNumerator;
        _fpsDenominator = fpsDenominator;
        _channels = channels;
        _dataStart = dataStart;
        _totalSamples = totalSamples;
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;
        _stream = stream;
        long visibleSamples = Math.Max(1, (long)Math.Round(overlay.Layout.WindowSeconds * sampleRate));
        long targetBuckets = Math.Max(1, (long)gridWidth * 2);
        _bucketSize = (int)Math.Clamp(
            (visibleSamples + targetBuckets - 1) / targetBuckets, 1, int.MaxValue);
        int bucketCount = checked((int)((totalSamples + _bucketSize - 1) / _bucketSize));
        _minimums = new int[bucketCount];
        _maximums = new int[bucketCount];
        BuildPeakEnvelope();
    }

    /// <summary>
    /// Parses the master WAV (signed 16-bit PCM, mono or stereo, produced by
    /// <see cref="Fmp.Core.Audio.WavWriter"/> or playback capture) and returns
    /// a ready source, or null when the file is missing or not in the
    /// supported format. Never throws. The RIFF chunk list is walked to find
    /// the <c>fmt </c> and <c>data</c> chunks, so valid third-party WAVs
    /// (including WAVE_FORMAT_EXTENSIBLE, JUNK/LIST chunks and non-44-byte
    /// headers) are parsed correctly instead of being misread.
    /// </summary>
    public static MasterWaveformFrameSource? TryCreate(
        PanelOverlayRenderer overlay,
        string masterWavPath,
        int fpsNumerator,
        int fpsDenominator)
    {
        if (overlay is null
            || string.IsNullOrWhiteSpace(masterWavPath)
            || !File.Exists(masterWavPath)
            || fpsNumerator <= 0
            || fpsDenominator <= 0)
        {
            return null;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                masterWavPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            Span<byte> riff = stackalloc byte[12];
            if (ReadAtLeast(stream, riff) < 12)
                return null;
            if (riff[0] != (byte)'R' || riff[1] != (byte)'I'
                || riff[2] != (byte)'F' || riff[3] != (byte)'F')
                return null;
            if (riff[8] != (byte)'W' || riff[9] != (byte)'A'
                || riff[10] != (byte)'V' || riff[11] != (byte)'E')
                return null;

            // Walk the RIFF chunk list: fmt (audio format, channels, sample
            // rate, bits per sample) and data (payload offset + size). Chunks
            // are word-aligned.
            int? channels = null;
            int? sampleRate = null;
            short? bitsPerSample = null;
            long? dataStart = null;
            long? dataSize = null;

            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            long offset = 12;
            long fileLength = stream.Length;
            while (offset + 8 <= fileLength)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                if (stream.Read(chunkHeader) < 8)
                    break;
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[..4]);
                int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader[4..8]);
                if (chunkSize < 0)
                    break;

                if (id == 0x20746D66u) // "fmt "
                {
                    if (chunkSize < 16)
                        return null;
                    stream.Seek(offset + 8, SeekOrigin.Begin);
                    if (stream.Read(fmt) < 16)
                        return null;
                    short format = BinaryPrimitives.ReadInt16LittleEndian(fmt[..2]);
                    // PCM or WAVE_FORMAT_EXTENSIBLE PCM only.
                    if (format != 1 && format != 0xFFFE)
                        return null;
                    channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..4]);
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                    bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..16]);
                }
                else if (id == 0x61746164u) // "data"
                {
                    dataStart = offset + 8;
                    dataSize = chunkSize;
                }

                offset += 8L + chunkSize + (chunkSize & 1L);
            }

            if (channels is not int channelCount
                || sampleRate is not int rate
                || bitsPerSample is not short bits
                || dataStart is not long start
                || dataSize is not long size)
            {
                return null;
            }
            if (bits != 16)
                return null;
            if (channelCount is < 1 or > 2)
                return null;
            if (rate <= 0)
                return null;
            long totalSamples = size / (channelCount * 2L);
            if (totalSamples <= 0)
                return null;

            return new MasterWaveformFrameSource(
                overlay,
                stream,
                rate,
                fpsNumerator,
                fpsDenominator,
                channelCount,
                start,
                totalSamples,
                overlay.Layout.CorrscopeGridWidth,
                overlay.Layout.CorrscopeGridHeight);
        }
        catch
        {
            stream?.Dispose();
            return null;
        }
    }

    private static int ReadAtLeast(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    public void ReadFrame(int frameIndex, Span<byte> destination)
    {
        int byteCount = checked(_gridWidth * _gridHeight * 4);
        if (destination.Length < byteCount)
            throw new System.ArgumentException(
                "scope frame destination is too small", nameof(destination));

        destination.Clear();

        long currentSample = OverlayLayout.FrameToSample(
            frameIndex, _sampleRate, _fpsNumerator, _fpsDenominator);
        long startSample = _overlay.Layout.WindowStartSample(currentSample, _sampleRate);
        long endSample = _overlay.Layout.WindowEndSample(currentSample, _sampleRate);
        long windowSamples = Math.Max(0, endSample - startSample);

        int panelCount = _overlay.Layout.PanelCount;
        int columnCount = _overlay.Layout.ColumnCount;
        int scopeHeight = _overlay.Layout.ScopeHeight;

        for (int panelIndex = 0; panelIndex < panelCount; panelIndex++)
        {
            int row = panelIndex / columnCount;
            OverlayRect scope = _overlay.Layout.GetScopeRect(panelIndex);
            int cellWidth = Math.Min(_gridWidth - scope.X, scope.Width);
            if (cellWidth <= 0 || scopeHeight <= 0)
                continue;
            DrawCell(
                destination,
                scope.X,
                row * scopeHeight,
                cellWidth,
                scopeHeight,
                startSample,
                windowSamples);
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void BuildPeakEnvelope()
    {
        _stream.Seek(_dataStart, SeekOrigin.Begin);
        const int ChunkSamples = 64 * 1024;
        byte[] raw = new byte[ChunkSamples * _channels * 2];
        for (long offset = 0; offset < _totalSamples; offset += ChunkSamples)
        {
            int count = (int)Math.Min(ChunkSamples, _totalSamples - offset);
            int bytes = count * _channels * 2;
            int total = 0;
            while (total < bytes)
            {
                int read = _stream.Read(raw, total, bytes - total);
                if (read <= 0) break;
                total += read;
            }
            int available = total / (_channels * 2);
            for (int i = 0; i < available; i++)
            {
                int offsetBytes = i * _channels * 2;
                int min = _channels == 2
                    ? Math.Min(BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offsetBytes, 2)),
                        BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offsetBytes + 2, 2)))
                    : BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offsetBytes, 2));
                int max = min;
                if (_channels == 2)
                {
                    int right = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offsetBytes + 2, 2));
                    max = Math.Max(max, right);
                }
                int bucket = (int)((offset + i) / _bucketSize);
                _minimums[bucket] = Math.Min(_minimums[bucket], min);
                _maximums[bucket] = Math.Max(_maximums[bucket], max);
            }
            if (available < count) break;
        }
    }

    private void DrawCell(
        Span<byte> destination,
        int cellX,
        int cellY,
        int width,
        int height,
        long startSample,
        long windowSamples)
    {
        int yCenter = cellY + height / 2;
        int scale = Math.Max(1, height / 2 - 1);

        for (int x = 0; x < width; x++)
        {
            int min = 0;
            int max = 0;
            long sampleStart = startSample + x * windowSamples / width;
            long sampleEnd = startSample + (x + 1L) * windowSamples / width;
            if (sampleEnd <= sampleStart) sampleEnd = sampleStart + 1;
            if (sampleEnd > 0 && sampleStart < _totalSamples)
            {
                sampleStart = Math.Max(0, sampleStart);
                sampleEnd = Math.Min(_totalSamples, sampleEnd);
                int firstBucket = (int)Math.Clamp(sampleStart / _bucketSize, 0, _minimums.Length - 1);
                int lastBucket = (int)Math.Clamp((sampleEnd - 1) / _bucketSize, 0, _minimums.Length - 1);
                for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
                {
                    min = Math.Min(min, _minimums[bucket]);
                    max = Math.Max(max, _maximums[bucket]);
                }
            }

            int yTop = yCenter - ((max * scale + 16384) >> 15);
            int yBottom = yCenter - ((min * scale + 16384) >> 15);
            yTop = Math.Max(cellY, yTop);
            yBottom = Math.Min(cellY + height - 1, yBottom);
            // Signed audio keeps min <= 0 <= max, so yTop <= yCenter <= yBottom
            // and the stroke spans from the highest positive peak to the lowest
            // negative peak. Exact silence (min == max == 0) collapses to a
            // single center pixel. The loop must iterate yTop -> yBottom:
            // iterating yBottom -> yTop would silently drop every negative
            // excursion (and draw nothing for all-negative columns).
            for (int y = yTop; y <= yBottom; y++)
            {
                int offset = (y * _gridWidth + cellX + x) * 4;
                destination[offset] = Color[0];
                destination[offset + 1] = Color[1];
                destination[offset + 2] = Color[2];
                destination[offset + 3] = Color[3];
            }
        }
    }
}
