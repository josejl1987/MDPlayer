using System;
using System.Linq;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization.Rendering.Gpu;
using MDPlayer.Fmp.Tests.Fixtures;
using OpenTK.Windowing.Desktop;
using Xunit;

#nullable enable
namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Pixel oracle for the GPU renderer's scope path. These are the guard that
/// lets the scope upload be reworked without a visual regression going
/// undetected:
///   1. The same input renders byte-identically across frames (determinism).
///      Without this, a stale-cache bug (e.g. an external GL texture wrapped
///      once and mutated, whose old contents keep being sampled) is invisible.
///   2. Two different scope grids produce different output — proving the scope
///      source actually reaches the framebuffer.
/// This is deliberately cheap and whole-buffer: it needs no layout internals.
/// </summary>
public sealed class GpuScopePixelOracleTest
{
    private static GpuPanelRenderer CreateRenderer()
    {
        // xUnit runs tests on threadpool threads; OpenTK/GLFW would reject
        // the non-main-thread init. Same workaround as ScratchGpuSplitBenchmark
        // (measurement-adjacent guard): every GL call happens on ONE thread,
        // which is what matters here.
        GLFWProvider.CheckForMainThread = false;

        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        ResolvedVisualizationLayout layout = RendererTestLayout.Build(timeline, 1920, 1080);
        var options = new PanelOverlayRenderer.Options
        {
            FpsNumerator = 60,
            FpsDenominator = 1,
        };
        return new GpuPanelRenderer(timeline, layout, options);
    }

    private static byte[] MakeGrid(int byteCount, bool filled)
    {
        var grid = new byte[byteCount];
        if (filled)
        {
            for (int i = 0; i + 3 < grid.Length; i += 4)
            {
                grid[i] = 255;      // R
                grid[i + 1] = 0;    // G
                grid[i + 2] = 136;  // B
                grid[i + 3] = 255;  // A (opaque => premul == straight)
            }
        }
        return grid;
    }

    [Fact]
    public void SameScopeGrid_RendersDeterministically()
    {
        using var gpu = CreateRenderer();
        byte[] dst = new byte[gpu.FrameByteCount];
        var grid = MakeGrid(gpu.ScopeFrameByteCount, filled: true);

        // Warm both calls so the chrome cache is built in both.
        gpu.RenderCompositeFrame(0, grid, dst, scopeFramesAreOpaque: true);
        gpu.RenderCompositeFrame(0, grid, dst, scopeFramesAreOpaque: true);
        byte[] first = (byte[])dst.Clone();
        gpu.RenderCompositeFrame(0, grid, dst, scopeFramesAreOpaque: true);
        byte[] second = (byte[])dst.Clone();

        Assert.True(
            first.SequenceEqual(second),
            "Identical scope input must render byte-identically; a difference " +
            "indicates nondeterminism or a stale-cache regression in the scope path.");
    }

    [Fact]
    public void DifferentScopeGrids_ProduceDifferentOutput()
    {
        using var gpu = CreateRenderer();
        byte[] dst = new byte[gpu.FrameByteCount];

        var blank = MakeGrid(gpu.ScopeFrameByteCount, filled: false);
        var filled = MakeGrid(gpu.ScopeFrameByteCount, filled: true);

        gpu.RenderCompositeFrame(0, blank, dst, scopeFramesAreOpaque: true);
        byte[] blankOut = (byte[])dst.Clone();

        gpu.RenderCompositeFrame(0, filled, dst, scopeFramesAreOpaque: true);
        byte[] filledOut = (byte[])dst.Clone();

        Assert.False(
            blankOut.SequenceEqual(filledOut),
            "The scope grid must reach the framebuffer: a blank vs opaque-filled " +
            "grid must produce different pixels. Equality means the scope source " +
            "is being dropped or served from a stale cache.");
    }
}