using System.Diagnostics;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

#nullable enable
namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TEMPORARY benchmark — runs the REAL production composition pipeline
/// end-to-end (overlay render + unpremultiply + pipe write + FFmpeg x264
/// encode) at canvas size, with real ffmpeg and a real WAV, using the same
/// synthetic-corr harness as SinglePassComposerTests. Prints per-stage wall
/// time per frame. Deleted after measurement; never run in CI.
/// </summary>
public sealed class ScratchRealPipelineBenchmark
{
    private readonly ITestOutputHelper _out;
    public ScratchRealPipelineBenchmark(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1920, 1080, "veryfast")]  // production default preset
    [InlineData(1920, 1080, "ultrafast")] // encode-cost sensitivity
    public void FullComposition(int w, int h, string preset)
    {
        _out.WriteLine($"BENCH BEGIN 1080p preset={preset}");
        string root = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string audioPath = Path.Combine(root, "master.wav");
        string videoPath = Path.Combine(root, "visualization.mp4");
        try
        {
            VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
            ResolvedVisualizationLayout layout = RendererTestLayout.Build(timeline, w, h);
            var renderer = new PanelOverlayRenderer(
                timeline,
                layout,
                new PanelOverlayRenderer.Options
                {
                    FpsNumerator = 60,
                    FpsDenominator = 1,
                    Presentation = new VisualizationPresentation("BENCH", "", ""),
                });

            using (var wav = new WavWriter(audioPath, 1_000, 2))
            {
                // Audio must last at least as long as the video (300 frames @
                // 60 fps = 5 s); a shorter track truncates the stream at
                // shortest=1 and the composer kills the still-running producer.
                wav.Write(new short[20_000]); // 20 s audio
                wav.Close();
            }

            int gridBytes = renderer.Width * renderer.Layout.CorrscopeGridHeight * 4;
            int totalBytes = checked(gridBytes * (int)renderer.TotalFrames);
            using var corr = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            corr.StartInfo.ArgumentList.Add("-c");
            corr.StartInfo.ArgumentList.Add($"head -c {totalBytes} /dev/zero");
            corr.Start();

            var composer = new SinglePassComposer("/usr/bin/ffmpeg", new SinglePassComposer.Options
            {
                TimeoutMinutes = 5,
                VideoPreset = preset,
                VideoCrf = "18",
            });

            long total = renderer.TotalFrames;
            _out.WriteLine($"canvas={w}x{h} frameCount={total} scopeGrid={renderer.Layout.CorrscopeGridWidth}x{renderer.Layout.CorrscopeGridHeight} encoder=libx264 preset={preset}");

            var sw = Stopwatch.StartNew();
            composer.Compose(corr, audioPath, videoPath, renderer);
            sw.Stop();
            double wallMs = sw.Elapsed.TotalMilliseconds;

            SinglePassComposer.ComposeMetrics m = composer.LastMetrics;
            double overlayMs = m.OverlayCpuSeconds * 1000.0 / total;
            double blockedMs = m.FfmpegWriteWaitSeconds * 1000.0 / total;
            double wallPerFrame = wallMs / total;
            double fps = total / sw.Elapsed.TotalSeconds;

            _out.WriteLine($"FULL COMPOSE preset={preset}: wall {wallPerFrame:F1} ms/frame ({fps:F1} fps) | overlay {overlayMs:F1} | encoderBlocked {blockedMs:F1} | other {wallPerFrame - overlayMs - blockedMs:F1}");
            _out.WriteLine($"PROJECTION preset={preset}: 4-min song (14400 frames) = {14400 / fps / 60.0:F1} min");
            _out.WriteLine($"BENCH FILE preset={preset}: mp4={new FileInfo(videoPath).Length / (1024.0 * 1024.0):F1} MiB");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}