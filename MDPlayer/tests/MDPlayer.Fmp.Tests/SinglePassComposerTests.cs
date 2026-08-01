using System.Diagnostics;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class SinglePassComposerTests
{
    [Fact]
    public void Compose_SyntheticCorrscopeFramesProducesOneFinalMp4()
    {
        string root = Path.Combine(Path.GetTempPath(), $"single-pass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string audioPath = Path.Combine(root, "master.wav");
        string videoPath = Path.Combine(root, "visualization.mp4");
        try
        {
            var timeline = new VisualizationTimeline
            {
                SampleRate = 1_000,
                StartSample = 0,
                EndSample = 1_000,
            };
            var renderer = new PanelOverlayRenderer(
                timeline,
                RendererTestLayout.Build(timeline, 480, 360),
                new PanelOverlayRenderer.Options
            {
                FpsNumerator = 10,
                FpsDenominator = 1,
                Presentation = new VisualizationPresentation("SYNTHETIC", "", ""),
            });

            using (var wav = new WavWriter(audioPath, 1_000, 2))
            {
                wav.Write(new short[2_000]);
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

            new SinglePassComposer("/usr/bin/ffmpeg", new SinglePassComposer.Options
            {
                TimeoutMinutes = 1,
                VideoPreset = "ultrafast",
                VideoCrf = "20",
            }).Compose(corr, audioPath, videoPath, renderer);

            Assert.True(File.Exists(videoPath));
            Assert.True(new FileInfo(videoPath).Length > 0);
            Assert.Empty(Directory.GetFiles(root, "*.partial.mp4"));
            Assert.Single(Directory.GetFiles(root, "*.mp4"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ComposeMasterOnly_ReportsFrameAndPipelineMetrics()
    {
        string root = Path.Combine(Path.GetTempPath(), $"single-pass-master-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string audioPath = Path.Combine(root, "master.wav");
        string videoPath = Path.Combine(root, "visualization.mp4");
        try
        {
            var timeline = new VisualizationTimeline
            {
                SampleRate = 1_000,
                StartSample = 0,
                EndSample = 1_000,
            };
            var renderer = new PanelOverlayRenderer(
                timeline,
                RendererTestLayout.Build(timeline, 480, 360),
                new PanelOverlayRenderer.Options
            {
                FpsNumerator = 5,
                FpsDenominator = 1,
                Presentation = new VisualizationPresentation("MASTER", "", ""),
            });

            using (var wav = new WavWriter(audioPath, 1_000, 2))
            {
                wav.Write(new short[2_000]);
                wav.Close();
            }

            var composer = new SinglePassComposer("/usr/bin/ffmpeg", new SinglePassComposer.Options
            {
                TimeoutMinutes = 1,
                VideoPreset = "ultrafast",
                VideoCrf = "20",
            });

            composer.ComposeMasterOnly(audioPath, videoPath, renderer, includeWaveform: false);

            Assert.True(File.Exists(videoPath));
            Assert.True(new FileInfo(videoPath).Length > 0);
            Assert.Empty(Directory.GetFiles(root, "*.partial.mp4"));

            SinglePassComposer.ComposeMetrics metrics = composer.LastMetrics;
            Assert.Equal(renderer.TotalFrames, metrics.FrameCount);
            Assert.InRange(metrics.MaxQueueDepth, 1, 3);
            Assert.True(metrics.WallTimeSeconds > 0);
            Assert.True(metrics.OverlayCpuSeconds >= 0);
            Assert.True(metrics.FfmpegWriteWaitSeconds >= 0);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildArguments_EncodesSingleRawStreamAndMasterAudio()
    {
        IReadOnlyList<string> args = SinglePassComposer.BuildArguments(
            "master.wav",
            "final.mp4",
            width: 1280,
            height: 720,
            fpsNumerator: 30,
            fpsDenominator: 1,
            new SinglePassComposer.Options
            {
                VideoPreset = "ultrafast",
                VideoCrf = "20",
            });

        // One raw RGBA input on stdin, plus the master WAV.
        Assert.Contains("rawvideo", args);
        Assert.Contains("rgba", args);
        Assert.Contains("1280x720", args);
        Assert.Contains("pipe:0", args);
        Assert.Contains("master.wav", args);

        // Direct stream mapping — no filter graph at all.
        Assert.DoesNotContain("-filter_complex", args);
        Assert.Contains("0:v:0", args);
        Assert.Contains("1:a:0", args);

        // Encode settings.
        Assert.Contains("libx264", args);
        Assert.Contains("ultrafast", args);
        Assert.Contains("20", args);
        Assert.Contains("yuv420p", args);
        Assert.Contains("aac", args);
    }

    [Fact]
    public void BuildArguments_UsesVeryfastCrf18ByDefault()
    {
        IReadOnlyList<string> args = SinglePassComposer.BuildArguments(
            "master.wav",
            "final.mp4",
            1280, 720, 30, 1);

        Assert.Contains("veryfast", args);
        Assert.Contains("18", args);
    }

    [Fact]
    public void AutoEncoder_UsesTheDetectedRuntimeCapability()
    {
        var composer = new SinglePassComposer("/usr/bin/ffmpeg", new SinglePassComposer.Options
        {
            Encoder = VideoEncoder.Auto,
        });

        Assert.Equal(
            composer.SupportsEncoder(VideoEncoder.Nvenc)
                ? VideoEncoder.Nvenc
                : VideoEncoder.LibX264,
            composer.EffectiveEncoder);
    }

    [Fact]
    public void BuildArguments_Nvenc_UsesGpuEncoderAndCq()
    {
        IReadOnlyList<string> args = SinglePassComposer.BuildArguments(
            "master.wav",
            "final.mp4",
            1280, 720, 30, 1,
            new SinglePassComposer.Options
            {
                Encoder = VideoEncoder.Nvenc,
                VideoPreset = "ultrafast",
                VideoCrf = "20",
            });

        Assert.Contains("h264_nvenc", args);
        Assert.Contains("p1", args);          // ultrafast → nvenc p1
        Assert.Contains("-rc", args);
        Assert.Contains("vbr", args);
        Assert.Contains("-cq", args);
        Assert.Contains("20", args);
        Assert.DoesNotContain("libx264", args);
        Assert.DoesNotContain("-crf", args);
    }

    [Fact]
    public void BuildArguments_NvencFinalQuality_UsesP4AndCq18()
    {
        IReadOnlyList<string> args = SinglePassComposer.BuildArguments(
            "master.wav",
            "final.mp4",
            1280, 720, 30, 1,
            new SinglePassComposer.Options
            {
                Encoder = VideoEncoder.Nvenc,
                VideoPreset = "veryfast",
                VideoCrf = "18",
            });

        Assert.Contains("h264_nvenc", args);
        Assert.Contains("p4", args);          // veryfast → nvenc p4
        Assert.Contains("18", args);
    }
}
