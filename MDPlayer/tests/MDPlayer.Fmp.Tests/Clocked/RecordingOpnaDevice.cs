using Fmp.Core.Playback.Opna;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Recording fake <see cref="IClockedOpnaDevice"/> for clocked-path tests:
/// captures every operation with its absolute master-clock timestamp and
/// offers a scriptable IRQ line.
/// </summary>
internal sealed class RecordingOpnaDevice : IClockedOpnaDevice
{
    public int OutputRateHz { get; set; } = 44100;
    public ulong MasterClock { get; private set; }
    public bool IrqAsserted { get; set; }
    public int OutputLatencyFrames { get; set; }

    public List<(ulong clock, byte bank, byte address, byte value)> Writes { get; } = new();
    public List<(ulong clock, byte bank)> StatusReads { get; } = new();
    public List<ulong> AdvanceToCalls { get; } = new();
    public List<byte> ResetChipCalls { get; } = new();
    public List<byte> ClearAdpcmCalls { get; } = new();
    public bool Disposed { get; private set; }

    /// <summary>Status byte returned by ReadStatus, per bank.</summary>
    public byte[] StatusBytes { get; } = { 0x00, 0x00 };

    public void AdvanceTo(ulong masterClock)
    {
        if (masterClock < MasterClock)
            throw new OpnaClockRegressionException($"clock regressed: {masterClock} < {MasterClock}");
        MasterClock = masterClock;
        AdvanceToCalls.Add(masterClock);
    }

    public void WriteRegister(ulong masterClock, byte bank, byte address, byte value)
        => Writes.Add((masterClock, bank, address, value));

    public byte ReadStatus(ulong masterClock, byte bank)
    {
        StatusReads.Add((masterClock, bank));
        return StatusBytes[bank];
    }

    public int DrainAudio(short[] interleavedStereo, int requestedFrames)
    {
        // Fake never produces audio in the bridge tests; invariants tested
        // against the real device in the integration suite.
        return 0;
    }

    public void ResetChip()
    {
        ResetChipCalls.Add(1);
        MasterClock = 0;
        IrqAsserted = false;
    }

    public void ClearAdpcmRam(byte fillValue) => ClearAdpcmCalls.Add(fillValue);

    public void Dispose() => Disposed = true;
}
