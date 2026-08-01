namespace Fmp.Core.Nise98;

/// <summary>
/// Portable replacement for MNDRV.FMTimer.
/// Provides timer ticks at the OPN clock rate for the Nise98 emulator.
/// </summary>
public class OpnaTimer
{
    private readonly int baseClock;
    private long counter;
    private bool enabled;

    public OpnaTimer(bool _unused, object _unused2, int baseClock)
    {
        this.baseClock = baseClock;
        counter = 0;
        enabled = false;
    }

    public void Start()
    {
        enabled = true;
    }

    public void Stop()
    {
        enabled = false;
    }

    /// <summary>
    /// Advance the timer by one emulated sample period.
    /// Returns the number of ticks elapsed.
    /// </summary>
    public long Tick(int sampleRate)
    {
        if (!enabled) return 0;
        counter += baseClock / sampleRate;
        return counter;
    }

    public long GetCounter() => counter;
    public void Reset() { counter = 0; }
}
