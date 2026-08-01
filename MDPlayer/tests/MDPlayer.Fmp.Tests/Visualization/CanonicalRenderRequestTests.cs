using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class CanonicalRenderRequestTests
{
    [Fact]
    public void ParserUsesCanonicalDefaults()
    {
        var parsed = RenderCommandParser.ParseCore(["song.vgz"]);

        Assert.Equal("song.vgz", parsed.Request.InputPath);
        Assert.Equal(Path.Combine("song.visualization", "visualization.mp4"), parsed.Request.OutputPath);
        Assert.Equal(CompositionKind.Diagnostic, parsed.Request.Composition);
        Assert.Equal(new OutputSettings(), parsed.Request.Output);
        Assert.Equal(new TrackSettings(), parsed.Request.Tracks);
        Assert.Equal(new ViewSettings(), parsed.Request.View);
        Assert.Equal(new StyleSettings(), parsed.Request.Style);
        Assert.Equal(new PresentationSettings(), parsed.Request.Presentation);
        Assert.Equal(new PlaybackSettings(), parsed.Request.Playback);

        Assert.Equal("auto", parsed.Runtime.Backend);
        Assert.False(parsed.Runtime.Quiet);
        Assert.False(parsed.Runtime.Json);
        Assert.Equal(60, parsed.Runtime.ToolTimeoutMinutes);
    }

    [Fact]
    public void RequestJsonSeed_IsOverriddenByCommandLine()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-request-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequestSerializer.WriteToFile(new VisualizationRequest
            {
                InputPath = "seed.vgz",
                OutputPath = "seed.mp4",
                Output = new OutputSettings
                {
                    Quality = RenderQuality.Final,
                    Width = 1280,
                    Height = 720,
                    FpsNumerator = 30,
                    Encoder = VideoEncoder.LibX264,
                    Overwrite = true,
                },
                View = new ViewSettings
                {
                    PastSeconds = 1.1,
                    FutureSeconds = 4.4,
                    TimeGrid = TimeGridMode.Authoritative,
                    Structure = StructureOverlayMode.Off,
                },
                Playback = new PlaybackSettings
                {
                    LoopCount = 5,
                    FadeSeconds = 2,
                    TailSeconds = 0.75,
                    MaximumDurationSeconds = 90,
                    SampleRate = 44_100,
                    SsgGainDb = -6,
                    SpcPitch = SpcPitchInterpretation.Relative,
                },
            }, path);

            var parsed = RenderCommandParser.ParseCore([
                "--request-json", path,
                "--width", "1920",
                "--output", "override.mp4",
            ]);

            Assert.Equal("seed.vgz", parsed.Request.InputPath);
            Assert.Equal("override.mp4", parsed.Request.OutputPath);
            Assert.Equal(RenderQuality.Final, parsed.Request.Output.Quality);
            Assert.Equal(1920, parsed.Request.Output.Width);
            Assert.Equal(720, parsed.Request.Output.Height);
            Assert.Equal(30, parsed.Request.Output.FpsNumerator);
            Assert.Equal(VideoEncoder.LibX264, parsed.Request.Output.Encoder);
            Assert.True(parsed.Request.Output.Overwrite);
            Assert.Equal(new ViewSettings
            {
                PastSeconds = 1.1,
                FutureSeconds = 4.4,
                TimeGrid = TimeGridMode.Authoritative,
                Structure = StructureOverlayMode.Off,
            }, parsed.Request.View);
            Assert.Equal(new PlaybackSettings
            {
                LoopCount = 5,
                FadeSeconds = 2,
                TailSeconds = 0.75,
                MaximumDurationSeconds = 90,
                SampleRate = 44_100,
                SsgGainDb = -6,
                SpcPitch = SpcPitchInterpretation.Relative,
            }, parsed.Request.Playback);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SsgGainAndSpcPitchRoundTrip()
    {
        VisualizationRequest expected = new()
        {
            InputPath = "song.vgz",
            OutputPath = "song.mp4",
            Playback = new PlaybackSettings
            {
                SsgGainDb = -9,
                SpcPitch = SpcPitchInterpretation.Relative,
            },
        };
        CanonicalCommand command = new VisualizationCommandFormatter()
            .Format(expected, CommandDisplayMode.FullyResolved);
        var parsed = RenderCommandParser.ParseCore(command.Arguments.Skip(1).ToArray());
        Assert.Equal(expected.InputPath, parsed.Request.InputPath);
        Assert.Equal(expected.OutputPath, parsed.Request.OutputPath);
        Assert.Equal(expected.Output, parsed.Request.Output);
        Assert.Equal(expected.View, parsed.Request.View);
        Assert.Equal(expected.Style, parsed.Request.Style);
        Assert.Equal(expected.Presentation, parsed.Request.Presentation);
        Assert.Equal(expected.Playback, parsed.Request.Playback);
        Assert.Equal(expected.Tracks.Selection, parsed.Request.Tracks.Selection);
        Assert.Equal(expected.Tracks.IncludedIds, parsed.Request.Tracks.IncludedIds);
        Assert.Equal(expected.Tracks.ExcludedIds, parsed.Request.Tracks.ExcludedIds);
    }

    [Fact]
    public void RuntimePathsDoNotEnterRequest()
    {
        var parsed = RenderCommandParser.ParseCore([
            "song.vgz", "--ffmpeg", "/tools/ffmpeg", "--fmp-com", "/tools/FMP.COM"]);
        string json = VisualizationRequestSerializer.Serialize(parsed.Request);
        Assert.DoesNotContain("ffmpeg", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FMP.COM", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullyResolvedFormatterRoundTripsRequest()
    {
        VisualizationRequest expected = new()
        {
            InputPath = "song.vgz",
            OutputPath = "song.mp4",
            Output = new OutputSettings
            {
                Quality = RenderQuality.Final,
                Width = 1280,
                Height = 720,
                FpsNumerator = 60000,
                FpsDenominator = 1001,
                Encoder = VideoEncoder.Nvenc,
                Overwrite = true,
            },
            Tracks = new TrackSettings
            {
                Selection = TrackSelectionMode.Custom,
                IncludedIds = ["ym2608.0.fm.1"],
                ExcludedIds = ["ym2608.0.rhythm.1"],
                IncludeInactiveDiagnosticTracks = true,
            },
            View = new ViewSettings
            {
                PastSeconds = 0.4,
                FutureSeconds = 1.6,
                TimeGrid = TimeGridMode.Analytical,
                Structure = StructureOverlayMode.Off,
            },
            Style = new StyleSettings
            {
                Effects = VisualEffects.Cinematic,
                NoteColor = NoteColorMode.Channel,
                Palette = PaletteKind.Accessible,
            },
            Presentation = new PresentationSettings
            {
                Title = "My Song",
                Subtitle = "Sub",
                Credits = "Cred",
                FontPath = "/fonts/noto.ttf",
            },
            Playback = new PlaybackSettings
            {
                LoopCount = 4,
                FadeSeconds = 3,
                TailSeconds = 1,
                MaximumDurationSeconds = 120,
                SampleRate = 48_000,
                SsgGainDb = -9,
                SpcPitch = SpcPitchInterpretation.Relative,
            },
        };

        CanonicalCommand command = new VisualizationCommandFormatter()
            .Format(expected, CommandDisplayMode.FullyResolved);
        var parsed = RenderCommandParser.ParseCore(command.Arguments.Skip(1).ToArray());

        Assert.Equal(expected.InputPath, parsed.Request.InputPath);
        Assert.Equal(expected.OutputPath, parsed.Request.OutputPath);
        Assert.Equal(expected.Output, parsed.Request.Output);
        Assert.Equal(expected.Tracks.Selection, parsed.Request.Tracks.Selection);
        Assert.Equal(expected.Tracks.IncludedIds, parsed.Request.Tracks.IncludedIds);
        Assert.Equal(expected.Tracks.ExcludedIds, parsed.Request.Tracks.ExcludedIds);
        Assert.Equal(expected.View, parsed.Request.View);
        Assert.Equal(expected.Style, parsed.Request.Style);
        Assert.Equal(expected.Presentation, parsed.Request.Presentation);
        Assert.Equal(expected.Playback, parsed.Request.Playback);
    }
}
