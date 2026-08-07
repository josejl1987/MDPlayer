#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// A contiguous span of musical time during which tempo is constant. The map is
/// absolute (source-origin): <paramref name="QuarterPositionAtStart"/> is the
/// quarter-note position at <paramref name="StartSample"/> and may be fractional
/// or negative (pickup before the first downbeat).
/// <paramref name="EndSample"/> is exclusive.
/// </summary>
internal sealed record TempoSegment(
    long StartSample,
    long EndSample,
    double QuarterPositionAtStart,
    double SamplesPerQuarter,
    double BeatsPerMinute,
    TimingSource Source,
    double Confidence)
{
    /// <summary>True when the segment spans at least one sample position update.</summary>
    public bool IsEmpty => EndSample <= StartSample;

    /// <summary>Tempo expressed as microseconds per quarter note (MIDI convention).</summary>
    public int MicrosecondsPerQuarter
    {
        get
        {
            double us = 60_000_000.0 / BeatsPerMinute;
            return (int)Math.Clamp(Math.Round(us), 1, int.MaxValue);
        }
    }

    /// <summary>The absolute quarter-note position at the exclusive end sample.</summary>
    public double QuarterPositionAtEnd =>
        QuarterPositionAtStart + (EndSample - StartSample) / SamplesPerQuarter;

    /// <summary>Quarter-position at an arbitrary sample inside this segment.</summary>
    public double QuarterPositionAt(long sample)
    {
        if (sample < StartSample || sample > EndSample)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sample),
                "sample outside this tempo segment");
        }
        return QuarterPositionAtStart + (sample - StartSample) / SamplesPerQuarter;
    }
}
