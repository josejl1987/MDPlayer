using System.Diagnostics;
using Fmp.Application.Contracts;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// The single production construction point for <see cref="PanelOverlayRenderer"/>
/// and <see cref="VisualizationFrameRenderer"/>. Every consumer (final video,
/// GUI preview, visual review) builds a frame renderer here so they all use the
/// same overlay options and the same scope source.
/// </summary>
internal static class VisualizationFrameRendererFactory
{
    public static VisualizationFrameRenderer Create(
        PreparedVisualizationSource prepared,
        VisualizationWorkspace workspace,
        RenderRuntimeOptions runtime,
        bool introOutro)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(runtime);

        PanelOverlayRenderer overlay = VisualizationComposition.CreateRenderer(
            prepared.Timeline,
            prepared.Layout,
            VisualizationRendererOptions.Build(
                prepared.Request,
                prepared.Presentation,
                introOutro,
                prepared.Energy));

        CorrscopeFrameSource? scope =
            CreateScopeSource(
                prepared,
                workspace,
                runtime,
                overlay);

        if (scope is null
            && prepared.Scope.Enabled
            && prepared.Layout.Geometry.HasScopes)
        {
            Console.Error.WriteLine(
                "warning: scope source unavailable (Corrscope/Python/bridge missing); " +
                "scope regions will fall back to the internal master waveform, so " +
                "preview does not equal final frames in those regions");
        }

        return new VisualizationFrameRenderer(overlay, scope);
    }

    /// <summary>
    /// Starts the Corrscope raw-frame bridge when scopes are enabled and
    /// Corrscope is available; otherwise returns null (the caller falls back to
    /// the internal master waveform, exactly as final composition does). The
    /// created frame source is ready to read frame 0 on first use.
    /// </summary>
    private static CorrscopeFrameSource? CreateScopeSource(
        PreparedVisualizationSource prepared,
        VisualizationWorkspace workspace,
        RenderRuntimeOptions runtime,
        PanelOverlayRenderer overlay)
    {
        if (!prepared.Scope.Enabled || !prepared.Layout.Geometry.HasScopes)
            return null;

        // Corrscope availability / config only matters if there are scopes.
        if (prepared.Scope.Result is null)
            return null;

        CorrscopeRunner? runner = VisualizationComposition.PrepareCorrscope(
            prepared.Request,
            runtime,
            workspace,
            prepared.Layout,
            prepared.Scope.Result,
            enabled: true,
            backendId: prepared.BackendId);

        if (runner is null || !runner.IsAvailable)
            return null;

        string bridgePath = Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py");
        if (!File.Exists(bridgePath))
            return null;

        string pythonPath = CorrscopeRunner.ResolvePythonPath(runner.CorrPath);
        if (string.IsNullOrWhiteSpace(pythonPath))
            return null;

        return new CorrscopeFrameSource(
            () => runner.StartRawFrames(
                pythonPath,
                bridgePath,
                workspace.CorrscopeConfigPath),
            overlay.ScopeFrameByteCount);
    }
}