namespace Fmp.Core.Playback.Opna;

/// <summary>
/// The native <c>mdplayer_opna</c> session exposed through the absolute
/// master-clock contract <see cref="IClockedOpnaDevice"/>.
/// </summary>
public sealed class NativeOpnaDevice : IClockedOpnaDevice
{
    private readonly OpnaNativeSession _session;

    private NativeOpnaDevice(OpnaNativeSession session, int outputRateHz)
    {
        _session = session;
        OutputRateHz = outputRateHz;
    }

    /// <summary>
    /// Opens a native session at the requested output rate. Only 44100, 48000
    /// and 96000 are accepted; anything else surfaces as
    /// <see cref="OpnaUnsupportedRateException"/> from the native library.
    /// </summary>
    public static NativeOpnaDevice Open(int outputRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(outputRateHz, 0);
        return new NativeOpnaDevice(
            OpnaNativeSession.Open((uint)outputRateHz), outputRateHz);
    }

    /// <inheritdoc />
    public int OutputRateHz { get; }

    /// <inheritdoc />
    public ulong MasterClock => _session.MasterClock;

    /// <inheritdoc />
    public bool IrqAsserted => _session.GetIrq();

    /// <inheritdoc />
    public int OutputLatencyFrames => _session.OutputLatencyFrames;

    /// <inheritdoc />
    public void AdvanceTo(ulong masterClock) => _session.AdvanceTo(masterClock);

    /// <inheritdoc />
    public void WriteRegister(ulong masterClock, byte bank, byte address, byte value) =>
        _session.WriteRegister(masterClock, bank, address, value);

    /// <inheritdoc />
    public byte ReadStatus(ulong masterClock, byte bank) =>
        _session.ReadStatus(masterClock, bank);

    /// <inheritdoc />
    public int DrainAudio(short[] interleavedStereo, int requestedFrames) =>
        _session.DrainAudio(interleavedStereo, requestedFrames);

    /// <inheritdoc />
    public void ResetChip() => _session.ResetChip();

    /// <inheritdoc />
    public void ClearAdpcmRam(byte fillValue) => _session.ClearAdpcmRam(fillValue);

    /// <inheritdoc />
    /// <summary>
    /// ABI version reported by the loaded native library. Validation accessor
    /// (no production counters); useful for the replay report and the ABI/export
    /// gate.
    /// </summary>
    internal uint AbiVersion => _session.AbiVersion;

    public void Dispose() => _session.Dispose();
}
