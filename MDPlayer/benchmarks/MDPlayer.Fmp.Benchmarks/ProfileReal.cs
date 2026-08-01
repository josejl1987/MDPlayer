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
        long frameBytes = renderer.FrameByteCount;
        var layout = renderer.Layout;
        var rects = new List<OverlayRect>
        {
            layout.TopBarRect,
            layout.BottomBarRect,
        };
        for (int p = 0; p < layout.PanelCount; p++)
        {
            rects.Add(layout.GetHeaderRect(p));
            rects.Add(layout.GetTimelineRect(p));
            rects.Add(layout.GetScopeRect(p));
            var pr = layout.GetPanelRect(p);
            rects.Add(new OverlayRect(pr.X, pr.Y, pr.Width, 1));
        }
        long restoreBytes = 0;
        foreach (var r in rects) restoreBytes += (long)r.Width * r.Height;
        Console.WriteLine($"frame bytes: {frameBytes:N0} restore rects: {rects.Count} restore bytes/frame: {restoreBytes:N0} ({100.0 * restoreBytes / frameBytes:F1}% of frame)");
        Console.WriteLine($"panel layout: {layout.PanelCount} panels");
        foreach (var r in rects)
            Console.WriteLine($"  rect X={r.X} Y={r.Y} W={r.Width} H={r.Height}");
        var actualField = typeof(PanelOverlayRenderer).GetField("_dynamicRestoreRects",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var actualRects = (OverlayRect[])actualField!.GetValue(renderer)!;
        long actualBytes = 0;
        foreach (var r in actualRects) actualBytes += (long)r.Width * r.Height;
        Console.WriteLine($"ACTUAL restore rects: {actualRects.Length} ACTUAL bytes/frame: {actualBytes:N0} ({100.0 * actualBytes / frameBytes:F1}% of frame)");
        foreach (var r in actualRects)
            Console.WriteLine($"  ACTUAL X={r.X} Y={r.Y} W={r.Width} H={r.Height}");

        SequentialCompositeSession session = renderer.CreateSequentialSession();
        session.Initialize(destination);
        const int frames = 20000;
        long total = renderer.TotalFrames;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            session.RenderNext(i % total, scopeGrid, destination);
        sw.Stop();
        Console.WriteLine($"session rendered {frames} frames in {sw.Elapsed.TotalSeconds:F2}s ({sw.Elapsed.TotalMilliseconds / frames:F3}ms/frame)");

        sw.Restart();
        for (int i = 0; i < frames; i++)
            renderer.RenderCompositeFrame(i % total, scopeGrid, destination);
        sw.Stop();
        Console.WriteLine($"fullcopy rendered {frames} frames in {sw.Elapsed.TotalSeconds:F2}s ({sw.Elapsed.TotalMilliseconds / frames:F3}ms/frame)");
    }
}
