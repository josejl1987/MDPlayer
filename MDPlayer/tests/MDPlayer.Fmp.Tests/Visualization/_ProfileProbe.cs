using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class ProfileProbe
{
    [SkippableFact]
    public void RenderRealTimeline()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("MDPLAYER_RUN_PERF") != "1",
            "Set MDPLAYER_RUN_PERF=1 to run the machine-dependent probe.");

        VisualizationTimeline timeline = VisualizationJsonWriter.Read(
            "/home/jose/MDPlayer/18 U.S.A. (Ken) I.visualization/timeline.json");
        var renderer = new PanelOverlayRenderer(
            timeline,
            new PanelOverlayRenderer.Options
            {
                Width = 1920,
                Height = 1080,
                FpsNumerator = 60,
                LayoutMode = VisualizationLayoutMode.UnifiedRoll,
                Channels = VisualizationChannelFilter.Active,
                Effects = EffectsMode.Minimal,
            });
        byte[] destination = new byte[renderer.FrameByteCount];
        SequentialCompositeSession session = renderer.CreateSequentialSession();
        session.Initialize(destination);
        const int frames = 60000;
        long totalFrames = renderer.TotalFrames;
        var sw = Stopwatch.StartNew();
        for (int frame = 0; frame < frames; frame++)
            session.RenderNext(frame % totalFrames, ReadOnlySpan<byte>.Empty, destination);
        sw.Stop();
        Console.WriteLine($"rendered {frames} frames in {sw.Elapsed.TotalSeconds:F2}s ({sw.Elapsed.TotalMilliseconds / frames:F3}ms/frame)");
    }
}