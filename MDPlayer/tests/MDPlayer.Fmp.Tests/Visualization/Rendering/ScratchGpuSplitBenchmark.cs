using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization.Rendering.Gpu;
using MDPlayer.Fmp.Tests.Fixtures;
using OpenTK.Windowing.Desktop;
using Xunit;
using Xunit.Abstractions;

#nullable enable
namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TEMPORARY probe — splits the GPU per-frame cost into draw / scope upload /
/// flush-submit-sync / readback for the four benchmark cases:
///
///   A. Full draw + synchronous flush, NO readback      → draw + submit cost
///   B. Clear only + readback                            → pure readback cost
///   C. Full draw + readback                             → production total
///   D. Full draw, scopes disabled + readback            → scope contribution
///
/// Deleted after measurement; never run in CI. Run under the target GL device
/// env (e.g. __NV_PRIME_RENDER_OFFLOAD=1 __GLX_VENDOR_LIBRARY_NAME=nvidia with
/// WAYLAND_DISPLAY unset) to measure that device's split.
/// </summary>
public sealed class ScratchGpuSplitBenchmark
{
    private readonly ITestOutputHelper _out;
    public ScratchGpuSplitBenchmark(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1920, 1080, false)]
    [InlineData(1920, 1080, true)]
    public void Split(int w, int h, bool opaqueScope)
    {
        const int Warmup = 25;
        const int N = 500;

        _out.WriteLine("GPU SPLIT BEGIN");
        // OpenTK assumes GLFW is initialized on the process main thread.
        // xUnit runs tests on threadpool threads, so the entry-point check
        // never matches and EnsureInitialized throws. This is a measurement
        // probe (never production): the invariant that matters is that ALL
        // GLFW/GL calls happen on ONE thread — which holds here — not WHICH
        // thread it is.
        GLFWProvider.CheckForMainThread = false;

        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        ResolvedVisualizationLayout layout = RendererTestLayout.Build(timeline, w, h);
        var options = new PanelOverlayRenderer.Options
        {
            FpsNumerator = 60,
            FpsDenominator = 1,
            EnablePerformanceMetrics = true,
        };

        using var gpu = new GpuPanelRenderer(timeline, layout, options);
        byte[] dst = new byte[gpu.FrameByteCount];
        byte[] grid = new byte[gpu.ScopeFrameByteCount];
        long total = gpu.TotalFrames;
        _out.WriteLine(
            $"canvas={w}x{h} opaqueScope={opaqueScope} frameBytes={gpu.FrameByteCount:N0} " +
            $"totalFrames={total} panels={gpu.Layout.PanelCount}");

        gpu.BenchmarkMode = GpuBenchmarkMode.Full;
        for (int i = 0; i < Warmup; i++)
            gpu.RenderCompositeFrame(i % total, grid, dst, opaqueScope);

        // A: full draw, synchronous flush, no readback.
        var a = Measure(gpu, total, grid, dst, N, GpuBenchmarkMode.DrawOnlySyncFlush, opaqueScope);
        // B: clear only + readback.
        var b = Measure(gpu, total, grid, dst, N, GpuBenchmarkMode.ClearOnlyReadback, opaqueScope);
        // C: full draw + readback (production).
        var c = Measure(gpu, total, grid, dst, N, GpuBenchmarkMode.Full, opaqueScope);
        // D: full draw, scopes disabled + readback.
        var d = Measure(gpu, total, grid, dst, N, GpuBenchmarkMode.FullNoScopes, opaqueScope);

        _out.WriteLine("");
        _out.WriteLine("--- per-frame split (ms) ---");
        void Row(string label, RenderPerformanceSnapshot s)
        {
            _out.WriteLine(
                $"{label,-28} draw={s.GpuDrawSeconds / s.Frames * 1000,6:F2} " +
                $"flush={s.GpuFlushSyncSeconds / s.Frames * 1000,6:F2} " +
                $"read={s.GpuReadbackSeconds / s.Frames * 1000,6:F2} " +
                $"scope={s.ScopeUploadSeconds / s.Frames * 1000,6:F2} " +
                $"TOTAL={(s.GpuDrawSeconds + s.GpuFlushSyncSeconds + s.GpuReadbackSeconds) / s.Frames * 1000,6:F2} " +
                $"GPU={s.GpuFrameSeconds / s.Frames * 1000,6:F2}");
        }

        Row("A", a);
        Row("B", b);
        Row("C", c);
        Row("D", d);

        _out.WriteLine("");
        _out.WriteLine("--- derived ---");
        double drawMs = a.GpuDrawSeconds / a.Frames * 1000 + a.GpuFlushSyncSeconds / a.Frames * 1000;
        double readMs = b.GpuReadbackSeconds / b.Frames * 1000;
        double cTotal = (c.GpuDrawSeconds + c.GpuFlushSyncSeconds + c.GpuReadbackSeconds) / c.Frames * 1000;
        double scopeMs = c.ScopeUploadSeconds / c.Frames * 1000;
        double dDraw = d.GpuDrawSeconds / d.Frames * 1000;
        double cDraw = c.GpuDrawSeconds / c.Frames * 1000;
        _out.WriteLine($"draw (+submit sync)  ~{drawMs,6:F2} ms   (case A: GPU work, no readback)");
        _out.WriteLine($"readback             ~{readMs,6:F2} ms   (case B: clear + read)");
        _out.WriteLine($"scope upload         ~{scopeMs,6:F2} ms   (case C ScopeUploadTicks)");
        _out.WriteLine($"draw w/o scopes      ~{dDraw,6:F2} ms   (case D)");
        _out.WriteLine($"scope draw delta     ~{cDraw - dDraw,6:F2} ms   (case C draw − case D draw)");
        _out.WriteLine($"C GPU time (ring)    ~{c.GpuFrameSeconds / c.Frames * 1000,6:F2} ms   (real GL_TIMESTAMP span: render+readback)");
        _out.WriteLine($"C total check        ~{cTotal,6:F2} ms   vs A+B-partial cross-check");
        _out.WriteLine($"C total from metrics ~{(c.GpuDrawSeconds + c.GpuFlushSyncSeconds + c.GpuReadbackSeconds) / c.Frames * 1000,6:F2} ms");
        _out.WriteLine("GPU SPLIT END");
    }

    private static RenderPerformanceSnapshot Measure(
        GpuPanelRenderer gpu,
        long total,
        byte[] grid,
        byte[] dst,
        int n,
        GpuBenchmarkMode mode,
        bool opaqueScope)
    {
        gpu.BenchmarkMode = mode;
        gpu.ResetPerformanceMetrics();
        for (int i = 0; i < n; i++)
            gpu.RenderCompositeFrame(i % total, grid, dst, opaqueScope);
        return gpu.Performance;
    }
}