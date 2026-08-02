using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

internal sealed record VisualizationLayoutSettings(
    int Width,
    int Height,
    double PastSeconds,
    double FutureSeconds,
    double RollZoom,
    int? ScopeHeight,
    int? TimelineHeight,
    double? ScopeRatio,
    VisualizationScopePosition ScopePosition,
    VisualizationChannelFilter Channels,
    VisualizationGroupBy GroupBy,
    IReadOnlyList<string> IncludeTracks = null,
    IReadOnlyList<string> ExcludeTracks = null);

internal static class VisualizationLayoutBuilder
{
    public static ResolvedVisualizationLayout Build(
        VisualizationTimeline timeline,
        VisualizationLayoutMode mode,
        VisualizationLayoutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(settings);

        return mode switch
        {
            VisualizationLayoutMode.Diagnostic => BuildDiagnostic(timeline, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static ResolvedVisualizationLayout BuildDiagnostic(
        VisualizationTimeline timeline,
        VisualizationLayoutSettings settings)
    {
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            settings.Channels,
            settings.GroupBy);
        topology = ApplyTrackFiltering(topology, settings);

        int panelCount = topology.Panels.Count;
        int availableWidth = settings.Width;
        int gridHeight = GridHeight(settings.Height);
        int availableHeight = gridHeight;

        // Search the full diagnostic grid first. Relying only on
        // GridForPanelCount would leave twelve panels as a three-column grid
        // no matter how narrow each panel becomes.
        ResolvedPanelGrid? full = OverlayLayout.FindGrid(
            panelCount,
            availableWidth,
            availableHeight,
            minimumPanelWidth: 300,
            minimumPanelHeight: 150);

        if (full is not null)
            return BuildFullDiagnostic(timeline, settings, topology, full.Value);

        // Narrower: the compact per-channel overview keeps a readable panel
        // (channel + accent + current state + compact waveform + activity).
        ResolvedPanelGrid? overview = OverlayLayout.FindGrid(
            panelCount,
            availableWidth,
            availableHeight,
            minimumPanelWidth: 180,
            minimumPanelHeight: 56);

        if (overview is not null)
            return BuildDiagnosticOverview(timeline, settings, topology, overview.Value);

        // Last fallback: group by device rather than render illegible channel
        // rows. Each device panel shows its label, active/total channel count,
        // aggregate waveform and current activity.
        return BuildDeviceOverview(timeline, settings, topology);
    }

    private static int GridHeight(int height)
    {
        int top = Math.Clamp((int)Math.Round(height * (OverlayLayout.DefaultTopBarHeight / 1080.0)), 32, 96);
        int bottom = Math.Clamp((int)Math.Round(height * (OverlayLayout.DefaultBottomBarHeight / 1080.0)), 24, 64);
        return height - top - bottom;
    }

    private static ResolvedVisualizationLayout BuildFullDiagnostic(
        VisualizationTimeline timeline,
        VisualizationLayoutSettings settings,
        VisualizationTopology topology,
        ResolvedPanelGrid grid)
    {
        var geometry = new OverlayLayout(
            settings.Width,
            settings.Height,
            settings.PastSeconds,
            settings.FutureSeconds,
            topology.Panels.Count,
            VisualizationLayoutMode.Diagnostic,
            grid.Columns,
            grid.Rows,
            VisualizationLayoutVariant.DiagnosticGrid,
            settings.ScopeHeight,
            settings.TimelineHeight,
            settings.RollZoom,
            settings.ScopeRatio,
            settings.ScopePosition);

        if (VisualizationContentAvailability.HasRenderableContent(timeline))
            VisualizationLayoutValidator.Validate(geometry, topology);

        int panelWidth = settings.Width / Math.Max(1, grid.Columns);
        int panelHeight = GridHeight(settings.Height) / Math.Max(1, grid.Rows);
        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            VisualizationLayoutResolver.Decide(panelWidth, panelHeight, geometry.ScopeHeight, rollPossible: true);
        return new ResolvedVisualizationLayout(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutVariant.DiagnosticGrid,
            topology,
            geometry,
            density,
            caps);
    }

    private static ResolvedVisualizationLayout BuildDiagnosticOverview(
        VisualizationTimeline timeline,
        VisualizationLayoutSettings settings,
        VisualizationTopology topology,
        ResolvedPanelGrid grid)
    {
        var geometry = new OverlayLayout(
            settings.Width,
            settings.Height,
            settings.PastSeconds,
            settings.FutureSeconds,
            topology.Panels.Count,
            VisualizationLayoutMode.Diagnostic,
            grid.Columns,
            grid.Rows,
            VisualizationLayoutVariant.DiagnosticOverview,
            settings.ScopeHeight,
            settings.TimelineHeight,
            settings.RollZoom,
            settings.ScopeRatio,
            settings.ScopePosition);

        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            VisualizationLayoutResolver.Decide(
                settings.Width / Math.Max(1, grid.Columns),
                GridHeight(settings.Height) / Math.Max(1, grid.Rows),
                geometry.ScopeHeight,
                rollPossible: true);
        return new ResolvedVisualizationLayout(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutVariant.DiagnosticOverview,
            topology,
            geometry,
            density,
            caps);
    }

    private static ResolvedVisualizationLayout BuildDeviceOverview(
        VisualizationTimeline timeline,
        VisualizationLayoutSettings settings,
        VisualizationTopology topology)
    {
        // Collapse the topology to one panel per device. Each device panel
        // aggregates all its channels; only devices with at least one selected
        // channel are kept.
        VisualizationTopology deviceTopology = GroupByDevice(topology, timeline);
        int panelCount = deviceTopology.Panels.Count;

        ResolvedPanelGrid? grid = OverlayLayout.FindGrid(
            panelCount,
            settings.Width,
            GridHeight(settings.Height),
            minimumPanelWidth: 180,
            minimumPanelHeight: 56);
        int columns = grid?.Columns ?? Math.Max(1, (int)Math.Round(Math.Sqrt(panelCount)));
        int rows = grid?.Rows ?? (panelCount + columns - 1) / columns;

        var geometry = new OverlayLayout(
            settings.Width,
            settings.Height,
            settings.PastSeconds,
            settings.FutureSeconds,
            panelCount,
            VisualizationLayoutMode.Diagnostic,
            columns,
            rows,
            VisualizationLayoutVariant.DeviceOverview,
            settings.ScopeHeight,
            settings.TimelineHeight,
            settings.RollZoom,
            settings.ScopeRatio,
            settings.ScopePosition);

        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            VisualizationLayoutResolver.Decide(
                settings.Width / Math.Max(1, columns),
                GridHeight(settings.Height) / Math.Max(1, rows),
                geometry.ScopeHeight,
                rollPossible: false);
        return new ResolvedVisualizationLayout(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutVariant.DeviceOverview,
            deviceTopology,
            geometry,
            density,
            caps);
    }

    private static VisualizationTopology GroupByDevice(
        VisualizationTopology topology,
        VisualizationTimeline timeline)
    {
        var voiceByDevice = new Dictionary<string, VoiceDescriptor>(StringComparer.Ordinal);
        foreach (VoiceDescriptor voice in timeline.Voices)
            voiceByDevice[voice.Id.ToString()] = voice;

        return topology.Panels
            .GroupBy(panel =>
            {
                string deviceId = null;
                foreach (string id in panel.VoiceIds)
                {
                    if (voiceByDevice.TryGetValue(id, out VoiceDescriptor voice))
                    {
                        deviceId = voice.DeviceId.ToString();
                        break;
                    }
                }
                return deviceId ?? panel.Id;
            }, StringComparer.Ordinal)
            .OrderBy(group => group.Min(panel => panel.Order))
            .Select((group, index) =>
            {
                VisualizationPanel first = group.First();
                string[] voiceIds = group.SelectMany(p => p.VoiceIds)
                    .Distinct(StringComparer.Ordinal).ToArray();
                string[] operatorIds = group.SelectMany(p => p.OperatorVoiceIds)
                    .Distinct(StringComparer.Ordinal).ToArray();
                string label = DeviceLabel(timeline, first);
                return new VisualizationPanel(
                    $"device.{index + 1}",
                    label,
                    PreparedPanelKind.Aggregate,
                    PanelContentKind.DeviceAggregate,
                    index,
                    voiceIds,
                    operatorIds)
                {
                    Schema = PanelPresentationSchema.AggregateActivity,
                    Rows = Array.Empty<PanelRowDefinition>(),
                };
            })
            .ToArray()
            is { Length: > 0 } panels
                ? new VisualizationTopology(panels)
                : throw new InvalidOperationException("Device overview requires at least one panel.");
    }

    private static string DeviceLabel(VisualizationTimeline timeline, VisualizationPanel panel)
    {
        var voiceById = timeline.Voices.ToDictionary(v => v.Id.ToString(), StringComparer.Ordinal);
        foreach (string id in panel.VoiceIds)
        {
            if (voiceById.TryGetValue(id, out VoiceDescriptor voice))
            {
                return timeline.Devices
                    .FirstOrDefault(device => device.Id == voice.DeviceId)?.DisplayName
                    ?? voice.DeviceId.ToString();
            }
        }
        return panel.Label;
    }

    private static VisualizationTopology ApplyTrackFiltering(
        VisualizationTopology topology,
        VisualizationLayoutSettings settings)
    {
        IReadOnlyList<string> excludedTracks = settings.ExcludeTracks ?? Array.Empty<string>();
        IReadOnlyList<string> includedTracks = settings.IncludeTracks ?? Array.Empty<string>();
        if (excludedTracks.Count == 0 && includedTracks.Count == 0)
            return topology;

        var exclude = new HashSet<string>(excludedTracks, StringComparer.Ordinal);
        var include = new HashSet<string>(includedTracks, StringComparer.Ordinal);

        bool Excluded(VisualizationPanel panel)
            => exclude.Contains(panel.Id)
                || panel.VoiceIds.Any(exclude.Contains)
                || panel.OperatorVoiceIds.Any(exclude.Contains);

        bool Included(VisualizationPanel panel)
            => include.Contains(panel.Id)
                || panel.VoiceIds.Any(include.Contains)
                || panel.OperatorVoiceIds.Any(include.Contains);

        VisualizationPanel[] filtered = topology.Panels
            .Where(panel => !Excluded(panel))
            .Where(panel => includedTracks.Count == 0
                || Included(panel)
                || panel.Id.Contains("master", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (filtered.Length == 0)
        {
            throw new InvalidOperationException(
                "The selected track filters match no visualization panels.");
        }
        return new VisualizationTopology(filtered);
    }
}
