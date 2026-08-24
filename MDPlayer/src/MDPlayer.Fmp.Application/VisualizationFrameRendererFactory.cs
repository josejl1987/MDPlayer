using System.Diagnostics;
using Fmp.Application.Contracts;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// The single production construction point for the overlay renderer (CPU
/// <see cref="PanelOverlayRenderer"/> or GPU <c>GpuPanelRenderer</c>, selected
/// by <c>--render-backend</c>) and <see cref="VisualizationFrameRenderer"/>.
/// Every consumer (final video, GUI preview, visual review) builds a frame
/// renderer here so they all use the same overlay options and the same scope
/// source.
/// </summary>
internal static class VisualizationFrameRendererFactory
{
    /// <summary>
    /// Builds a timeline-only rendering of the captured semantic timeline:
    /// full overlay (notes, rhythm events, titles, layout) plus the master
    /// waveform when the initial capture produced one, but never touching
    /// isolated stems, Channel-energy analysis, Corrscope, Python or FFmpeg.
    /// This is the first usable frame shown before scope/stem preparation
    /// completes.
    /// </summary>
    public static VisualizationFrameRenderer CreateTimelinePreview(
        PreparedTimelineSource prepared,
        bool introOutro,
        string? renderBackend = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        // The timeline preview has no RenderRuntimeOptions of its own; it
        // follows the caller-supplied backend selection exactly like full
        // renders do, so the GUI first frame now rides the same
        // --render-backend path (auto currently = CPU until the GPU beats it; gpu
        // is opt-in)
        // otherwise).
        IFrameOverlayRenderer overlay =
            VisualizationComposition.CreateRenderer(
                prepared.Timeline,
                prepared.Layout,
                VisualizationRendererOptions.Build(
                    prepared.Request,
                    prepared.Presentation,
                    introOutro,
                    Array.Empty<ChannelEnergyEnvelope>()),
                renderBackend);

        IScopeFrameSource? scope = null;

        if (prepared.MasterAudioProduced
            && prepared.Layout.Geometry.HasScopes)
        {
            scope = MasterWaveformFrameSource.TryCreate(
                overlay,
                prepared.MasterAudioPath,
                overlay.FpsNumerator,
                overlay.FpsDenominator);
        }

        return new VisualizationFrameRenderer(
            overlay,
            scope,
            prepared.Request.View.ScopeFps);
    }

    public static VisualizationFrameRenderer Create(
        PreparedVisualizationSource prepared,
        VisualizationWorkspace workspace,
        RenderRuntimeOptions runtime,
        bool introOutro,
        ScopeFrameSourcePolicy scopePolicy = ScopeFrameSourcePolicy.Production)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(runtime);

        // Build the shared overlay first. Performance lanes use a deterministic
        // master source below; diagnostic production renders may still start
        // Corrscope after their scope configuration is ready.
        IScopeFrameSource? scope = null;
        IFrameOverlayRenderer? overlay = null;
        try
        {
            overlay = VisualizationComposition.CreateRenderer(
                prepared.Timeline,
                prepared.Layout,
                VisualizationRendererOptions.Build(
                    prepared.Request,
                    prepared.Presentation,
                    introOutro,
                    prepared.Energy),
                runtime.RenderBackend);

            if (scopePolicy == ScopeFrameSourcePolicy.Production
                && prepared.Layout.Variant == VisualizationLayoutVariant.PerformanceLanes
                && prepared.Layout.Geometry.HasScopes)
            {
                // Performance has one deliberately compact master strip, not a
                // per-channel Corrscope grid. Keep its source deterministic and
                // aligned with preview/review even when isolated stems exist.
                scope = MasterWaveformFrameSource.TryCreate(
                    overlay,
                    prepared.MasterAudioPath,
                    overlay.FpsNumerator,
                    overlay.FpsDenominator);
            }
            else if (scopePolicy == ScopeFrameSourcePolicy.Production)
            {
                int scopeFrameByteCount = checked(
                    prepared.Layout.Geometry.CorrscopeGridWidth
                    * prepared.Layout.Geometry.CorrscopeGridHeight * 4);
                scope = CreateProductionScopeSource(
                    prepared, workspace, runtime, scopeFrameByteCount);
            }

            if (scopePolicy == ScopeFrameSourcePolicy.Interactive)
                scope = CreateInteractiveScopeSource(prepared, overlay);

            if (scope is null
                && scopePolicy == ScopeFrameSourcePolicy.Production
                && prepared.Scope.Enabled
                && prepared.Layout.Geometry.HasScopes)
            {
                // No external bridge: fall back to the shared internal
                // master-waveform source so preview, review and final all render
                // the same scope content. This is the same fallback contract as
                // Corrscope's own master-waveform mode.
                scope = MasterWaveformFrameSource.TryCreate(
                    overlay,
                    prepared.MasterAudioPath,
                    overlay.FpsNumerator,
                    overlay.FpsDenominator);

                if (scope is null)
                {
                    Console.Error.WriteLine(
                        "warning: no scope source available (Corrscope/Python/bridge " +
                        "missing and no usable master WAV); scope regions will render " +
                        "transparent in preview, review AND final");
                }
            }

            return new VisualizationFrameRenderer(
                overlay, scope, prepared.Request.View.ScopeFps);
        }
        catch
        {
            // If overlay construction fails after the external process starts,
            // do not leave a producer process or pipe behind.
            scope?.Dispose();
            overlay?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fully in-process interactive scope source for GUI stills. Derived from
    /// the captured/isolated channel WAVs directly; it never probes Corrscope,
    /// Python, <c>corrscope-frames.py</c> or FFmpeg. Returns null when there is
    /// no scope region or no usable channel WAV (leaving scopes transparent).
    /// </summary>
    private static IScopeFrameSource? CreateInteractiveScopeSource(
        PreparedVisualizationSource prepared,
        IFrameOverlayRenderer overlay)
    {
        if (!prepared.Scope.Enabled
            || !prepared.Layout.Geometry.HasScopes
            || prepared.Layout.Variant != VisualizationLayoutVariant.DiagnosticGrid)
        {
            return null;
        }

        return InteractiveWaveformFrameSource.TryCreate(
            overlay,
            prepared.ScopeChannels,
            overlay.FpsNumerator,
            overlay.FpsDenominator);
    }

    /// <summary>
    /// Starts the Corrscope raw-frame bridge when scopes are enabled. Corrscope
    /// is a hard requirement for any render that needs scope content: when the
    /// tool is missing it is installed into a managed, project-local venv, and
    /// if it still cannot run the render fails rather than silently emitting
    /// empty scope regions. Returns null only when scopes are genuinely not part
    /// of this render (so the deterministic master-waveform fallback applies).
    /// </summary>
    private static IScopeFrameSource? CreateProductionScopeSource(
        PreparedVisualizationSource prepared,
        VisualizationWorkspace workspace,
        RenderRuntimeOptions runtime,
        int scopeFrameByteCount)
    {
        if (!prepared.Scope.Enabled || !prepared.Layout.Geometry.HasScopes)
            return null;

        // Corrscope availability / config only matters if there are scopes.
        if (prepared.Scope.Result is null)
            return null;

        // Hard requirement: ensure corrscope is usable, auto-installing into a
        // project-local venv when absent. An explicit --corrscope override is
        // authoritative. A failure here stops the render.
        string pythonPath;
        string corrExecutablePath;
        try
        {
            pythonPath = CorrscopeInstaller.Ensure(
                TimeSpan.FromMinutes(runtime.ToolTimeoutMinutes),
                explicitCorrPath: runtime.CorrscopePath);
            corrExecutablePath = CorrscopeInstaller.ResolveCorrExecutable(pythonPath);
        }
        catch (CorrscopeInstallException ex)
        {
            throw new VisualizationScopeException(
                "Corrscope is required for this render but could not be installed: " + ex.Message,
                4);
        }
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            throw new VisualizationScopeException(
                "Corrscope is required for this render but could not be resolved. Install with: pip install corrscope",
                4);
        }

        CorrscopeRunner? runner = VisualizationComposition.PrepareCorrscope(
            prepared.Request,
            runtime,
            workspace,
            prepared.Layout,
            prepared.Scope.Result,
            enabled: true,
            backendId: prepared.BackendId,
            corrExecutablePath: corrExecutablePath);

        if (runner is null || !runner.IsAvailable)
        {
            throw new VisualizationScopeException(
                "Corrscope is required for this render but its executable could not be located.",
                4);
        }

        string bridgePath = Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py");
        if (!File.Exists(bridgePath))
        {
            throw new VisualizationScopeException(
                "Corrscope is required for this render but the frame bridge 'corrscope-frames.py' is missing.",
                4);
        }

        return new CorrscopeFrameSource(
            () => runner.StartRawFrames(
                pythonPath,
                bridgePath,
                workspace.CorrscopeConfigPath),
            scopeFrameByteCount);
    }
}

/// <summary>
/// Selects which scope frame source family a renderer is built with.
/// <see cref="ScopeFrameSourcePolicy.Production"/> preserves the exact
/// Corrscope path used for final video, motion preview and review images;
/// <see cref="ScopeFrameSourcePolicy.Interactive"/> uses the fully in-process
/// random-access channel waveform source for GUI stills (no external process,
/// cheap seeks, approximate triggering).
/// </summary>
internal enum ScopeFrameSourcePolicy
{
    Production,
    Interactive,
}
