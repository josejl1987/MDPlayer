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
        string? backendId = null,
        string? corrExecutablePath = null)
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
                Fps = request.Output.FpsNumerator / (double)request.Output.FpsDenominator,
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
                // Keep the full per-channel panel set (including silent voices)
                // so corrscope's ncols layout produces exactly the RowCount x
                // ColumnCount grid the overlay slices: dropping silent channels
                // shrinks corrscope's row height below ScopeHeight and breaks
                // the grid alignment / frame dimensions.
                IncludeSilentChannels = true,
                HideLabels = true,
                ResDivisor = 1.0,
                Antialiasing = request.Output.Quality != RenderQuality.Draft,
            });
        // A resolved corr executable path (e.g. the managed venv's `corr`
        // wrapper) overrides the user-supplied --corrscope override.
        return new CorrscopeRunner(runtime.ToolTimeoutMinutes, corrExecutablePath ?? runtime.CorrscopePath);
    }
}
