namespace Fmp.Core.Nise98;

/// <summary>
/// Exact rational mapper from Nise286 CPU cycles to the YM2608 master clock.
///
/// Conversion: <c>opnaClock = floor(totalCpuCycles * masterClockHz / cpuClockHz)</c>,
/// evaluated with integer arithmetic only (UInt128) — never floating point,
/// never seconds, never sample rates. State is maintained as
/// <c>lastCpuCycle</c>, <c>currentOpnaClock</c> and <c>fractionalRemainder</c>
/// so the mapping is:
///
///  * monotonic — the output never regresses;
///  * deterministic — same input always yields the same output;
///  * chunk-independent — the result depends only on the absolute input, not
///    on how the CPU cycles were fed in;
///  * drift-free — the fractional remainder carries exactly, so long runs
///    stay on the closed-form value.
///
/// A regression (input below the previous input) throws
/// <see cref="ArgumentOutOfRangeException"/>. Overflow of the accumulated
/// opna clock throws <see cref="OverflowException"/> (checked, no saturation).
/// </summary>
public sealed class NiseOpnaClockMapper
{
    /// <summary>YM2608 master clock frequency (Hz).</summary>
    public const ulong Ym2608MasterClockHz = 7_987_200;

    private readonly uint _cpuClockHz;
    private readonly ulong _masterClockHz;

    private ulong _lastCpuCycle;
    private ulong _currentOpnaClock;
    private ulong _fractionalRemainder;

    /// <summary>
    /// Creates a mapper for the given CPU clock frequency. The master clock
    /// defaults to the YM2608 clock (<see cref="Ym2608MasterClockHz"/>); tests
    /// may pass a different master clock to keep expected values small.
    /// </summary>
    public NiseOpnaClockMapper(uint cpuClockHz, ulong masterClockHz = Ym2608MasterClockHz)
    {
        if (cpuClockHz == 0)
            throw new ArgumentOutOfRangeException(nameof(cpuClockHz), cpuClockHz, "CPU clock frequency must be non-zero");
        if (masterClockHz == 0)
            throw new ArgumentOutOfRangeException(nameof(masterClockHz), masterClockHz, "master clock frequency must be non-zero");
        _cpuClockHz = cpuClockHz;
        _masterClockHz = masterClockHz;
    }

    /// <summary>
    /// Returns the absolute YM2608 master-clock timestamp for the given
    /// absolute CPU cycle count.
    /// </summary>
    public ulong Map(ulong totalCpuCycles)
    {
        if (totalCpuCycles < _lastCpuCycle)
            throw new ArgumentOutOfRangeException(
                nameof(totalCpuCycles), totalCpuCycles,
                $"CPU cycle clock regressed: {totalCpuCycles} < {_lastCpuCycle}");

        ulong delta = totalCpuCycles - _lastCpuCycle;
        if (delta == 0)
            return _currentOpnaClock;

        // currentOpnaClock += floor((delta * master + fractionalRemainder) / cpuHz)
        // carried exactly as an integer rational; UInt128 keeps delta * master
        // from overflowing (delta < 2^64, master < 2^23 → product < 2^87).
        UInt128 numerator = (UInt128)delta * _masterClockHz + _fractionalRemainder;
        _currentOpnaClock = checked(_currentOpnaClock + (ulong)(numerator / _cpuClockHz));
        _fractionalRemainder = (ulong)(numerator % _cpuClockHz);
        _lastCpuCycle = totalCpuCycles;
        return _currentOpnaClock;
    }

    /// <summary>Resets the mapper to the origin (cycle 0 → opna clock 0).</summary>
    public void Reset()
    {
        _lastCpuCycle = 0;
        _currentOpnaClock = 0;
        _fractionalRemainder = 0;
    }
}
