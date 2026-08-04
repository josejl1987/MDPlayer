namespace Fmp.Core.Rendering;

/// <summary>
/// Maps an authoritative Nise286 CPU cycle count to an output-rate stereo
/// sample position for PPZ8 command scheduling. Uses the exact rational
/// CPU→OPNA master-clock mapping (same ratio as <see cref="Nise98.NiseOpnaClockMapper"/>)
/// followed by the exact master-clock→output-sample division, both in
/// UInt128, so the placement never depends on float arithmetic or on how the
/// caller batches execution slices.
///
///     sample = floor( Map(cpuCycle) * sampleRate / masterClock )
///
/// where <c>Map</c> is the clock mapper's exact rational step
/// (<c>masterClock / cpuHz</c> per cycle).
/// </summary>
internal sealed class NisePpz8CommandMapper
{
    private readonly ulong _cpuHz;
    private readonly ulong _masterClockHz;
    private readonly int _sampleRate;

    public NisePpz8CommandMapper(uint cpuHz, int sampleRate, ulong masterClockHz = Nise98.NiseOpnaClockMapper.Ym2608MasterClockHz)
    {
        if (cpuHz == 0)
            throw new ArgumentOutOfRangeException(nameof(cpuHz));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _cpuHz = cpuHz;
        _sampleRate = sampleRate;
        _masterClockHz = masterClockHz;
    }

    /// <summary>
    /// The exact master-clock value the OPNA device would reach after
    /// <paramref name="cpuCycles"/> cycles of the configured CPU, computed
    /// with the same rational rule as the clock mapper.
    /// </summary>
    public ulong MapMasterClock(ulong cpuCycles)
    {
        // Map = floor(cpuCycles * masterClock / cpuHz); keep the division
        // exact with UInt128 so the value equals NiseOpnaClockMapper.Map
        // for every cycle count (no double rounding).
        return (ulong)((UInt128)_masterClockHz * cpuCycles / _cpuHz);
    }

    /// <summary>
    /// Output-rate stereo sample position (0-based) at which a PPZ8 command
    /// written at <paramref name="cpuCycles"/> takes effect.
    /// </summary>
    public long MapSample(ulong cpuCycles)
    {
        ulong masterClock = MapMasterClock(cpuCycles);
        return (long)((UInt128)masterClock * (ulong)_sampleRate / _masterClockHz);
    }
}