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
    private static readonly byte[] Color = { 0x7A, 0xA4, 0xFF };

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
    private short[] _window = System.Array.Empty<short>();
    private byte[] _raw = System.Array.Empty<byte>();
    private int _windowCount;

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

        long startSample = OverlayLayout.FrameToSample(
            frameIndex, _sampleRate, _fpsNumerator, _fpsDenominator);
        long endSample = OverlayLayout.FrameToSample(
            (long)frameIndex + 1, _sampleRate, _fpsNumerator, _fpsDenominator);
        long windowSamples = Math.Max(0, endSample - startSample);

        // Read the frame's audio window once; every grid cell samples it.
        ReadWindow(startSample, windowSamples);

        int panelCount = _overlay.Layout.PanelCount;
        int columnCount = _overlay.Layout.ColumnCount;
        int scopeHeight = _overlay.Layout.ScopeHeight;

        for (int panelIndex = 0; panelIndex < panelCount; panelIndex++)
        {
            int row = panelIndex / columnCount;
            int column = panelIndex % columnCount;
            OverlayRect scope = _overlay.Layout.GetScopeRect(panelIndex);
            int cellWidth = Math.Min(_gridWidth / columnCount, scope.Width);
            if (cellWidth <= 0 || scopeHeight <= 0)
                continue;
            DrawCell(
                destination,
                column * cellWidth,
                row * scopeHeight,
                cellWidth,
                scopeHeight,
                windowSamples);
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private void ReadWindow(long startSample, long windowSamples)
    {
        int needed = checked((int)Math.Min(windowSamples, _totalSamples) * _channels);
        if (_window.Length < needed)
            _window = new short[needed];
        if (_window.Length > 0)
            Array.Clear(_window, 0, _window.Length);

        if (windowSamples <= 0 || _totalSamples <= 0)
        {
            _windowCount = 0;
            return;
        }

        long first = Math.Clamp(startSample, 0, _totalSamples);
        long last = Math.Min(_totalSamples, startSample + windowSamples);
        int count = checked((int)(last - first));
        if (count <= 0)
        {
            _windowCount = 0;
            return;
        }

        int bytes = count * _channels * 2;
        if (_raw.Length < bytes)
            _raw = new byte[bytes];

        _stream.Seek(_dataStart + first * _channels * 2L, SeekOrigin.Begin);
        int total = 0;
        while (total < bytes)
        {
            int read = _stream.Read(_raw, total, bytes - total);
            if (read <= 0)
                break;
            total += read;
        }
        if (total < bytes)
        {
            // Truncated (or lying) WAV: the unread tail would otherwise retain
            // the previous frame's samples and be drawn as garbage. Silence it.
            Array.Clear(_raw, total, bytes - total);
        }

        int perChannel = count;
        for (int i = 0; i < perChannel; i++)
        {
            // _raw is interleaved: sample pair i occupies bytes [4i, 4i+4)
            // for stereo (left at 4i, right at 4i+2), or [2i, 2i+2) for mono.
            int interleaved = i * _channels;
            _window[i] =
                BinaryPrimitives.ReadInt16LittleEndian(_raw.AsSpan(interleaved * 2, 2));
            if (_channels == 2)
            {
                _window[perChannel + i] =
                    BinaryPrimitives.ReadInt16LittleEndian(_raw.AsSpan((interleaved + 1) * 2, 2));
            }
        }
        _windowCount = perChannel;
    }

    private void DrawCell(
        Span<byte> destination,
        int cellX,
        int cellY,
        int width,
        int height,
        long windowSamples)
    {
        int yCenter = cellY + height / 2;
        int scale = Math.Max(1, height / 2 - 1);

        for (int x = 0; x < width; x++)
        {
            long s0 = x * windowSamples / width;
            long s1 = (x + 1L) * windowSamples / width;
            if (s1 <= s0)
                s1 = s0 + 1;

            int min = 0;
            int max = 0;
            for (long s = s0; s < s1; s++)
            {
                if (s >= _windowCount)
                    break;
                int left = _window[(int)s];
                if (left < min) min = left;
                if (left > max) max = left;
                if (_channels == 2)
                {
                    int right = _window[(int)s + _windowCount];
                    if (right < min) min = right;
                    if (right > max) max = right;
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
                destination[offset + 3] = 255;
            }
        }
    }
}
