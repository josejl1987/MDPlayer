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
/// Parity guard for the GPU-resident export seam: the export ring must render
/// the EXACT same pixels as the standard readback path, because the whole point
/// is that the encoder consumes the GPU export while every other consumer still
/// reads back — they must not diverge frame by frame.
///
/// Renders the same frame two ways and asserts byte-identical RGBA:
///   1. Standard: RenderCompositeFrame → GPU surface → ReadPixels.
///   2. Export:   IGpuExportSession.RenderNext → ring GL texture → verification
///                readback (never used in production).
/// </summary>
public sealed class GpuExportRingParityTest
{
    private static GpuPanelRenderer CreateRenderer()
    {
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

    private static string DescribeDiff(byte[] a, byte[] b)
    {
        int width = 1920;
        int first = -1;
        int total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                total++;
                if (first < 0)
                    first = i;
            }
        }
        int row = first < 0 ? -1 : first / (width * 4);
        int col = first < 0 ? -1 : (first % (width * 4)) / 4;
        return $"firstDiffOff={first} row={row} col={col} a=({A(a, first)}) b=({A(b, first)}) totalDiffBytes={total}";

        static string A(byte[] buf, int i)
        {
            if (i < 0 || i + 3 >= buf.Length)
                return "-";
            return $"{buf[i]},{buf[i + 1]},{buf[i + 2]},{buf[i + 3]}";
        }
    }

    private static byte[] FilledScopeGrid(int byteCount)
    {
        var grid = new byte[byteCount];
        for (int i = 0; i + 3 < grid.Length; i += 4)
        {
            grid[i] = 255;
            grid[i + 1] = 40;
            grid[i + 2] = 200;
            grid[i + 3] = 255;
        }
        return grid;
    }

    [Fact]
    public void ExportRing_RendersByteIdenticalToStandardReadback()
    {
        using var gpu = CreateRenderer();
        using IGpuExportSession session = gpu.CreateGpuExportSession(capacity: 4, scopeFramesAreOpaque: true);

        byte[] standard = new byte[gpu.FrameByteCount];
        byte[] exportReadback = new byte[gpu.FrameByteCount];
        byte[] grid = FilledScopeGrid(gpu.ScopeFrameByteCount);

        // Exercise every ring slot so texture reuse across slots is covered.
        for (int frameIndex = 0; frameIndex < 4; frameIndex++)
        {
            gpu.RenderCompositeFrame(frameIndex, grid, standard, scopeFramesAreOpaque: true);
            GpuExportFrame exported = session.RenderNext(frameIndex, grid);
            gpu.ReadExportFrameForTest(exported.Slot, exportReadback);

            Assert.True(
                standard.SequenceEqual(exportReadback),
                $"Frame {frameIndex} (export slot {exported.Slot}) diverged. " + DescribeDiff(standard, exportReadback));
        }
    }

    [Fact]
    public void ExportRing_WrapsAndReusesSlotsAfterRelease()
    {
        using var gpu = CreateRenderer();
        using IGpuExportSession session = gpu.CreateGpuExportSession(capacity: 2, scopeFramesAreOpaque: true);

        byte[] grid = FilledScopeGrid(gpu.ScopeFrameByteCount);

        // Fill both slots, release slot 0, then render again — the ring must let
        // slot 0 be reused.
        GpuExportFrame f0 = session.RenderNext(0, grid);
        GpuExportFrame f1 = session.RenderNext(1, grid);
        session.Release(f0.Slot);
        GpuExportFrame f2 = session.RenderNext(2, grid);

        // Release is not required before this test ends; it only verifies reuse.
        session.Release(f1.Slot);
        session.Release(f2.Slot);
    }
}