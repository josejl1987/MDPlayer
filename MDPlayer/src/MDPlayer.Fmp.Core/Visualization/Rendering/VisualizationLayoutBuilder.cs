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

        var geometry = new OverlayLayout(
            settings.Width,
            settings.Height,
            settings.PastSeconds,
            settings.FutureSeconds,
            topology.Panels.Count,
            VisualizationLayoutMode.Diagnostic,
            settings.ScopeHeight,
            settings.TimelineHeight,
            settings.RollZoom,
            settings.ScopeRatio,
            settings.ScopePosition);

        // Low-level video-pipeline fixtures may intentionally use an empty
        // timeline. Publishing paths reject those captures before rendering;
        // keep the renderer-neutral fixture boundary constructible while
        // validating every renderable layout here.
        if (VisualizationContentAvailability.HasRenderableContent(timeline))
            VisualizationLayoutValidator.Validate(geometry, topology);

        return new ResolvedVisualizationLayout(
            VisualizationLayoutMode.Diagnostic,
            topology,
            geometry);
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

        return filtered.Length > 0
            ? new VisualizationTopology(filtered)
            : topology;
    }
}
