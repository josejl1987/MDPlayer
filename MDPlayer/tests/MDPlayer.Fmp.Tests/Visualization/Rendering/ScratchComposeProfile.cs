using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization.Rendering.Gpu;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

#nullable enable
namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TEMPORARY probe — measures per-stage overlay cost at canvas size for the
/// CPU sequential-session path and (when a GL surface exists) the GPU path.
/// Deleted after measurement; never run in CI.
/// </summary>
public sealed class ScratchComposeProfile
{
    private readonly ITestOutputHelper _out;
    public ScratchComposeProfile(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1920, 1080)]
    public void Profile(int w, int h)
    {
        _out.WriteLine("PROFILE BEGIN");
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        ResolvedVisualizationLayout layout = RendererTestLayout.Build(timeline, w, h);
        var options = new PanelOverlayRenderer.Options
        {
            FpsNumerator = 60,
            FpsDenominator = 1,
            EnablePerformanceMetrics = true,
        };

        using var cpu = new PanelOverlayRenderer(timeline, layout, options);
        byte[] dst = new byte[cpu.FrameByteCount];
        byte[] grid = new byte[cpu.ScopeFrameByteCount];
        long total = cpu.TotalFrames;
        _out.WriteLine($"canvas={w}x{h} frameBytes={cpu.FrameByteCount:N0} scopeGrid={cpu.Layout.CorrscopeGridWidth}x{cpu.Layout.CorrscopeGridHeight} totalFrames={total} panels={cpu.Layout.PanelCount}");

        // ---- CPU sequential (exact video-composition path) ----
        var session = cpu.CreateSequentialSession();
        session.Initialize(dst);
        for (int i = 0; i < 25; i++)
            session.RenderNext(i % total, grid, dst);

        const int N = 150;
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
            session.RenderNext(i % total, grid, dst);
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        _out.WriteLine($"CPU sequential: {sw.Elapsed.TotalMilliseconds / N:F2} ms/frame ({N / sw.Elapsed.TotalSeconds:F1} fps) alloc={alloc / (double)N:F0} B/frame");
        Print("CPU ", cpu.Performance);

        // ---- CPU random-access composite (preview/review path) ----
        beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (int i = 0; i < N; i++)
            cpu.RenderCompositeFrame(i % total, grid, dst);
        sw.Stop();
        alloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        _out.WriteLine($"CPU composite: {sw.Elapsed.TotalMilliseconds / N:F2} ms/frame ({N / sw.Elapsed.TotalSeconds:F1} fps) alloc={alloc / (double)N:F0} B/frame");

        // ---- GPU full-redraw (same canvas, same data) ----
        GpuPanelRenderer? gpu = null;
        try
        {
            gpu = new GpuPanelRenderer(timeline, layout, options);
        }
        catch (Exception ex)
        {
            _out.WriteLine($"GPU unavailable: {ex.GetType().Name}: {ex.Message}");
        }
        if (gpu is not null)
        {
            using (gpu)
            {
                for (int i = 0; i < 25; i++)
                    gpu.RenderCompositeFrame(i % total, grid, dst);
                beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                for (int i = 0; i < N; i++)
                    gpu.RenderCompositeFrame(i % total, grid, dst);
                sw.Stop();
                alloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
                _out.WriteLine($"GPU composite: {sw.Elapsed.TotalMilliseconds / N:F2} ms/frame ({N / sw.Elapsed.TotalSeconds:F1} fps) alloc={alloc / (double)N:F0} B/frame");
                Print("GPU ", gpu.Performance);
            }
        }
        _out.WriteLine("PROFILE END");
    }

    private void Print(string label, RenderPerformanceSnapshot s)
    {
        _out.WriteLine(
            $"{label}render={s.RenderSeconds * 1000:F2}ms comp={s.CompositingSeconds * 1000:F2} state={s.FrameStateSeconds * 1000:F2} static={s.StaticLayerSeconds * 1000:F2} dynamic={s.DynamicSeconds * 1000:F2} grid={s.GridLineSeconds * 1000:F2} text={s.TextSeconds * 1000:F2} layout={s.LayoutSeconds * 1000:F2} piano={s.PianoRollSeconds * 1000:F2} pitchGrid={s.PitchGridSeconds * 1000:F2} pitchBand={s.PitchBandSeconds * 1000:F2} ribbon={s.RibbonSeconds * 1000:F2} ribbonDec={s.RibbonDecorationSeconds * 1000:F2} wave={s.WaveformSeconds * 1000:F2}");
        _out.WriteLine(
            $"{label}counts full={s.FullRedraws} partial={s.PartialRedraws} unchanged={s.UnchangedFrames} renderedPx={s.RenderedPixels:N0} avoidedPx={s.AvoidedPixels:N0} copies={s.SurfaceCopies} fullCopies={s.FullFrameCopies} scopeCopies={s.ScopeCopies} copiedBytes={s.CopiedBytes:N0} alloc/frame={s.AllocatedBytesPerFrame:F0} peakWs={s.PeakWorkingSetBytes / 1_000_000.0:F0}MB");
    }
}