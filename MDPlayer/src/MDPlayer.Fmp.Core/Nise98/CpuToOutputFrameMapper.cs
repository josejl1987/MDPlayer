namespace Fmp.Core.Nise98;

/// <summary>
/// Exact rational mapper from Nise286 CPU cycles to host output stereo-frames,
/// used to schedule PPZ8 commands during replay. Integers only (UInt128); never
/// floating point, never seconds, never the master clock.
///
///   outputFrame = floor( cpuCycle * sampleRate / cpuHz )
///
/// The mapping is pure and stateless (depends only on the absolute input), so a
/// command mapped to output frame N is applied before generating frame N and
/// equal-cycle commands map to the same frame.
/// </summary>
internal sealed class CpuToOutputFrameMapper
{
    private readonly ulong _cpuHz;
    private readonly int _sampleRate;

    public CpuToOutputFrameMapper(ulong cpuHz, int sampleRate)
    {
        if (cpuHz == 0)
            throw new ArgumentOutOfRangeException(nameof(cpuHz));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _cpuHz = cpuHz;
        _sampleRate = sampleRate;
    }

    /// <summary>
    /// Host output stereo-frame (0-based) at which a command written at
    /// <paramref name="cpuCycles"/> takes effect (<c>floor(cpu*sr/cpuHz)</c>).
    /// </summary>
    public long MapOutputFrame(ulong cpuCycles) =>
        (long)((UInt128)cpuCycles * (ulong)_sampleRate / _cpuHz);

    /// <summary>
    /// The CPU-cycle ceiling for a chunk ending at output frame
    /// <paramref name="absoluteOutputFrame"/> (<c>ceil(frame*cpuHz/sr)</c>).
    /// Every event at or before this cycle belongs to the chunk.
    /// </summary>
    public ulong MapFrameToCpuCeiling(long absoluteOutputFrame) =>
        (ulong)(((UInt128)(ulong)absoluteOutputFrame * _cpuHz + (ulong)_sampleRate - 1) / (ulong)_sampleRate);
}
