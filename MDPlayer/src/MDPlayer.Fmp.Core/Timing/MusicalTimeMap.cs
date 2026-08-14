#nullable enable

namespace Fmp.Core.Timing;

/// <summary>A bounded timing candidate retained for structural second-pass scoring.</summary>
internal sealed record MusicalGridCandidate(
    double Bpm,
    Meter Meter,
    double BeatPhase,
    double Score);

/// <summary>
/// Canonical conversion from playback sample positions (the timeline clock, in
/// source samples) to quarter-note musical positions and MIDI ticks. Every MIDI event —
/// notes, pitch bends, rhythm/drums, loops, markers — MUST be converted through this
/// single map so that tempo, beat phase and downbeats are aligned to the same grid.
///
/// Segments are ordered, non-overlapping and contiguous: the quarter position
/// carried by a following segment equals the value produced by the previous segment at
/// the same sample, so there is no discontinuity at boundaries.
/// </summary>
internal sealed class MusicalTimeMap
{
    private readonly IReadOnlyList<TempoSegment> _segments;

    public MusicalTimeMap(
        int sampleRate,
        long startSample,
        IReadOnlyList<TempoSegment> segments,
        Meter? meter = null,
        double? firstDownbeatQuarter = null,
        double confidence = 1.0,
        double? alternateBpm = null,
        bool isTempoAmbiguous = false,
        IReadOnlyList<MusicalGridCandidate>? gridCandidates = null)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            throw new ArgumentException("musical time map requires at least one tempo segment", nameof(segments));

        SampleRate = sampleRate;
        StartSample = startSample;
        Meter = meter;
        FirstDownbeatQuarter = firstDownbeatQuarter;
        Confidence = confidence;
        AlternateBpm = alternateBpm;
        IsTempoAmbiguous = isTempoAmbiguous;
        GridCandidates = gridCandidates ?? Array.Empty<MusicalGridCandidate>();
        _segments = ValidateSegments(segments);
    }
    public IReadOnlyList<MusicalGridCandidate> GridCandidates { get; }

    public int SampleRate { get; }

    /// <summary>The first source sample the map is defined from (timeline StartSample).</summary>
    public long StartSample { get; }

    public IReadOnlyList<TempoSegment> Segments => _segments;

    public Meter? Meter { get; }

    /// <summary>
    /// Absolute quarter-note position of the first known downbeat, when one is
    /// established (driver bar info, user override, or high-confidence accent
    /// inference). Null when unknown — the exporter must NOT invent a downbeat.
    /// </summary>
    public double? FirstDownbeatQuarter { get; }

    /// <summary>
    /// Overall confidence in the tempo (0..1): the symbolic-inference fit score and
    /// half/double alias margin when inferred, else the weakest segment fit.
    /// </summary>
    public double Confidence { get; }

    /// <summary>Nearest metrically-equivalent alternative BPM, when ambiguous.</summary>
    public double? AlternateBpm { get; }

    /// <summary>True when two metrically-equivalent tempos (half/double/etc.) compete.</summary>
    public bool IsTempoAmbiguous { get; }

    /// <summary>First sample covered by any segment (inclusive).</summary>
    public long FirstSample => _segments[0].StartSample;

    /// <summary>Last sample covered by the final segment (exclusive).</summary>
    public long EndSample => _segments[^1].EndSample;

    /// <summary>
    /// The absolute quarter-note position at a sample. Because segments are
    /// contiguous this is continuous across segment boundaries; each event
    /// therefore derives from its own absolute sample and cumulative rounding
    /// drift cannot accumulate with song length.
    /// </summary>
    public double SampleToQuarterPosition(long sample)
    {
        // Deterministic out-of-range behavior: clamp to the edge segment's
        // boundary value, mirroring ComputeSampleToQuarter in the builder.
        // (LocateSegment already returns the first/last segment outside range,
        // but QuarterPositionAt throws for out-of-range samples, so handle them
        // explicitly rather than relying on the segment accessor.)
        if (sample < FirstSample)
            return _segments[0].QuarterPositionAtStart;
        if (sample >= EndSample)
            return _segments[^1].QuarterPositionAtEnd;

        TempoSegment segment = LocateSegment(sample);
        return segment.QuarterPositionAt(sample);
    }

    /// <summary>Converts a source sample position to source-wall-clock seconds.</summary>
    public double SampleToSeconds(long sample) => (double)sample / SampleRate;

    /// <summary>
    /// Converts a source sample to a MIDI tick using the wall-clock tempo actually
    /// emitted for each segment. Musical quarter positions remain available for grid
    /// metadata, but inferred phase or BPM must never alter source playback timing.
    /// </summary>
    public long SampleToTick(long sample, int ppq)
    {
        if (ppq <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppq));

        // Map each source timestamp independently from the same absolute anchor.
        // Decimal keeps the final source-time/rational conversion stable over long
        // songs and avoids introducing a rounded delta that a later event would
        // inherit.
        decimal ticks = (decimal)SampleToQuarterPosition(FirstSample) * ppq;
        long clamped = Math.Clamp(sample, FirstSample, EndSample);
        foreach (TempoSegment segment in _segments)
        {
            if (clamped <= segment.StartSample)
                break;
            long segmentEnd = Math.Min(clamped, segment.EndSample);
            long sourceDelta = Math.Max(0, segmentEnd - segment.StartSample);
            ticks += (decimal)sourceDelta * 1_000_000m * ppq
                / ((decimal)SampleRate * segment.MicrosecondsPerQuarter);
            if (clamped <= segment.EndSample)
                break;
        }
        return decimal.ToInt64(decimal.Round(ticks, 0, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Converts an absolute quarter-note position to an absolute MIDI tick.
    /// Uses <see cref="MidpointRounding.AwayFromZero"/> so a beat anchor sitting
    /// exactly on a quarter boundary maps to the exact tick and never drifts.
    /// </summary>
    public long QuarterPositionToTick(double quarter, int ppq)
    {
        if (ppq <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppq));
        double ticks = quarter * ppq;
        return (long)Math.Round(ticks, MidpointRounding.AwayFromZero);
    }

    /// <summary>Converts an absolute MIDI tick back to a quarter-note position (inverse).</summary>
    public double TickToQuarterPosition(long tick, int ppq) =>
        ppq <= 0 ? throw new ArgumentOutOfRangeException(nameof(ppq)) : (double)tick / ppq;

    private TempoSegment LocateSegment(long sample)
    {
        if (sample < FirstSample)
            return _segments[0];
        if (sample >= EndSample)
            return _segments[^1];
        // Segments are ordered; binary search for the segment containing sample.
        int low = 0;
        int high = _segments.Count - 1;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_segments[mid].EndSample <= sample)
                low = mid + 1;
            else
                high = mid;
        }
        return _segments[low];
    }

    private static IReadOnlyList<TempoSegment> ValidateSegments(IReadOnlyList<TempoSegment> segments)
    {
        var sorted = segments.OrderBy(segment => segment.StartSample).ToArray();
        for (int index = 0; index < sorted.Length; index++)
        {
            TempoSegment current = sorted[index];
            if (current.SamplesPerQuarter <= 0 || !double.IsFinite(current.SamplesPerQuarter))
                throw new ArgumentException($"segment {index} has invalid samples-per-quarter", nameof(segments));
            if (current.BeatsPerMinute <= 0 || !double.IsFinite(current.BeatsPerMinute))
                throw new ArgumentException($"segment {index} has invalid BPM", nameof(segments));
            if (current.EndSample < current.StartSample)
                throw new ArgumentException($"segment {index} end precedes start", nameof(segments));
            if (index > 0)
            {
                TempoSegment previous = sorted[index - 1];
                if (current.StartSample < previous.EndSample)
                    throw new ArgumentException($"segment {index} overlaps segment {index - 1}", nameof(segments));
                // Enforce explicit continuity: the following segment's quarter
                // position at its start must equal the previous segment's value.
                double expected = previous.QuarterPositionAt(current.StartSample);
                if (Math.Abs(expected - current.QuarterPositionAtStart) > 1e-6)
                {
                    throw new ArgumentException(
                        $"segment {index} breaks continuity at sample {current.StartSample}: " +
                        $"expected quarter {expected:0.######}, got {current.QuarterPositionAtStart:0.######}",
                        nameof(segments));
                }
            }
        }
        return sorted;
    }
}
