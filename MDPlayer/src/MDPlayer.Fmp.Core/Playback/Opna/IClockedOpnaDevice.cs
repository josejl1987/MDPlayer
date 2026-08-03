namespace Fmp.Core.Playback.Opna;

/// <summary>
/// Absolute YM2608 master-clock device contract.
///
/// Timing model: the caller is the master of time. Every operation takes or
/// advances an absolute clock in YM2608 master cycles (7,987,200 Hz). The
/// device never advances time on its own; <see cref="AdvanceTo"/> moves the
/// chip forward, and every other call is a pure, time-independent action or
/// a read of the already-advanced state. Draining audio is a FIFO read that
/// never moves the clock.
///
/// This deliberately differs from the sample-position contract of
/// <see cref="ILegacyFmpAudioEngine"/>: the two timing models are not merged
/// behind one interface.
/// </summary>
public interface IClockedOpnaDevice : IDisposable
{
    /// <summary>Output sample rate the session was opened at (44100/48000/96000).</summary>
    int OutputRateHz { get; }

    /// <summary>Current absolute master clock. Never advances time.</summary>
    ulong MasterClock { get; }

    /// <summary>Current chip IRQ level. Never advances or clears anything.</summary>
    bool IrqAsserted { get; }

    /// <summary>
    /// Advances the chip so <paramref name="masterClock"/> becomes the current
    /// absolute time. Throws <see cref="OpnaClockRegressionException"/> if the
    /// clock would regress.
    /// </summary>
    void AdvanceTo(ulong masterClock);

    /// <summary>
    /// Schedules one register write at <paramref name="masterClock"/> through
    /// the bus scheduler. Preserves call order for equal clocks.
    /// </summary>
    void WriteRegister(ulong masterClock, byte bank, byte address, byte value);

    /// <summary>Reads the live LLE status byte at <paramref name="masterClock"/>.</summary>
    byte ReadStatus(ulong masterClock, byte bank);

    /// <summary>
    /// Drains already-queued resampled stereo PCM into
    /// <paramref name="interleavedStereo"/> (at least
    /// <paramref name="requestedFrames"/> * 2 samples). Returns frames written,
    /// which may be less than requested. Never advances the clock.
    /// </summary>
    int DrainAudio(short[] interleavedStereo, int requestedFrames);

    /// <summary>
    /// Runs the chip-reset helper: preserves the external ADPCM RAM and the
    /// output rate, empties queued audio, resets time to zero.
    /// </summary>
    void ResetChip();

    /// <summary>Fills all 256 KiB of external ADPCM RAM with <paramref name="fillValue"/>.</summary>
    void ClearAdpcmRam(byte fillValue);
}
