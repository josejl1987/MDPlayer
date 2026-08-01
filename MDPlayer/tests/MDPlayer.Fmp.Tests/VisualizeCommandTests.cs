using Fmp.Cli;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class VisualizeCommandTests
{
    [Fact]
    public void ParseArgs_ParsesCoreRenderOptions()
    {
        var options = VisualizeCommand.ParseArgs(
        [
            "track.ovi",
            "-o", "out",
            "--sample-rate", "48000",
            "--loops=1",
            "--fade=0",
            "--tail=0.25",
            "--max-duration=10",
            "--overwrite",
        ]);

        Assert.NotNull(options);
        Assert.Equal("track.ovi", options.Input);
        Assert.Equal("out", options.OutputDir);
        Assert.Equal(48_000, options.SampleRate);
        Assert.Equal(1, options.Loops);
        Assert.Equal(0, options.Fade);
        Assert.Equal(0.25, options.Tail);
        Assert.Equal(10.0, options.MaxDuration);
        Assert.True(options.Overwrite);
    }

    [Fact]
    public void ParseArgs_ParsesLayoutMode()
    {
        var options = VisualizeCommand.ParseArgs(["track.ovi", "--layout", "diagnostic"]);

        Assert.NotNull(options);
        Assert.Equal(VisualizationLayoutMode.Diagnostic, options.LayoutMode);
    }

    [Fact]
    public void ParseArgs_AppliesLayoutTemplatePalettePreviewAndMotionOptions()
    {
        string templatePath = Path.Combine(Path.GetTempPath(), $"mdplayer-template-{Guid.NewGuid():N}.json");
        string palettePath = Path.Combine(Path.GetTempPath(), $"mdplayer-palette-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(templatePath,
                "{\"layout\":\"diagnostic\",\"channels\":\"all\",\"scopePosition\":\"left\",\"scopeRatio\":0.25}");
            File.WriteAllText(palettePath,
                "{\"canvasBackground\":\"#010203\",\"accentColors\":[\"#00ff00\"]}");

            var options = VisualizeCommand.ParseArgs([
                "track.ovi",
                "--layout-template", templatePath,
                "--palette", palettePath,
                "--preview-html", "preview.html",
                "--diagnostic-pages", "pages",
                "--motion-blur-samples", "3",
            ]);

            Assert.NotNull(options);
            Assert.Equal(VisualizationLayoutMode.Diagnostic, options.LayoutMode);
            Assert.Equal(VisualizationChannelFilter.All, options.Channels);
            Assert.Equal(VisualizationScopePosition.Left, options.ScopePosition);
            Assert.Equal(0.25, options.ScopeRatio);
            Assert.Equal(3, options.MotionBlurSamples);
            Assert.Equal("preview.html", options.PreviewHtmlPath);
            Assert.Equal(1, options.Palette.CanvasBackground.R);
        }
        finally
        {
            if (File.Exists(templatePath)) File.Delete(templatePath);
            if (File.Exists(palettePath)) File.Delete(palettePath);
        }
    }

    [Fact]
    public void ParseArgs_AcceptsAutomaticEncoderSelection()
    {
        var options = VisualizeCommand.ParseArgs(["track.ovi", "--encoder", "auto"]);

        Assert.NotNull(options);
        Assert.Equal(VideoEncoder.Auto, options.Encoder);
    }

    [Fact]
    public void ParseArgs_RejectsUnknownOption()
    {
        Assert.Null(VisualizeCommand.ParseArgs(["track.ovi", "--scope-trigger"]));
    }

    [Theory]
    [InlineData("--max-duration", "0")]
    [InlineData("--max-duration", "-1")]
    [InlineData("--duration", "0")]
    public void ParseArgs_RejectsNonPositiveDuration(string option, string value)
    {
        Assert.Null(VisualizeCommand.ParseArgs(["track.ovi", option, value]));
    }

    [Fact]
    public void RenderAndBatchRejectNonPositiveDurationBeforeOpeningInputs()
    {
        Assert.Equal(2, RenderCommand.Handle(["missing.ovi", "--duration", "0"]));
        Assert.Equal(2, BatchCommand.Handle(["missing-directory", "--max-duration", "0"]));
    }

    [Fact]
    public void ParseArgs_ParsesRawVideoCompositionOptions()
    {
        var options = VisualizeCommand.ParseArgs(
        [
            "track.ovi",
            "--video", "final.mp4",
            "--width=1280",
            "--height", "720",
            "--fps", "30",
            "--corrscope", "/tools/corr",
            "--ffmpeg=/tools/ffmpeg",
            "--tool-timeout-minutes", "20",
        ]);

        Assert.NotNull(options);
        Assert.Equal("final.mp4", options.VideoPath);
        Assert.Equal(1280, options.Width);
        Assert.Equal(720, options.Height);
        Assert.Equal(30, options.Fps);
        Assert.Equal("/tools/corr", options.CorrscopePath);
        Assert.Equal("/tools/ffmpeg", options.FfmpegPath);
        Assert.Equal(20, options.ExternalToolTimeoutMinutes);
    }

    [Fact]
    public void ParseArgs_ParsesPresentationOptions()
    {
        var options = VisualizeCommand.ParseArgs(
        [
            "track.ovi",
            "--title", "PALACE OF DESTRUCTION",
            "--subtitle=YS I -- PC-98 / YM2608",
            "--credits", "MUSIC: YUZO KOSHIRO",
        ]);

        Assert.NotNull(options);
        Assert.Equal("PALACE OF DESTRUCTION", options.Title);
        Assert.Equal("YS I -- PC-98 / YM2608", options.Subtitle);
        Assert.Equal("MUSIC: YUZO KOSHIRO", options.Credits);
    }

    [Fact]
    public void ParseArgs_ParsesOptionalAnalysisOptions()
    {
        var options = VisualizeCommand.ParseArgs(
        [
            "track.ovi",
            "--analysis",
            "--analysis-python", "/opt/python",
            "--analysis-output", "analysis",
            "--analysis-cache", "shared-cache.txt",
            "--analysis-detail", "full",
            "--analysis-force",
            "--analysis-timeout-minutes", "3",
            "--analysis-overlay", "standard",
        ]);

        Assert.NotNull(options);
        Assert.True(options.Analysis);
        Assert.Equal("/opt/python", options.AnalysisPython);
        Assert.Equal("analysis", options.AnalysisOutput);
        Assert.Equal("shared-cache.txt", options.AnalysisCache);
        Assert.Equal(AnalysisDetail.Full, options.AnalysisDetail);
        Assert.True(options.AnalysisForce);
        Assert.Equal(3, options.AnalysisTimeoutMinutes);
        Assert.Equal("standard", options.AnalysisOverlay);
    }

    [Fact]
    public void ParseArgs_AllowsAnalysisToRemainDisabledByDefault()
    {
        var options = VisualizeCommand.ParseArgs(["track.ovi", "--analysis-overlay", "none"]);

        Assert.NotNull(options);
        Assert.False(options.Analysis);
        Assert.Equal("none", options.AnalysisOverlay);
    }

    [Fact]
    public void ParseArgs_RejectsNonPositiveAnalysisTimeout()
    {
        Assert.Null(VisualizeCommand.ParseArgs(["track.ovi", "--analysis-timeout-minutes", "0"]));
    }

    [Fact]
    public void AlignTimelineToAudio_TrimsNotesPitchAndRhythmAtAudioEnd()
    {
        var source = MDPlayer.Fmp.Tests.Fixtures.VisualizationTimelineFixture.Create();
        long audioEnd = 2500;

        var aligned = VisualizeCommand.AlignTimelineToAudio(source, audioEnd);

        Assert.Equal(audioEnd, aligned.EndSample);
        Assert.All(aligned.Notes, note =>
        {
            Assert.True(note.StartSample < audioEnd);
            Assert.InRange(note.EndSample, note.StartSample + 1, audioEnd);
            Assert.All(note.Pitch, point => Assert.True(point.SamplePosition < audioEnd));
        });
        Assert.All(aligned.Rhythm, evt => Assert.True(evt.SamplePosition < audioEnd));
    }
}
