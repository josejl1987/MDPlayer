using System.Buffers;
using System.Buffers.Binary;

namespace Fmp.Core.Audio;

/// <summary>
/// Minimal internal PCM WAV writer.
/// Streams signed 16-bit stereo PCM to disk.
/// </summary>
internal class WavWriter : IDisposable
{
    private readonly string _finalPath;
    private readonly string _tempPath;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private FileStream _stream;
    private long _dataSize;
    private bool _closed;
    private byte[] _scratch;

    public WavWriter(string path, int sampleRate = 44100, int channels = 2, int bitsPerSample = 16)
    {
        _finalPath = path;
        _tempPath = path + ".tmp";
        _sampleRate = sampleRate;
        _channels = channels;
        _bitsPerSample = bitsPerSample;
        _dataSize = 0;
        _closed = false;
        _scratch = ArrayPool<byte>.Shared.Rent(8192);

        _stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write);
        WriteHeader(0);
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        if (_closed || _stream == null) return;
        int byteCount = samples.Length * 2;
        if (_scratch.Length < byteCount)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = ArrayPool<byte>.Shared.Rent(byteCount);
        }

        Span<byte> target = _scratch.AsSpan(0, byteCount);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(target.Slice(i * 2, 2), samples[i]);
        }

        _stream.Write(_scratch, 0, byteCount);
        _dataSize += byteCount;
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;

        if (_stream != null)
        {
            int headerSize = 44;
            int fileSize = (int)_dataSize + headerSize - 8;
            int dataChunkSize = (int)_dataSize;

            _stream.Seek(4, SeekOrigin.Begin);
            WriteLE32(_stream, fileSize);

            _stream.Seek(40, SeekOrigin.Begin);
            WriteLE32(_stream, dataChunkSize);

            _stream.Flush();
            _stream.Close();
            _stream = null;
        }

        if (_scratch != null)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = null;
        }

        if (File.Exists(_finalPath))
            File.Delete(_finalPath);
        File.Move(_tempPath, _finalPath);
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _closed = true;
            if (_stream != null)
            {
                _stream.Close();
                _stream = null;
            }
            if (_scratch != null)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = null;
            }
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
        }
    }

    private void WriteHeader(int dataSize)
    {
        WriteASCII(_stream, "RIFF");
        WriteLE32(_stream, 36 + dataSize);
        WriteASCII(_stream, "WAVE");
        WriteASCII(_stream, "fmt ");
        WriteLE32(_stream, 16);
        WriteLE16(_stream, 1);
        WriteLE16(_stream, (short)_channels);
        WriteLE32(_stream, _sampleRate);
        WriteLE32(_stream, _sampleRate * _channels * _bitsPerSample / 8);
        WriteLE16(_stream, (short)(_channels * _bitsPerSample / 8));
        WriteLE16(_stream, (short)_bitsPerSample);
        WriteASCII(_stream, "data");
        WriteLE32(_stream, dataSize);
    }

    private static void WriteASCII(FileStream s, string text)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteLE16(FileStream s, short value)
    {
        s.WriteByte((byte)(value & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private static void WriteLE32(FileStream s, int value)
    {
        s.WriteByte((byte)(value & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
        s.WriteByte((byte)((value >> 16) & 0xFF));
        s.WriteByte((byte)((value >> 24) & 0xFF));
    }
}
