namespace Fmp.Core.Visualization.Rendering;

internal enum VisualizationTimeGridLineKind
{
    Subdivision,
    Beat,
    Measure,
}

internal readonly record struct VisualizationTimeGridLine(
    long Sample,
    VisualizationTimeGridLineKind Kind,
    bool Analytical);

internal static class VisualizationTimeGridBuilder
{
    public static VisualizationTimeGridLine[] Build(
        VisualizationTimeline timeline,
        VisualizationTimeGrid mode)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (mode == VisualizationTimeGrid.None)
            return Array.Empty<VisualizationTimeGridLine>();

        bool authoritative = timeline.Beats.Length > 0;
        if (mode == VisualizationTimeGrid.Authoritative && !authoritative)
            return Array.Empty<VisualizationTimeGridLine>();

        if (authoritative && mode is VisualizationTimeGrid.Automatic or VisualizationTimeGrid.Authoritative)
        {
            BeatEvent[] beats = timeline.Beats
                .Where(value => value.SamplePosition >= timeline.StartSample
                    && value.SamplePosition <= timeline.EndSample
                    && double.IsFinite(value.BeatIndex))
                .OrderBy(value => value.SamplePosition)
                .ToArray();
            var authoritativeLines = new List<VisualizationTimeGridLine>(beats.Length * 4);
            for (int index = 0; index < beats.Length; index++)
            {
                if (index > 0)
                {
                    long previous = beats[index - 1].SamplePosition;
                    long current = beats[index].SamplePosition;
                    long interval = current - previous;
                    if (interval >= 4)
                    {
                        for (int subdivision = 1; subdivision < 4; subdivision++)
                        {
                            long subdivisionSample = previous + (long)Math.Round(interval * subdivision / 4.0);
                            if (subdivisionSample > previous && subdivisionSample < current)
                                authoritativeLines.Add(new VisualizationTimeGridLine(
                                    subdivisionSample,
                                    VisualizationTimeGridLineKind.Subdivision,
                                    Analytical: false));
                        }
                    }
                }

                BeatEvent beatMarker = beats[index];
                authoritativeLines.Add(new VisualizationTimeGridLine(
                    beatMarker.SamplePosition,
                    IsMeasure(beatMarker.BeatIndex)
                        ? VisualizationTimeGridLineKind.Measure
                        : VisualizationTimeGridLineKind.Beat,
                    Analytical: false));
            }
            return authoritativeLines
                .OrderBy(value => value.Sample)
                .ThenBy(value => value.Kind)
                .DistinctBy(value => value.Sample)
                .ToArray();
        }

        double? bpm = timeline.Timing
            .Select(value => value.ValidatedBpm)
            .FirstOrDefault(value => value is > 0 and <= 1000);
        if (bpm is not > 0)
            return Array.Empty<VisualizationTimeGridLine>();

        double samplesPerBeat = timeline.SampleRate * 60.0 / bpm.Value;
        if (!double.IsFinite(samplesPerBeat) || samplesPerBeat < 1)
            return Array.Empty<VisualizationTimeGridLine>();

        long start = Math.Max(timeline.StartSample, timeline.StartSample - (long)Math.Ceiling(samplesPerBeat));
        long end = timeline.EndSample;
        int capacity = (int)Math.Min(100_000, Math.Max(1, (end - start) / Math.Max(1, (long)samplesPerBeat) + 2));
        var lines = new List<VisualizationTimeGridLine>(capacity);
        long sample = start;
        int beat = 0;
        while (sample <= end && lines.Count < 100_000)
        {
            if (sample >= timeline.StartSample)
            {
                lines.Add(new VisualizationTimeGridLine(
                    sample,
                    beat % 4 == 0
                        ? VisualizationTimeGridLineKind.Measure
                        : VisualizationTimeGridLineKind.Beat,
                    Analytical: true));
            }
            beat++;
            sample = timeline.StartSample + (long)Math.Round(beat * samplesPerBeat);
        }
        return lines.ToArray();
    }

    private static bool IsMeasure(double beatIndex)
    {
        double nearest = Math.Round(beatIndex);
        return Math.Abs(beatIndex - nearest) < 0.001
            && ((long)nearest % 4 == 0);
    }
}
