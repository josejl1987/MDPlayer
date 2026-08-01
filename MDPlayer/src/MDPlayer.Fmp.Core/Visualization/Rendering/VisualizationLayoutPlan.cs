using System.Text.Json;

namespace Fmp.Core.Visualization.Rendering;

internal sealed record VisualizationLayoutTrackPlan(
    string Id,
    string Label,
    string Kind,
    string PitchSystem,
    bool Selected,
    string ExclusionReason,
    string[] SourceVoiceIds,
    string[] ScopeStemIds,
    double? MinimumMidi,
    double? MaximumMidi,
    double? CameraMinimumMidi,
    double? CameraMaximumMidi,
    double LeadRoleConfidence,
    double SalienceScore);

internal sealed record VisualizationLayoutRegionPlan(
    string Id,
    string Kind,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>
/// Immutable, serializable explanation of the prepared presentation. It is
/// deliberately built before frame rendering so `--print-layout` and
/// `--layout-json` describe the same topology consumed by the compositor.
/// </summary>
internal sealed record VisualizationLayoutPlan(
    string RequestedLayout,
    string SelectedLayout,
    string ChannelFilter,
    int Width,
    int Height,
    int PanelCount,
    int Columns,
    int Rows,
    int HeaderHeight,
    int ScopeHeight,
    int TimelineHeight,
    double PastSeconds,
    double FutureSeconds,
    string GroupBy,
    string TimeGrid,
    string ScopePosition,
    double? ScopeRatio,
    long EstimatedFrameCount,
    string SelectedEncoder,
    string AnalysisAvailability,
    IReadOnlyList<VisualizationLayoutTrackPlan> Tracks,
    IReadOnlyList<VisualizationLayoutRegionPlan> Regions)
{
    public static VisualizationLayoutPlan Create(
        VisualizationTimeline timeline,
        VisualizationTopology topology,
        OverlayLayout layout,
        VisualizationLayoutMode requestedLayout,
        VisualizationChannelFilter channelFilter,
        VisualizationGroupBy groupBy = VisualizationGroupBy.None,
        VisualizationTimeGrid timeGrid = VisualizationTimeGrid.None,
        VisualizationScopePosition scopePosition = VisualizationScopePosition.Bottom,
        double? scopeRatio = null,
        int fpsNumerator = 60,
        int fpsDenominator = 1,
        string selectedEncoder = "auto",
        string analysisAvailability = "none")
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(layout);

        var selected = topology.Panels
            .SelectMany(panel => panel.VoiceIds.Concat(panel.OperatorVoiceIds))
            .ToHashSet(StringComparer.Ordinal);
        OverlayScene prepared = OverlaySceneBuilder.Build(timeline, layout, topology);
        VisualizationLayoutTrackPlan[] tracks = timeline.Voices.Count > 0
            ? timeline.Voices
                .OrderBy(voice => voice.Order)
                .ThenBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
                .Select(voice =>
                {
                    string id = voice.Id.ToString();
                    bool included = selected.Contains(id);
                    (double? minimum, double? maximum) = GetPitchBounds(
                        timeline,
                        [id],
                        voice.PitchSystem,
                        voice.RelativePitchAnchorMidi);
                    (double? cameraMinimum, double? cameraMaximum) = GetCameraBounds(
                        prepared,
                        layout,
                        id,
                        timeline,
                        fpsNumerator,
                        fpsDenominator);
                    VisualizationTrackDescriptor descriptor = prepared.Panels
                        .Select(panel => panel.Track)
                        .FirstOrDefault(track => track.SourceVoiceIds.Contains(id, StringComparer.Ordinal));
                    return new VisualizationLayoutTrackPlan(
                        id,
                        voice.DisplayName,
                        TrackKindFor(voice),
                        voice.SupportsPitch
                            ? voice.PitchSystem.ToString()
                            : PitchCoordinateSystem.None.ToString(),
                        included,
                        included ? null : channelFilter == VisualizationChannelFilter.All
                            ? "layout grouping"
                            : "inactive or filtered",
                        [id],
                        [id],
                        minimum,
                        maximum,
                        cameraMinimum,
                        cameraMaximum,
                        descriptor?.LeadRoleConfidence ?? Math.Clamp(voice.LeadRoleConfidence ?? 0, 0, 1),
                        descriptor?.SalienceScore ?? 1.0);
                })
                .ToArray()
            : topology.Panels
                .Select(panel =>
                {
                    VisualizationTrackKind kind = TrackKindFor(panel.Schema);
                    (double? minimum, double? maximum) = GetPitchBounds(timeline, panel.VoiceIds.Concat(panel.OperatorVoiceIds).ToArray());
                    (double? cameraMinimum, double? cameraMaximum) = GetCameraBounds(
                        prepared,
                        layout,
                        panel.Id,
                        timeline,
                        fpsNumerator,
                        fpsDenominator);
                    return new VisualizationLayoutTrackPlan(
                        panel.Id,
                        panel.Label,
                        kind.ToString(),
                        IsPitched(kind) ? PitchCoordinateSystem.AbsoluteMidi.ToString() : PitchCoordinateSystem.None.ToString(),
                        true,
                        null,
                        panel.VoiceIds.ToArray(),
                        panel.VoiceIds.ToArray(),
                        minimum,
                        maximum,
                        cameraMinimum,
                        cameraMaximum,
                        0,
                        1.0);
                })
                .ToArray();

        var regions = new List<VisualizationLayoutRegionPlan>
        {
            Region("top-bar", "metadata", layout.TopBarRect),
            Region("bottom-bar", "progress", layout.BottomBarRect),
        };
        for (int index = 0; index < topology.Panels.Count; index++)
        {
            OverlayRect panel = layout.GetPanelRect(index);
            regions.Add(Region($"panel.{index + 1}", "panel", panel));
            regions.Add(Region($"panel.{index + 1}.header", "header", layout.GetHeaderRect(index)));
            if (layout.HasRoll)
                regions.Add(Region($"panel.{index + 1}.timeline", "semantic", layout.GetTimelineRect(index)));
            if (layout.HasScopes && !layout.IsSharedComposition)
                regions.Add(Region($"panel.{index + 1}.scope", "scope", layout.GetScopeRect(index)));
        }
        if (layout.HasScopes && layout.IsSharedComposition)
            regions.Add(Region("shared-scope", "scope", layout.SharedScopeRect));

        double frameRate = fpsNumerator / (double)Math.Max(1, fpsDenominator);
        long estimatedFrames = frameRate > 0 && timeline.SampleRate > 0
            ? (long)Math.Ceiling(Math.Max(0, timeline.EndSample - timeline.StartSample)
                * frameRate / timeline.SampleRate)
            : 0;

        return new VisualizationLayoutPlan(
            VisualizationLayoutNames.ToCliName(requestedLayout),
            VisualizationLayoutNames.ToCliName(layout.Mode),
            channelFilter.ToString(),
            layout.Width,
            layout.Height,
            layout.PanelCount,
            layout.ColumnCount,
            layout.RowCount,
            layout.PanelHeaderHeight,
            layout.ScopeHeight,
            layout.TimelineHeight,
            layout.PastSeconds,
            layout.FutureSeconds,
            groupBy.ToString(),
            timeGrid.ToString(),
            scopePosition.ToString(),
            scopeRatio,
            estimatedFrames,
            selectedEncoder,
            analysisAvailability,
            tracks,
            regions);
    }

    private static VisualizationLayoutRegionPlan Region(string id, string kind, OverlayRect rect)
        => new(id, kind, rect.X, rect.Y, rect.Width, rect.Height);

    private static (double? Minimum, double? Maximum) GetPitchBounds(
        VisualizationTimeline timeline,
        IReadOnlyCollection<string> voiceIds,
        PitchCoordinateSystem pitchSystem = PitchCoordinateSystem.AbsoluteMidi,
        double? relativeAnchorMidi = null)
    {
        var values = new List<double>();
        foreach (NoteEvent note in timeline.Notes.Where(note => voiceIds.Contains(note.ChannelId)))
        {
            double initial = pitchSystem == PitchCoordinateSystem.FrequencyHz
                ? note.InitialFrequencyHz
                : note.InitialMidiNote;
            if (PitchCoordinateConverter.TryConvertToMidi(
                    pitchSystem,
                    initial,
                    relativeAnchorMidi,
                    out double initialMidi)
                && initialMidi >= 0)
            {
                values.Add(initialMidi);
            }

            foreach (PitchChange point in note.Pitch ?? Array.Empty<PitchChange>())
            {
                double value = pitchSystem == PitchCoordinateSystem.FrequencyHz
                    ? point.FrequencyHz
                    : point.MidiNote;
                if (PitchCoordinateConverter.TryConvertToMidi(
                        pitchSystem,
                        value,
                        relativeAnchorMidi,
                        out double pointMidi)
                    && pointMidi >= 0)
                {
                    values.Add(pointMidi);
                }
            }
        }

        return values.Count == 0 ? (null, null) : (values.Min(), values.Max());
    }

    private static (double? Minimum, double? Maximum) GetCameraBounds(
        OverlayScene scene,
        OverlayLayout layout,
        string trackId,
        VisualizationTimeline timeline,
        int fpsNumerator,
        int fpsDenominator)
    {
        PreparedPanel panel = scene.Panels.FirstOrDefault(value =>
            value.Id == trackId
            || value.Track.SourceVoiceIds.Contains(trackId, StringComparer.Ordinal));
        if (panel == null || panel.CameraNotes.Length == 0)
            return (null, null);

        bool extended = panel.Track.Kind == VisualizationTrackKind.FmOperatorGroup;
        int laneHeight = layout.GetPitchedLaneRect(panel.Index, extended).Height;
        var camera = new PitchCamera(
            panel.CameraNotes,
            laneHeight,
            timeline.SampleRate,
            layout.PastSeconds,
            layout.FutureSeconds,
            timeline.StartSample,
            timeline.EndSample,
            fpsNumerator,
            fpsDenominator,
            allowExtendedSpan: extended,
            rollZoom: layout.RollZoom);
        (double min, double max) = camera.GetPreciseRange(timeline.StartSample);
        return (min, max);
    }

    private static string TrackKindFor(VoiceDescriptor voice) => voice.Presentation switch
    {
        VoicePresentationKind.Percussion => VisualizationTrackKind.Percussion.ToString(),
        VoicePresentationKind.Noise => VisualizationTrackKind.Noise.ToString(),
        VoicePresentationKind.Pcm => VisualizationTrackKind.Sample.ToString(),
        VoicePresentationKind.Wavetable => VisualizationTrackKind.WaveTable.ToString(),
        VoicePresentationKind.Aggregate => VisualizationTrackKind.AggregateActivity.ToString(),
        VoicePresentationKind.Fm3 => VisualizationTrackKind.FmOperatorGroup.ToString(),
        _ when voice.SupportsPitch => VisualizationTrackKind.Pitched.ToString(),
        _ => VisualizationTrackKind.Unsupported.ToString(),
    };

    private static VisualizationTrackKind TrackKindFor(PanelPresentationSchema schema) => schema switch
    {
        PanelPresentationSchema.PitchedLane => VisualizationTrackKind.Pitched,
        PanelPresentationSchema.FmOperatorGroup => VisualizationTrackKind.FmOperatorGroup,
        PanelPresentationSchema.NoiseLane => VisualizationTrackKind.Noise,
        PanelPresentationSchema.PercussionRows => VisualizationTrackKind.Percussion,
        PanelPresentationSchema.SampleLane => VisualizationTrackKind.Sample,
        PanelPresentationSchema.WaveTableLane => VisualizationTrackKind.WaveTable,
        PanelPresentationSchema.AggregateActivity => VisualizationTrackKind.AggregateActivity,
        _ => VisualizationTrackKind.Unsupported,
    };

    private static bool IsPitched(VisualizationTrackKind kind)
        => kind is VisualizationTrackKind.Pitched
            or VisualizationTrackKind.FmOperatorGroup
            or VisualizationTrackKind.WaveTable;

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
}
