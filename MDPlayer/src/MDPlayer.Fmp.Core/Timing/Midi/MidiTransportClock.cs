#nullable enable

namespace Fmp.Core.Midi;

/// <summary>
/// Fixed 120 BPM source-sample transport used by raw MIDI transcription.
/// </summary>
internal static class MidiTransportClock
{
    private const int MaxPpq = 0x7FFF;

    /// <summary>
    /// Converts a source sample position to a source-relative MIDI tick.
    /// At 120 BPM, one second contains two quarter notes, so the conversion is
    /// <c>(sample - timelineStartSample) * 2 * ppq / sampleRate</c>, rounded
    /// away from zero exactly once.
    /// </summary>
    internal static long SampleToTick(
        long timelineStartSample,
        long sample,
        int sampleRate,
        int ppq)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (ppq <= 0 || ppq > MaxPpq)
            throw new ArgumentOutOfRangeException(nameof(ppq));

        long delta = checked(sample - timelineStartSample);
        if (delta < 0)
            throw new InvalidOperationException(
                $"Source sample {sample} precedes timeline start {timelineStartSample}.");

        decimal ticks = (decimal)delta * (2m * ppq) / sampleRate;
        return decimal.ToInt64(decimal.Round(ticks, 0, MidpointRounding.AwayFromZero));
    }
}
