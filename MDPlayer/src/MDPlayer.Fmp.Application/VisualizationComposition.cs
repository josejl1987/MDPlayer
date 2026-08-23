using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization.Rendering.Gpu;
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

    public static IFrameOverlayRenderer CreateRenderer(
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        PanelOverlayRenderer.Options options,
        string? renderBackend = null)
    {
        bool useGpu = RenderBackendKey.Resolve(renderBackend) switch
        {
            RenderBackendSelection.Gpu => true,
            RenderBackendSelection.Cpu => false,
            // Auto now prefers GPU when a GL surface probes successfully;
            // measured on XA2020 perf lanes --scope off: GPU 10–12 ms/frame vs
            // CPU 33 ms/frame (CPU ribbonSeconds dominates). When probing fails
            // (headless/remote) we fall back to the CPU raster renderer with a
            // diagnostic (see GpuBackendProbe).
            _ => GpuBackendProbe.IsAvailable,
        };
        if (useGpu)
            return new GpuPanelRenderer(timeline, layout, options);
        return new PanelOverlayRenderer(timeline, layout, options);
    }

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
        double outputFps = request.Output.FpsNumerator / (double)request.Output.FpsDenominator;
        CorrscopeConfigWriter.Write(
            workspace.CorrscopeConfigPath,
            workspace.ScopeDir,
            scope,
            audioDir: "../audio",
            overrides: new CorrscopeOverrides
            {
                // The scope renders at its own cadence (auto min(outputFps,
                // 30) or an explicit --scope-fps); the bridge still renders
                // every YAML frame (render_subfps = 1) and the frame renderer
                // maps output frames onto scope frames (plan §5).
                Fps = ScopeFrameMapping.Resolve(request.View.ScopeFps, outputFps),
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

/// <summary>
/// Overlay renderer family selected by <c>--render-backend</c>. <c>Auto</c>
/// resolves to the CPU raster renderer for now — the GPU is opt-in
/// (<c>gpu</c>) until it proves faster than the CPU renderer on a real
/// workload; <c>cpu</c> forces the CPU renderer; <c>gpu</c> forces the GPU
/// renderer and stays strict — a missing GL surface is a hard error, never a
/// silent fallback.
/// </summary>
internal enum RenderBackendSelection
{
    Auto,
    Cpu,
    Gpu,
}

internal static class RenderBackendKey
{
    public static RenderBackendSelection Resolve(string? key)
        => (key ?? "").Trim().ToLowerInvariant() switch
        {
            "gpu" or "skia-gpu" => RenderBackendSelection.Gpu,
            "cpu" or "skia-cpu" => RenderBackendSelection.Cpu,
            _ => RenderBackendSelection.Auto,
        };
}

/// <summary>
/// One-time per-process probe for GPU availability: attempts to create a
/// hidden GL surface and caches the outcome. Currently diagnostic only (the
/// <c>auto</c> backend stays on CPU); it is kept so an opt-in GPU default can
/// be gated on real availability once the GPU path wins its benchmark.
/// Availability is decided by <see cref="GpuSkiaContext"/> construction alone
/// (window + GL entry points + Ganesh + surface); every failure mode is an
/// <see cref="InvalidOperationException"/>. Failures are expected on headless
/// or remote hosts and resolve to the CPU raster backend with a single
/// diagnostic line; explicit <c>gpu</c> bypasses this probe entirely and stays
/// strict.
/// </summary>
internal static class GpuBackendProbe
{
    private static readonly Lazy<bool> Availability = new(
        ProbeGpuSurface, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// True when a hidden GL surface could be created, probed once per process.
    /// </summary>
    public static bool IsAvailable => Availability.Value;

    private static bool ProbeGpuSurface()
    {
        try
        {
            using (new GpuSkiaContext(ProbeWidth, ProbeHeight))
                return true;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(
                "warning: GPU renderer unavailable; using CPU raster backend: " + ex.Message);
            return false;
        }
    }

    // A small surface is enough to exercise the full GL initialization path;
    // the real renderer creates the actual output-size surface per render.
    private const int ProbeWidth = 320;
    private const int ProbeHeight = 240;
}
