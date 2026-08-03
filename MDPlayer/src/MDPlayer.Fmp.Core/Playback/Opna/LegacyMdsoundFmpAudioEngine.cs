using Fmp.Core.Audio.Mdsound;

namespace Fmp.Core.Playback.Opna;

/// <summary>
/// The MDSound-backed playback engine exposed through the sample-position
/// contract <see cref="ILegacyFmpAudioEngine"/>. This is the current
/// production backend; no production call path is changed by its existence.
/// </summary>
public sealed class LegacyMdsoundFmpAudioEngine : ILegacyFmpAudioEngine
{
    private readonly MdsoundFmpChipSink _sink;
    private int[][] _staging = new[] { Array.Empty<int>(), Array.Empty<int>() };

    /// <summary>
    /// Creates the engine over a fresh MDSound YM2608 + PPZ8 instance.
    /// </summary>
    /// <param name="sampleRate">Output sample rate (default 44100).</param>
    /// <param name="chipId">Chip identifier passed to MDSound (default 0).</param>
    /// <param name="ssgGainDb">SSG/PSG mix gain in decibels (default 0).</param>
    public LegacyMdsoundFmpAudioEngine(int sampleRate = 44100, byte chipId = 0, double ssgGainDb = 0)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sink = new MdsoundFmpChipSink(sampleRate, chipId, ssgGainDb);
        SampleRate = sampleRate;
    }

    /// <inheritdoc />
    public int SampleRate { get; }

    /// <inheritdoc />
    public void Start() => _sink.Start();

    /// <inheritdoc />
    public void Stop() => _sink.Stop();

    /// <inheritdoc />
    public void Reset() => _sink.Reset();

    /// <inheritdoc />
    public void WriteYm2608(int chipId, byte port, byte address, byte value, long samplePosition) =>
        _sink.WriteYm2608(chipId, port, address, value, samplePosition);

    /// <inheritdoc />
    public void LoadYm2608AdpcmData(uint startAddress, byte[] data) =>
        _sink.LoadYm2608AdpcmData(startAddress, data);

    /// <inheritdoc />
    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition) =>
        _sink.LoadPpz8Bank(bank, mode, samples, samplePosition);

    /// <inheritdoc />
    public void WritePpz8(byte port, byte address, byte value, long samplePosition) =>
        _sink.WritePpz8(port, address, value, samplePosition);

    /// <inheritdoc />
    public int Render(short[] interleavedStereo, int frames)
    {
        ArgumentNullException.ThrowIfNull(interleavedStereo);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        if (interleavedStereo.Length < frames * 2)
            throw new ArgumentException("interleaved stereo buffer too small for the requested frame count", nameof(interleavedStereo));

        if (_staging[0].Length < frames)
        {
            _staging[0] = new int[frames];
            _staging[1] = new int[frames];
        }
        else
        {
            Array.Clear(_staging[0], 0, frames);
            Array.Clear(_staging[1], 0, frames);
        }

        // MDSound.FmpChipSink returns `samples` without touching the buffers
        // when the engine is not started; the zeroed staging then yields
        // guaranteed silence instead of stale/garbage samples.
        int rendered = _sink.Render(_staging, frames);
        for (int i = 0; i < rendered; i++)
        {
            interleavedStereo[2 * i] = (short)_staging[0][i];
            interleavedStereo[2 * i + 1] = (short)_staging[1][i];
        }
        return rendered;
    }

    /// <inheritdoc />
    public void Dispose() => _sink.Dispose();
}
