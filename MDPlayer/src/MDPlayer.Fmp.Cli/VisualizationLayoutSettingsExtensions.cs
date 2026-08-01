using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

internal static class VisualizationLayoutSettingsExtensions
{
    public static VisualizationLayoutSettings ToLayoutSettings(this VisualizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new VisualizationLayoutSettings(
            options.Width,
            options.Height,
            options.PastSeconds,
            options.FutureSeconds,
            options.RollZoom,
            options.ScopeHeight,
            options.TimelineHeight,
            options.ScopeRatio,
            options.ScopePosition,
            options.Channels,
            options.GroupBy,
            options.IncludeTracks,
            options.ExcludeTracks);
    }
}
