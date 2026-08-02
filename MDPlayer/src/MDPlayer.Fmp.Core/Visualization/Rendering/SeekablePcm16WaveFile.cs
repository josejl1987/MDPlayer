using System.Buffers.Binary;
using System.IO;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Random-access reader for a signed 16-bit PCM WAV (mono or stereo), used by
/// the interactive scope source so a seek can jump directly to any sample
/// instead of decoding sequentially. It reuses the RIFF-walking parser that
/// <see cref="MasterWaveformFrameSource"/> previously carried inline: arbitrary
/// chunk order, a <c>JUNK</c>/<c>LIST</c> header, non-44-byte headers, and
/// <c>WAVE_FORMAT_EXTENSIBLE</c> are all supported.
///
/// A reader is opened once per renderer lifetime and shares byte/sample scratch
/// buffers; the underlying stream is not loaded into memory. Each read touches
/// only a few milliseconds of audio. Malformed/truncated data is surfaced as a
/// channel-local failure (returns fewer/zero samples) and must never fail the
/// whole preview.
/// </summary>
internal sealed class SeekablePcm16WaveFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly long _dataStart;
    private byte[] _raw;
    private short[] _mono;

    public int SampleRate { get; }
    public int Channels { get; }
    public long TotalSamples { get; }

    /// <summary>
    /// Opens and parses the file, returning null when it is missing, empty, or
    /// not a signed 16-bit PCM mono/stereo WAV. Never throws for invalid input.
    /// </summary>
    public static SeekablePcm16WaveFile? TryOpen(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            return null;
        }

        try
        {
            Span<byte> riff = stackalloc byte[12];
            if (ReadAtLeast(stream, riff) < 12)
                return FinishFail(stream);
            if (riff[0] != (byte)'R' || riff[1] != (byte)'I'
                || riff[2] != (byte)'F' || riff[3] != (byte)'F')
                return FinishFail(stream);
            if (riff[8] != (byte)'W' || riff[9] != (byte)'A'
                || riff[10] != (byte)'V' || riff[11] != (byte)'E')
                return FinishFail(stream);

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
                        return FinishFail(stream);
                    stream.Seek(offset + 8, SeekOrigin.Begin);
                    if (stream.Read(fmt) < 16)
                        return FinishFail(stream);
                    short format = BinaryPrimitives.ReadInt16LittleEndian(fmt[..2]);
                    // PCM or WAVE_FORMAT_EXTENSIBLE PCM only. Note 0xFFFE stored
                    // as signed 16-bit reads back as -2.
                    if (format != 1 && format != unchecked((short)0xFFFE))
                        return FinishFail(stream);
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
                return FinishFail(stream);
            }
            if (bits != 16)
                return FinishFail(stream);
            if (channelCount is < 1 or > 2)
                return FinishFail(stream);
            if (rate <= 0)
                return FinishFail(stream);
            long totalSamples = size / (channelCount * 2L);
            if (totalSamples <= 0)
                return FinishFail(stream);

            return new SeekablePcm16WaveFile(stream, start, channelCount, rate, totalSamples);
        }
        catch
        {
            stream.Dispose();
            return null;
        }
    }

    private static SeekablePcm16WaveFile? FinishFail(FileStream stream)
    {
        stream.Dispose();
        return null;
    }

    private SeekablePcm16WaveFile(
        FileStream stream,
        long dataStart,
        int channels,
        int sampleRate,
        long totalSamples)
    {
        _stream = stream;
        _dataStart = dataStart;
        _raw = new byte[16 * 1024];
        _mono = new short[8192];
        Channels = channels;
        SampleRate = sampleRate;
        TotalSamples = totalSamples;
    }

    /// <summary>
    /// Reads up to <c>monoDestination.Length</c> samples starting at
    /// <paramref name="startSample"/>, downmixed to mono (stereo uses the louder
    /// channel per sample). Reads before sample zero or beyond EOF clamp to
    /// silence. Returns the number of samples actually written; truncated WAV
    /// data yields fewer samples rather than an exception.
    /// </summary>
    public int ReadWindow(long startSample, Span<short> monoDestination)
    {
        int requested = monoDestination.Length;
        if (requested <= 0 || TotalSamples <= 0)
            return 0;

        int leadingSilence = startSample < 0
            ? (int)Math.Min(requested, -startSample)
            : 0;

        long clampedStart = Math.Max(0, startSample);
        int available = (int)Math.Min((long)requested - leadingSilence, TotalSamples - clampedStart);
        if (available < 0)
            available = 0;

        // Backfill the leading silence (startSample < 0) before any data.
        monoDestination[..leadingSilence].Clear();
        if (available <= 0)
        {
            // No data available at all: the whole destination must be silence,
            // not a stale tail from a previous call that reused this buffer.
            monoDestination[leadingSilence..].Clear();
            return leadingSilence;
        }

        int bytes = checked(available * Channels * 2);
        if (_raw.Length < bytes)
            _raw = new byte[bytes];

        _stream.Seek(_dataStart + clampedStart * Channels * 2L, SeekOrigin.Begin);
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
            // Truncated (or lying) WAV: silence the tail instead of retaining
            // stale samples, and treat unread bytes as unavailable. The caller
            // reuses this destination buffer across frames, so any samples we
            // will not write must be zeroed to avoid a ghost waveform drawn
            // from a previous frame's tail.
            Array.Clear(_raw, total, bytes - total);
            int full = total;
            available = total / (Channels * 2);
            if (full < bytes)
                monoDestination[(leadingSilence + available)..requested].Clear();
        }
        else
        {
            // A full read that ends before the destination (startSample beyond
            // the audible tail) still leaves the destination tail undefined.
            monoDestination[(leadingSilence + available)..requested].Clear();
        }

        Span<short> scratch = _mono;
        if (scratch.Length < available)
            scratch = new short[available];

        for (int i = 0; i < available; i++)
        {
            int interleaved = i * Channels;
            short s = BinaryPrimitives.ReadInt16LittleEndian(_raw.AsSpan(interleaved * 2, 2));
            scratch[i] = s;
        }
        if (Channels == 2)
        {
            for (int i = 0; i < available; i++)
            {
                short right = BinaryPrimitives.ReadInt16LittleEndian(
                    _raw.AsSpan((i * 2 + 1) * 2, 2));
                scratch[i] = Downmix(scratch[i], right);
            }
        }

        scratch[..available].CopyTo(monoDestination[leadingSilence..(leadingSilence + available)]);
        return leadingSilence + available;
    }

    /// <summary>
    /// Retains the louder of the two channels at each sample, preserving
    /// hard-panned signals (matching the stem-generation philosophy) rather
    /// than averaging them away.
    /// </summary>
    private static short Downmix(short left, short right)
    {
        return Math.Abs((int)left) >= Math.Abs((int)right) ? left : right;
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

    public void Dispose()
    {
        _stream.Dispose();
    }
}
