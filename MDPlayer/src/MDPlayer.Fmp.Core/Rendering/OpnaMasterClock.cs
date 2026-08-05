namespace Fmp.Core.Rendering;

/// <summary>
/// Constant and exact rational mapper for the absolute YM2608 master-clock
/// timeline. The capture path timestamps every OPNA/PPZ8 event with an absolute
/// master-clock position accumulated by <see cref="FmpRuntime"/> at the control
/// tick rate; the replay paths use this shared mapper to relate that timeline
/// to host output frames. Integers only (UInt128); never floating point.
/// </summary>
internal static class OpnaMasterClock
{
    /// <summary>YM2608 master clock frequency (Hz).</summary>
    public const ulong Hz = 7_987_200;
}

/// <summary>
/// Exact rational mapper between absolute YM2608 master-clock positions and
/// host output stereo-frames, used by the trace replay paths.
///
///   outputFrame = floor( masterClock * sampleRate / masterHz )
///
/// Both directions are pure and stateless (depend only on the argument), so
/// equal master-clock positions always map to the same output frame and the
/// same frame always maps to the same clock ceiling.
/// </summary>
internal sealed class OpnaMasterClockFrameMapper
{
    private readonly int _sampleRate;

    public OpnaMasterClockFrameMapper(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
    }

    /// <summary>Host output stereo-frame (0-based) at which a command written at
    /// <paramref name="masterClock"/> takes effect (<c>floor(clk*sr/hz)</c>).</summary>
    public long MapOutputFrame(ulong masterClock) =>
        (long)((UInt128)masterClock * (ulong)_sampleRate / OpnaMasterClock.Hz);

    /// <summary>
    /// The master-clock ceiling for a chunk ending at output frame
    /// <paramref name="absoluteOutputFrame"/> (<c>ceil(frame*hz/sr)</c>). Every
    /// event at or before this clock belongs to the chunk.
    /// </summary>
    public ulong MapFrameToMasterCeiling(long absoluteOutputFrame)
    {
        if (absoluteOutputFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteOutputFrame));
        return (ulong)(((UInt128)(ulong)absoluteOutputFrame * OpnaMasterClock.Hz
                        + (ulong)_sampleRate - 1) / (ulong)_sampleRate);
    }
}
