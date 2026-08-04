using Fmp.Core.Playback.Opna;

namespace Fmp.Core.Rendering.NativeAudioValidation;

/// <summary>
/// Recording test seam around an underlying <see cref="IClockedOpnaDevice"/>.
/// Counts every operation without altering behavior, so the validation harness
/// can assert that replay performs zero native status reads and zero native IRQ
/// reads. This is a wrapper used only by validation; the production replay
/// path is unchanged and carries no counters.
/// </summary>
internal sealed class RecordingClockedDevice : IClockedOpnaDevice
{
    private readonly IClockedOpnaDevice _inner;

    public RecordingClockedDevice(IClockedOpnaDevice inner) => _inner = inner;

    public int StatusReadCount { get; private set; }
    public int IrqReadCount { get; private set; }
    public int DrainCount { get; private set; }
    public int AdvanceCount { get; private set; }
    public int WriteCount { get; private set; }

    public int OutputRateHz => _inner.OutputRateHz;
    public ulong MasterClock => _inner.MasterClock;
    public bool IrqAsserted { get { IrqReadCount++; return _inner.IrqAsserted; } }
    public int OutputLatencyFrames => _inner.OutputLatencyFrames;

    public void AdvanceTo(ulong masterClock) { AdvanceCount++; _inner.AdvanceTo(masterClock); }

    public void WriteRegister(ulong masterClock, byte bank, byte address, byte value)
    { WriteCount++; _inner.WriteRegister(masterClock, bank, address, value); }

    public byte ReadStatus(ulong masterClock, byte bank)
    { StatusReadCount++; return _inner.ReadStatus(masterClock, bank); }

    public int DrainAudio(short[] interleavedStereo, int requestedFrames)
    { DrainCount++; return _inner.DrainAudio(interleavedStereo, requestedFrames); }

    public void ResetChip() => _inner.ResetChip();
    public void ClearAdpcmRam(byte fillValue) => _inner.ClearAdpcmRam(fillValue);
    public void Dispose() => _inner.Dispose();
}
