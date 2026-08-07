#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>Result of building a musical time map: the map plus its diagnostics.</summary>
internal sealed class MusicalTimeMapBuildResult
{
    public required MusicalTimeMap Map { get; init; }

    public required TimingDiagnostics Diagnostics { get; init; }
}

/// <summary>
/// Orchestrates the production of a canonical <see cref="MusicalTimeMap"/> from
/// timeline evidence. Enforces the timing-source priority (driver beat anchors,
/// validated tempo, user override, symbolic inference) so that the MIDI writer
/// never reinterprets "BPM known" as "grid known".
/// </summary>
internal static class MusicalTimeMapBuilder
{
    /// <summary>
    /// Builds the map. When <paramref name="options"/> provides a fixed tempo
    /// override it is honoured; otherwise the strongest timeline evidence drives
    /// the grid. Phase is only claimed when anchors or an explicit offset settle it.
    /// </summary>
    public static MusicalTimeMapBuildResult Build(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(options);
        if (timeline.SampleRate <= 0)
            throw new ArgumentException("timeline sample rate must be positive");

        // Phase override priority: explicit quarter wins; else samples converted.
        double? beatOffsetQuarter = options.BeatOffsetQuarter;
        if (beatOffsetQuarter is null && options.BeatOffsetSamples is > 0)
        {
            // Convert samples→quarters using the best-known tempo so far.
            double? spq = EffectiveSamplesPerQuarter(timeline, options);
            if (spq is > 0)
                beatOffsetQuarter = options.BeatOffsetSamples.Value / spq.Value;
        }

        // Convert beat events into quarter-position anchors.
        BeatAnchor[] anchors = BuildAnchors(timeline, options.QuartersPerBeat);
        TempoChangePoint[] tempoChanges = BuildTempoChanges(timeline);

        TimingSource source = ResolveSource(timeline, options, anchors);

        if (source == TimingSource.SymbolicInference)
        {
            // Batch 3: fall back to onset-driven inference when no symbolic timing
            // evidence exists.
            return SymbolicTempoInference.Build(timeline, options, beatOffsetQuarter);
        }

        // Deterministic source application:
        //  - DriverBeatAnchors: fit anchors (+ tempo changes for Batch 2).
        //  - DriverValidatedTempo / UserOverride: fit the validated BPM grid.
        double? fixedBpm = options.FixedBpm;
        PiecewiseFit fit;
        if (anchors.Length >= 2 && source == TimingSource.DriverBeatAnchors)
        {
            fit = BeatGridFitter.Fit(
                anchors,
                timeline.SampleRate,
                tempoChanges,
                fixedBpm,
                HasPhaseOverride(options, beatOffsetQuarter) ? beatOffsetQuarter : null,
                options.DetectTempoChanges);
        }
        else
        {
            // Constant grid from validated BPM / override.
            double? gridBpm = fixedBpm ?? FirstValidatedBpm(timeline);
            fit = BeatGridFitter.Fit(
                anchors,
                timeline.SampleRate,
                tempoChanges: null,
                fixedBpm: gridBpm,
                phaseQuarterAtSampleZero: HasPhaseOverride(options, beatOffsetQuarter) ? beatOffsetQuarter : null,
                detectTempoChanges: false);
            fit.Diagnostics.TempoSource = source == TimingSource.UserOverride
                ? TimingSource.UserOverride
                : TimingSource.DriverValidatedTempo;
        }
        if (!HasPhaseOverride(options, beatOffsetQuarter))
        {
            // Without an explicit phase, the anchor path owns phase; the constant
            // path with no anchors reports phase unknown.
            if (anchors.Length < 2)
                fit.Diagnostics.PhaseUnknown = true;
        }

        // Propagate the resolved source onto the drive segments so the map and the
        // diagnostics agree about whose evidence produced the tempo.
        if (source == TimingSource.UserOverride)
        {
            foreach (SegmentFit segment in fit.Segments)
            {
                if (segment.Source != TimingSource.SymbolicInference)
                    segment.Source = TimingSource.UserOverride;
            }
        }
        // When there were no beat anchors, the phase cannot have come from them;
        // surface the real source (or unknown) rather than a misleading default.
        if (anchors.Length < 2)
        {
            if (fit.Diagnostics.PhaseUnknown)
                fit.Diagnostics.PhaseSource = source;
            else if (fit.Diagnostics.PhaseSource == TimingSource.DriverBeatAnchors)
                fit.Diagnostics.PhaseSource = source == TimingSource.UserOverride
                    ? TimingSource.UserOverride
                    : TimingSource.DriverValidatedTempo;
        }

        if (options.StrictTiming && !fit.Diagnostics.IsTrustworthy)
        {
            throw new MusicalTimingException(
                NotTrustworthyMessage(fit.Diagnostics));
        }

        MusicalTimeMap map = AssembleMap(timeline, options, fit);
        return new MusicalTimeMapBuildResult { Map = map, Diagnostics = fit.Diagnostics };
    }

    private static string NotTrustworthyMessage(TimingDiagnostics diagnostics)
    {
        if (diagnostics.PhaseUnknown)
            return "strict-timing: beat phase is unknown; supply --beat-offset-samples or --bpm with anchors";
        return "strict-timing: tempo sources are ambiguous or residuals are excessive";
    }

    private static BeatAnchor[] BuildAnchors(VisualizationTimeline timeline, double quartersPerBeat)
    {
        return (timeline.Beats ?? Array.Empty<BeatEvent>())
            .Where(beat => beat is not null
                && double.IsFinite(beat.BeatIndex)
                && beat.SamplePosition >= timeline.StartSample)
            .Select(beat => new BeatAnchor(
                beat.SamplePosition,
                beat.BeatIndex * quartersPerBeat))
            .ToArray();
    }

    private static TempoChangePoint[] BuildTempoChanges(VisualizationTimeline timeline)
    {
        return (timeline.Timing ?? Array.Empty<DriverTimingEvent>())
            .Where(value => value.ValidatedBpm is > 0 && double.IsFinite(value.ValidatedBpm.Value))
            .GroupBy(value => value.SamplePosition)
            .Select(group => group.First())
            .Select(value => new TempoChangePoint(
                value.SamplePosition,
                value.ValidatedBpm!.Value,
                TimingSource.DriverValidatedTempo))
            .OrderBy(value => value.Sample)
            .ToArray();
    }

    private static double? FirstValidatedBpm(VisualizationTimeline timeline) =>
        (timeline.Timing ?? Array.Empty<DriverTimingEvent>())
            .Select(value => value.ValidatedBpm)
            .Where(value => value is > 0 && double.IsFinite(value!.Value))
            .Select(value => value!.Value)
            .FirstOrDefault();

    private static double? EffectiveSamplesPerQuarter(VisualizationTimeline timeline, MusicalTimeMapOptions options)
    {
        if (options.FixedBpm is > 0)
            return timeline.SampleRate * 60.0 / options.FixedBpm.Value;
        double? bpm = FirstValidatedBpm(timeline);
        if (bpm is > 0)
            return timeline.SampleRate * 60.0 / bpm.Value;
        return null;
    }

    private static TimingSource ResolveSource(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        BeatAnchor[] anchors)
    {
        if (options.Source is TimingSource forced)
            return forced;

        // A fixed-BPM override is a user override regardless of anchors.
        if (options.FixedBpm is not null
            || options.BeatOffsetQuarter is not null
            || options.BeatOffsetSamples is not null)
            return TimingSource.UserOverride;

        if (anchors.Length >= 2)
            return TimingSource.DriverBeatAnchors;

        if (FirstValidatedBpm(timeline) is > 0)
            return TimingSource.DriverValidatedTempo;

        return TimingSource.SymbolicInference;
    }

    private static bool HasPhaseOverride(MusicalTimeMapOptions options, double? beatOffsetQuarter) =>
        options.BeatOffsetQuarter is not null
        || options.BeatOffsetSamples is > 0
        || beatOffsetQuarter is not null;

    /// <summary>
    /// Converts the fitted segments into a contiguous <see cref="MusicalTimeMap"/>,
    /// threading the quarter position at each boundary from the preceding segment so
    /// the map is continuous (and MusicalTimeMap's own continuity check passes).
    /// </summary>
    private static MusicalTimeMap AssembleMap(
        VisualizationTimeline timeline,
        MusicalTimeMapOptions options,
        PiecewiseFit fit)
    {
        SegmentFit[] fits = fit.Segments;
        var segments = new TempoSegment[fits.Length];
        long startSample = timeline.StartSample;

        // Phase baseline: anchor the first segment's start.
        double quarterAtStart;
        if (options.BeatOffsetQuarter is double q)
        {
            quarterAtStart = q + (startSample - 0) * (1.0 / fits[0].SamplesPerQuarter);
        }
        else
        {
            // Use the fitted phase at the first segment's start sample.
            quarterAtStart = fits[0].QuarterAtStart
                - (fits[0].StartSample - startSample) / fits[0].SamplesPerQuarter;
        }

        for (int i = 0; i < fits.Length; i++)
        {
            long endSample = i < fits.Length - 1
                ? fits[i + 1].StartSample
                : Math.Max(timeline.EndSample, startSample);

            var segment = new TempoSegment(
                StartSample: startSample,
                EndSample: endSample,
                QuarterPositionAtStart: quarterAtStart,
                SamplesPerQuarter: fits[i].SamplesPerQuarter,
                BeatsPerMinute: fits[i].BeatsPerMinute,
                Source: fits[i].Source,
                Confidence: fits[i].Confidence);
            segments[i] = segment;

            // Thread continuity into the next segment.
            startSample = endSample;
            quarterAtStart = segment.QuarterPositionAtEnd;
        }

        Meter? meter = options.Meter;
        double? firstDownbeatQuarter = null;
        if (options.FirstDownbeatSample is long downbeatSample && meter is not null)
        {
            // Compute the quarter position of the downbeat from the map, snapped to
            // the nearest bar boundary in this meter.
            double downbeatQuarter = ComputeSampleToQuarter(segments, downbeatSample);
            double quartersPerBar = meter.QuartersPerBar;
            firstDownbeatQuarter = Math.Round(downbeatQuarter / quartersPerBar) * quartersPerBar;
        }

        return new MusicalTimeMap(
            timeline.SampleRate,
            startSample: timeline.StartSample,
            segments,
            meter,
            firstDownbeatQuarter);
    }

    private static double ComputeSampleToQuarter(IReadOnlyList<TempoSegment> segments, long sample)
    {
        if (segments.Count == 0)
            return 0;
        if (sample < segments[0].StartSample)
            return segments[0].QuarterPositionAtStart;
        if (sample >= segments[^1].EndSample)
            return segments[^1].QuarterPositionAtEnd;
        int low = 0, high = segments.Count - 1;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (segments[mid].EndSample <= sample)
                low = mid + 1;
            else
                high = mid;
        }
        return segments[low].QuarterPositionAt(sample);
    }
}
