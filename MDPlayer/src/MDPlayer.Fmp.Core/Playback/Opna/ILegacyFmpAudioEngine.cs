namespace Fmp.Core.Playback.Opna;

/// <summary>
/// Sample-position audio engine contract: the current MDSound-backed playback
/// path. Register writes carry a <c>samplePosition</c> in output samples, and
/// <see cref="Render"/> produces PCM at the engine's own pace.
///
/// Timing model: rendering drives time. The engine maintains an implicit
/// sample position; callers write registers ahead of time (at a sample
/// position) and render buffers when audio is needed.
///
/// This deliberately differs from the absolute master-clock contract of
/// <see cref="IClockedOpnaDevice"/>: the two timing models are not merged
/// behind one interface.
/// </summary>
public interface ILegacyFmpAudioEngine : IDisposable
{
    /// <summary>Output sample rate in Hz (e.g. 44100).</summary>
    int SampleRate { get; }

    /// <summary>Starts the engine (chip power-on writes included).</summary>
    void Start();

    /// <summary>Stops the engine. Rendering afterwards produces silence.</summary>
    void Stop();

    /// <summary>Stops then starts, returning the engine to its initial state.</summary>
    void Reset();

    /// <summary>
    /// Writes one YM2608 register at the given output sample position. Port
    /// 0 selects the FM/SSG bank, port 1 the extended/FM2 bank.
    /// </summary>
    void WriteYm2608(int chipId, byte port, byte address, byte value, long samplePosition);

    /// <summary>Transfers an external ADPCM-B RAM block (VGM 0x81 semantics).</summary>
    void LoadYm2608AdpcmData(uint startAddress, byte[] data);

    /// <summary>
    /// Loads a PPZ8 PCM bank (<paramref name="mode"/>: 0 = 8-bit, 1 = 4-bit)
    /// at the given sample position. <paramref name="samples"/> is one
    /// ReadOnlyMemory per PPZ8 channel.
    /// </summary>
    void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition);

    /// <summary>Writes one PPZ8 register at the given sample position.</summary>
    void WritePpz8(byte port, byte address, byte value, long samplePosition);

    /// <summary>
    /// Renders up to <paramref name="frames"/> stereo frames of interleaved
    /// int16 PCM into <paramref name="interleavedStereo"/> (capacity at least
    /// <paramref name="frames"/> * 2). Returns the number of frames written.
    /// </summary>
    int Render(short[] interleavedStereo, int frames);
}
