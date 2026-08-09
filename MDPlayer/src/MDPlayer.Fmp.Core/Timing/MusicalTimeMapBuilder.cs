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
        if (beatOffsetQuarter is null && options.BeatOffsetSamples is not null)
        {
            // Convert samples→quarters using the best-known tempo so far. The offset
            // is SIGNED and any set value (including 0) is an explicit phase override:
            // negative lands a pickup before quarter 0, +0 pins quarter 0 at sample 0,
            // positive pushes quarter 0 ahead of sample 0. Samples cannot be converted
            // without a derivable tempo, so rather than silently dropping the override
            // (MEM009) we fail with an actionable error naming the missing input.
            double? spq = EffectiveSamplesPerQuarter(timeline, options);
            if (spq is > 0)
            {
                beatOffsetQuarter = options.BeatOffsetSamples.Value / spq.Value;
            }
            else
            {
                throw new MusicalTimingException(
                    $"cannot apply --beat-offset-samples: no derivable tempo to convert sample " +
                    $"offset {options.BeatOffsetSamples.Value} to a quarter position (supply --bpm " +
                    "or a driver-validated BPM)");
            }
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
                options.DetectTempoChanges,
                timeline.StartSample);
        }
        else
        {
            // Constant grid from validated BPM / override. When the driver supplied
            // validated tempo transitions (≥2 credible changes), prefer building
            // distinct continuous segments from them (T016), not just the first BPM.
            double? gridBpm = fixedBpm ?? FirstValidatedBpm(timeline);
            fit = BeatGridFitter.Fit(
                anchors,
                timeline.SampleRate,
                tempoChanges: source == TimingSource.DriverValidatedTempo ? tempoChanges : null,
                fixedBpm: gridBpm,
                phaseQuarterAtSampleZero: HasPhaseOverride(options, beatOffsetQuarter) ? beatOffsetQuarter : null,
                detectTempoChanges: false,
                timelineStartSample: timeline.StartSample);
            if (fit.Diagnostics.TempoSource != TimingSource.DriverValidatedTempo)
                fit.Diagnostics.TempoSource = source == TimingSource.UserOverride
                    ? TimingSource.UserOverride
                    : TimingSource.DriverValidatedTempo;
        }
        if (!HasPhaseOverride(options, beatOffsetQuarter))
        {
            // Without an explicit phase, the anchor path owns phase; a single beat
            // anchor can still establish an authoritative phase when a validated
            // BPM supplies the rate (§10 rate+phase rule). Only force PhaseUnknown
            // when the fit genuinely has no phase evidence at all (no anchors and
            // no derived phase) rather than whenever anchors.Length < 2 — the
            // validated-tempo path derives phase from one anchor via the fitter.
            if (anchors.Length < 2 && fit.Diagnostics.SampleZeroQuarter is null)
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

        // Reflect meter/downbeat status so the diagnostics explain why alignment is
        // (or is not) bar-aligned (§15).
        fit.Diagnostics.MeterKnown = options.Meter is not null;
        fit.Diagnostics.DownbeatKnown = map.FirstDownbeatQuarter is not null;

        return new MusicalTimeMapBuildResult { Map = map, Diagnostics = fit.Diagnostics };
    }

    private static string NotTrustworthyMessage(TimingDiagnostics diagnostics)
    {
        if (diagnostics.PhaseUnknown)
            return "strict-timing: beat phase is unknown; supply --beat-offset-samples or --bpm with anchors";
        if (diagnostics.HasConflictingAnchors)
            return "strict-timing: conflicting beat anchors at the same sample; cannot align (no averaging performed)";
        if (diagnostics.RejectedAnchors.Count > 0)
            return "strict-timing: beat anchors were rejected as outliers; residuals are excessive or conflicting";
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

        // A fixed-BPM override is a user override regardless of anchors. A beat-phase
        // override (BeatOffsetQuarter / BeatOffsetSamples) selects the phase dimension
        // ONLY; it must never convert authoritative driver tempo into UserOverride
        // (D005). BeatGridFitter consumes the phase separately (PhaseSource=UserOverride)
        // while this method still picks the tempo source from the strongest timing
        // evidence (driver anchors > validated BPM > inference).
        if (options.FixedBpm is not null)
            return TimingSource.UserOverride;

        if (anchors.Length >= 2)
            return TimingSource.DriverBeatAnchors;

        if (FirstValidatedBpm(timeline) is > 0)
            return TimingSource.DriverValidatedTempo;

        return TimingSource.SymbolicInference;
    }

    private static bool HasPhaseOverride(MusicalTimeMapOptions options, double? beatOffsetQuarter) =>
        options.BeatOffsetQuarter is not null
        || options.BeatOffsetSamples is not null
        || beatOffsetQuarter is not null;

    /// <summary>
    /// Validates that every segment's tempo is representable as a MIDI Set Tempo
    /// 24-bit µs-per-quarter value (<c>round(60_000_000 / BPM)</c> in
    /// [1, 0xFFFFFF]). Unrepresentable tempos (extreme BPM) fail with an
    /// actionable error rather than being silently clamped (§19).
    /// </summary>
    private static void ValidateTempoRepresentation(SegmentFit[] fits)
    {
        const int MaxUsPerQuarter = 0xFFFFFF; // 16_777_215
        foreach (SegmentFit fit in fits)
        {
            if (fit.BeatsPerMinute <= 0 || !double.IsFinite(fit.BeatsPerMinute))
                throw new MusicalTimingException(
                    $"cannot represent tempo: segment at sample {fit.StartSample} has invalid BPM " +
                    $"{fit.BeatsPerMinute:0.###}");
            double us = Math.Round(60_000_000.0 / fit.BeatsPerMinute);
            if (us < 1 || us > MaxUsPerQuarter)
            {
                throw new MusicalTimingException(
                    $"tempo {fit.BeatsPerMinute:0.####} BPM at sample {fit.StartSample} maps to " +
                    $"{us:0} µs/quarter, outside the MIDI Set Tempo 24-bit range (1..{MaxUsPerQuarter}); " +
                    "choose a tempo MIDI can represent");
            }
        }
    }

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
        ValidateTempoRepresentation(fit.Segments);

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
            // The downbeat's quarter position is the exact position of the sample on
            // the assembled map — no snapping to the nearest meter multiple. Bars are
            // relative to it, so an explicit 2.37 downbeat stays 2.37 (§15).
            firstDownbeatQuarter = ComputeSampleToQuarter(segments, downbeatSample);
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
