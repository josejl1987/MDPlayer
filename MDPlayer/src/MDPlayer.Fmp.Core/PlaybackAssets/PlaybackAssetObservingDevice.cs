using Fmp.Core.Playback.Opna;
using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets;

/// <summary>
/// Decorator that places one <see cref="IPlaybackAssetCollector"/> on the
/// ordered OPNA write stream immediately before each write reaches the
/// underlying emulator device. This is the single authoritative observation
/// point for the FMP native OPNA path: every write observed here is forwarded
/// unchanged, and instruments are captured as part of the same ordered stream.
/// </summary>
internal sealed class PlaybackAssetObservingDevice : IClockedOpnaDevice
{
    private readonly IClockedOpnaDevice _inner;
    private readonly IPlaybackAssetCollector _collector;
    private readonly ChipType _chipType;
    private readonly int _chipIndex;
    private bool _disposed;

    public PlaybackAssetObservingDevice(
        IClockedOpnaDevice inner,
        IPlaybackAssetCollector collector,
        ChipType chipType,
        int chipIndex)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _chipType = chipType;
        _chipIndex = chipIndex;
    }

    public int OutputRateHz => _inner.OutputRateHz;
    public ulong MasterClock => _inner.MasterClock;
    public bool IrqAsserted => _inner.IrqAsserted;
    public int OutputLatencyFrames => _inner.OutputLatencyFrames;

    public void AdvanceTo(ulong masterClock) => _inner.AdvanceTo(masterClock);

    public void WriteRegister(ulong masterClock, byte bank, byte address, byte value)
    {
        ObserveSafely(address, bank, value);
        _inner.WriteRegister(masterClock, bank, address, value);
    }

    public byte ReadStatus(ulong masterClock, byte bank) =>
        _inner.ReadStatus(masterClock, bank);

    public int DrainAudio(short[] interleavedStereo, int requestedFrames) =>
        _inner.DrainAudio(interleavedStereo, requestedFrames);

    public void ResetChip()
    {
        _inner.ResetChip();
    }

    public void ClearAdpcmRam(byte fillValue) => _inner.ClearAdpcmRam(fillValue);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _inner.Dispose();
    }

    private void ObserveSafely(byte address, byte bank, byte value)
    {
        // An export/collection bug must never corrupt playback: failures are
        // swallowed here rather than propagated into the emulator write path.
        try
        {
            _collector.Observe(new ChipWriteEvent
            {
                ChipType = _chipType,
                ChipIndex = _chipIndex,
                Port = bank,
                Address = address,
                Data = value,
                PlaybackSample = null,
                WriteIndex = null,
            });
        }
        catch (Exception)
        {
            // Diagnostics wiring is out of scope; do not disturb playback.
        }
    }
}
