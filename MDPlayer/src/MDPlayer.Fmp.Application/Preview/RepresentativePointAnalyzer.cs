using Fmp.Application.Contracts;
using Fmp.Core.Visualization;

namespace Fmp.Application.Preview;

/// <summary>
/// Computes representative scrub points for the timeline transport from a
/// captured <see cref="VisualizationTimeline"/>. Categories with no data are
/// skipped; every point is clamped into the track range.
/// </summary>
internal static class RepresentativePointAnalyzer
{
    private const double DensityWindowSeconds = 2.0;

    /// <summary>
    /// Computes the representative points for <paramref name="timeline"/>.
    /// Points (when their category has data): intro-end, first-event, densest,
    /// widest-pitch, first-percussion, first-sample, middle, near-outro.
    /// </summary>
    public static IReadOnlyList<RepresentativePoint> Compute(VisualizationTimeline timeline, double introSeconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (introSeconds < 0)
            introSeconds = 0;

        double sampleRate = timeline.SampleRate > 0 ? timeline.SampleRate : 44_100.0;
        double endSeconds = timeline.EndSample > timeline.StartSample
            ? timeline.EndSample / sampleRate
            : 0.0;

        var points = new List<RepresentativePoint>();

        // Always present (no data dependency); clamped into the track range.
        double introEndSeconds = endSeconds > 0 ? Math.Min(introSeconds, endSeconds) : introSeconds;
        points.Add(new RepresentativePoint
        {
            Kind = "intro-end",
            TimeSeconds = introEndSeconds,
            Label = "Intro ends",
        });

        long? firstEvent = FirstEventSample(timeline);
        if (firstEvent.HasValue)
        {
            points.Add(new RepresentativePoint
            {
                Kind = "first-event",
                TimeSeconds = firstEvent.Value / sampleRate,
                Label = "First event",
            });
        }

        if (timeline.Notes.Count > 0)
        {
            RepresentativePoint? densest = FindDensest(timeline, sampleRate);
            if (densest != null)
                points.Add(densest);

            RepresentativePoint? widest = FindWidestPitch(timeline, sampleRate);
            if (widest != null)
                points.Add(widest);
        }

        if (timeline.Rhythm.Count > 0)
        {
            long firstPercussion = timeline.Rhythm.Min(r => r.SamplePosition);
            points.Add(new RepresentativePoint
            {
                Kind = "first-percussion",
                TimeSeconds = firstPercussion / sampleRate,
                Label = "First percussion",
            });
        }

        if (timeline.SamplePlayback.Length > 0)
        {
            long firstSample = timeline.SamplePlayback.Min(s => s.StartSample);
            points.Add(new RepresentativePoint
            {
                Kind = "first-sample",
                TimeSeconds = firstSample / sampleRate,
                Label = "First sample",
            });
        }

        if (endSeconds > 0)
        {
            double middle = (timeline.StartSample + (timeline.EndSample - timeline.StartSample) / 2.0) / sampleRate;
            points.Add(new RepresentativePoint
            {
                Kind = "middle",
                TimeSeconds = middle,
                Label = "Middle of track",
            });

            double nearOutro = Math.Max(0, (timeline.EndSample - 3.0 * sampleRate) / sampleRate);
            points.Add(new RepresentativePoint
            {
                Kind = "near-outro",
                TimeSeconds = nearOutro,
                Label = "Near outro",
            });
        }

        return points;
    }

    private static long? FirstEventSample(VisualizationTimeline timeline)
    {
        long? first = null;
        foreach (NoteEvent note in timeline.Notes)
            first = Min(first, note.StartSample);
        foreach (RhythmEvent rhythm in timeline.Rhythm)
            first = Min(first, rhythm.SamplePosition);
        foreach (SamplePlaybackEvent sample in timeline.SamplePlayback)
            first = Min(first, sample.StartSample);
        foreach (NoiseStateEvent noise in timeline.NoiseStates)
            first = Min(first, noise.StartSample);
        return first;
    }

    private static long? Min(long? current, long candidate)
    {
        if (candidate < 0)
            return current;
        return current.HasValue ? Math.Min(current.Value, candidate) : candidate;
    }

    /// <summary>Maximum number of simultaneously sounding notes in any 2 s window.</summary>
    private static RepresentativePoint? FindDensest(VisualizationTimeline timeline, double sampleRate)
    {
        long windowSamples = (long)(DensityWindowSeconds * sampleRate);
        long[] starts = timeline.Notes.Select(n => n.StartSample).OrderBy(x => x).ToArray();
        long[] ends = timeline.Notes.Select(n => Math.Max(n.StartSample, n.EndSample)).OrderBy(x => x).ToArray();
        if (starts.Length == 0)
            return null;

        long[] candidates = starts.Concat(ends).Distinct().ToArray();

        long bestStart = 0;
        int bestCount = 0;
        foreach (long start in candidates)
        {
            // Notes overlapping [start, start + window): started before the end
            // of the window and end after the start of the window.
            int active = CountLessThan(starts, start + windowSamples) - CountLessThanOrEqual(ends, start);
            if (active > bestCount)
            {
                bestCount = active;
                bestStart = start;
            }
        }

        if (bestCount == 0)
            return null;

        return new RepresentativePoint
        {
            Kind = "densest",
            TimeSeconds = bestStart / sampleRate,
            Label = $"Densest section — {bestCount} simultaneous notes",
        };
    }

    /// <summary>
    /// The 2 s window over <see cref="NoteEvent.InitialMidiNote"/> values with
    /// the widest pitch span (sweep-line over note-start anchors).
    /// </summary>
    private static RepresentativePoint? FindWidestPitch(VisualizationTimeline timeline, double sampleRate)
    {
        long windowSamples = (long)(DensityWindowSeconds * sampleRate);
        List<NoteEvent> notes = timeline.Notes
            .Where(n => double.IsFinite(n.InitialMidiNote))
            .OrderBy(n => n.StartSample)
            .ToList();
        if (notes.Count == 0)
            return null;

        var byEnd = notes.OrderBy(n => n.EndSample).ToArray();
        var active = new SortedDictionary<double, int>();
        int addIndex = 0;
        int removeIndex = 0;
        double bestSpan = -1;
        long bestStart = 0;

        foreach (NoteEvent anchor in notes)
        {
            long start = anchor.StartSample;
            long windowEnd = start + windowSamples;

            while (addIndex < notes.Count && notes[addIndex].StartSample < windowEnd)
            {
                Add(active, notes[addIndex].InitialMidiNote);
                addIndex++;
            }
            while (removeIndex < byEnd.Length && byEnd[removeIndex].EndSample <= start)
            {
                Remove(active, byEnd[removeIndex].InitialMidiNote);
                removeIndex++;
            }

            if (active.Count > 0)
            {
                double span = active.Last().Key - active.First().Key;
                if (span > bestSpan)
                {
                    bestSpan = span;
                    bestStart = start;
                }
            }
        }

        if (bestSpan < 0)
            return null;

        return new RepresentativePoint
        {
            Kind = "widest-pitch",
            TimeSeconds = bestStart / sampleRate,
            Label = $"Widest pitch range ({bestSpan:F1} semitones)",
        };
    }

    private static void Add(SortedDictionary<double, int> active, double midi)
    {
        active[midi] = active.TryGetValue(midi, out int count) ? count + 1 : 1;
    }

    private static void Remove(SortedDictionary<double, int> active, double midi)
    {
        if (!active.TryGetValue(midi, out int count))
            return;
        if (count <= 1)
            active.Remove(midi);
        else
            active[midi] = count - 1;
    }

    private static int CountLessThan(long[] sorted, long value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int CountLessThanOrEqual(long[] sorted, long value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (sorted[mid] <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
