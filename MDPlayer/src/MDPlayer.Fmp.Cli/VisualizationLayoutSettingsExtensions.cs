using Fmp.Core.Visualization.Rendering;
using Fmp.Application.Contracts;

namespace Fmp.Cli;

internal static class VisualizationLayoutSettingsExtensions
{
    public static VisualizationLayoutSettings ToLayoutSettings(this VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new VisualizationLayoutSettings(
            request.Output.Width,
            request.Output.Height,
            request.View.PastSeconds,
            request.View.FutureSeconds,
            1.0,
            null,
            null,
            null,
            VisualizationScopePosition.Bottom,
            request.Tracks.IncludeInactiveDiagnosticTracks
                ? VisualizationChannelFilter.All
                : request.Tracks.Selection switch
                {
                    TrackSelectionMode.All => VisualizationChannelFilter.All,
                    TrackSelectionMode.Custom => VisualizationChannelFilter.All,
                    _ => VisualizationChannelFilter.Active,
                },
            VisualizationGroupBy.None,
            request.Tracks.IncludedIds.ToList(),
            request.Tracks.ExcludedIds.ToList());
    }


}
