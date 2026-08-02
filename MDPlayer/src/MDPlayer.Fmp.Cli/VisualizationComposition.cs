using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Application.Contracts;
using VideoEncoder = Fmp.Core.Visualization.Rendering.VideoEncoder;

namespace Fmp.Cli;

internal static class VisualizationComposition
{
    public static SinglePassComposer CreateComposer(
        RenderRuntimeOptions runtime,
        VideoEncoder encoder,
        string preset,
        string crf)
        => new SinglePassComposer(
            runtime.FfmpegPath,
            new SinglePassComposer.Options
            {
                TimeoutMinutes = runtime.ToolTimeoutMinutes,
                VideoPreset = preset,
                VideoCrf = crf,
                Encoder = encoder,
            });

    public static PanelOverlayRenderer CreateRenderer(
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        PanelOverlayRenderer.Options options)
        => new PanelOverlayRenderer(timeline, layout, options);

    public static CorrscopeRunner? PrepareCorrscope(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        ResolvedVisualizationLayout layout,
        ScopeRenderer.ScopeResult scope,
        bool enabled,
        string? backendId = null)
    {
        if (!enabled || !layout.Geometry.HasScopes)
            return null;
        CorrscopeConfigWriter.Write(
            workspace.CorrscopeConfigPath,
            workspace.ScopeDir,
            scope,
            audioDir: "../audio",
            overrides: new CorrscopeOverrides
            {
                Fps = request.Output.FpsNumerator,
                TriggerMs = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 20,
                RenderMs = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 12,
                EdgeStrength = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 0.35,
                BufferStrength = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 1.0,
                Responsiveness = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 0.25,
                BufferFalloff = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 0.35,
                ResetBelow = string.Equals(backendId, "fmp", StringComparison.Ordinal) ? null : 0.2,
                RenderWidth = layout.Geometry.CorrscopeGridWidth,
                RenderHeight = Math.Max(1, layout.Geometry.CorrscopeGridHeight),
                LayoutNCols = layout.Geometry.ColumnCount,
                IncludeMasterAsChannel = !scope.Stems.Any(s => s.Name != "master" && s.Success),
                IncludeSilentChannels = request.Tracks.Selection == TrackSelectionMode.All,
                HideLabels = true,
                ResDivisor = 1.0,
                Antialiasing = request.Output.Quality != RenderQuality.Draft,
            });
        return new CorrscopeRunner(runtime.ToolTimeoutMinutes, runtime.CorrscopePath);
    }
}
