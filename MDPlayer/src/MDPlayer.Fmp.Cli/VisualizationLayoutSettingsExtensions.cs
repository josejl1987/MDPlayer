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
                    // Custom: start from the full channel set; the include/
                    // exclude lists below select the subset.
                    TrackSelectionMode.Custom => VisualizationChannelFilter.All,
                    _ => VisualizationChannelFilter.Active,
                },
            VisualizationGroupBy.None,
            // Include/exclude lists are only meaningful in Custom selection
            // mode. Forwarding them under Active/All means a previously
            // selected custom subset silently keeps filtering after the user
            // switches modes.
            IncludeTracks: request.Tracks.Selection == TrackSelectionMode.Custom
                ? request.Tracks.IncludedIds.ToList()
                : Array.Empty<string>(),
            ExcludeTracks: request.Tracks.Selection == TrackSelectionMode.Custom
                ? request.Tracks.ExcludedIds.ToList()
                : Array.Empty<string>());
    }


}
