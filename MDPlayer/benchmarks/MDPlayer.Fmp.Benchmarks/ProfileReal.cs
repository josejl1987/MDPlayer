using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Benchmarks;

internal static class ProfileReal
{
    public static void Run(string timelinePath)
    {
        VisualizationTimeline timeline = VisualizationJsonWriter.Read(timelinePath);
        var renderer = new PanelOverlayRenderer(timeline, new PanelOverlayRenderer.Options
        {
            Width = 1920,
            Height = 1080,
            FpsNumerator = 60,
            FpsDenominator = 1,
        });
        byte[] destination = new byte[renderer.FrameByteCount];
        var scopeGrid = new byte[renderer.ScopeFrameByteCount];
        SequentialCompositeSession session = renderer.CreateSequentialSession();
        session.Initialize(destination);
        const int frames = 20000;
        long total = renderer.TotalFrames;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            session.RenderNext(i % total, scopeGrid, destination);
        sw.Stop();
        Console.WriteLine($"rendered {frames} frames in {sw.Elapsed.TotalSeconds:F2}s ({sw.Elapsed.TotalMilliseconds / frames:F3}ms/frame)");
    }
}
