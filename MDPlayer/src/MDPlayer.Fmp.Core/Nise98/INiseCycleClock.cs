namespace Fmp.Core.Nise98;

/// <summary>
/// Authoritative Nise286 CPU cycle clock.
///
/// This is the single source of truth for the emulated CPU's total elapsed
/// cycles and its configured clock frequency. The clocked OPNA execution path
/// derives every YM2608 master-clock timestamp from
/// <see cref="TotalCycles"/> through an exact rational mapper; it never reads
/// a render-loop counter, a sample position, or a replayed register trace.
/// </summary>
public interface INiseCycleClock
{
    /// <summary>
    /// Total CPU cycles executed since the last reset. Never regresses.
    /// </summary>
    ulong TotalCycles { get; }

    /// <summary>
    /// CPU clock frequency in Hz, taken from the active Nise98 machine
    /// configuration. The clocked execution coordinator sets
    /// <see cref="Nise98.CpuClockFrequencyHz"/> from the machine it drives;
    /// no PC-98 CPU frequency is ever hard-coded in the core.
    /// </summary>
    uint ClockFrequencyHz { get; }
}
