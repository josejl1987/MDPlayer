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
    public void CompositionPerformance_IsCanonical_AndMiditrailIsAcceptedAlias()
    {
        Assert.Equal(
            CompositionKind.Performance,
            RenderCommandParser.ParseCore(["song.vgz", "--composition", "performance"]).Request.Composition);
        Assert.Equal(
            CompositionKind.Performance,
            RenderCommandParser.ParseCore(["song.vgz", "--composition", "miditrail"]).Request.Composition);
        Assert.Throws<ArgumentException>(
            () => RenderCommandParser.ParseCore(["song.vgz", "--composition", "unknown"]));
    }

    [Fact]
    public void RequestJson_PerformanceWritesCanonical_AndReadsLegacyMiditrail()
    {
        string json = VisualizationRequestSerializer.Serialize(new VisualizationRequest
        {
            InputPath = "song.vgz",
            OutputPath = "out.mp4",
            Composition = CompositionKind.Performance,
        });
        Assert.Contains("\"Performance\"", json);
        Assert.DoesNotContain("MidiTrail", json);

        // Legacy project files written before the rename keep loading.
        Assert.Equal(
            CompositionKind.Performance,
            VisualizationRequestSerializer.Deserialize(
                """{"schemaVersion":1,"inputPath":"song.vgz","outputPath":"out.mp4","composition":"MidiTrail"}""").Composition);
        Assert.Equal(
            CompositionKind.Performance,
            VisualizationRequestSerializer.Deserialize(
                """{"schemaVersion":1,"inputPath":"song.vgz","outputPath":"out.mp4","composition":"miditrail"}""").Composition);
        Assert.Equal(
            CompositionKind.Diagnostic,
            VisualizationRequestSerializer.Deserialize(
                """{"schemaVersion":1,"inputPath":"song.vgz","outputPath":"out.mp4"}""").Composition);
    }

    [Fact]
    public void RequestJson_LegacyNumericCompositionStillReads()
    {
        Assert.Equal(
            CompositionKind.Performance,
            VisualizationRequestSerializer.Deserialize(
                """{"schemaVersion":1,"inputPath":"song.vgz","outputPath":"out.mp4","composition":1}""").Composition);
        Assert.Equal(
            CompositionKind.Diagnostic,
            VisualizationRequestSerializer.Deserialize(
                """{"schemaVersion":1,"inputPath":"song.vgz","outputPath":"out.mp4","composition":0}""").Composition);
        Assert.Throws<VisualizationRequestException>(
            () => VisualizationRequestSerializer.Deserialize(
                """{"schemaVersion":1,"inputPath":"song.vgz","outputPath":"out.mp4","composition":7}"""));
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
    public void ScopeFpsAndScopeOpacity_RoundTripAndValidate()
    {
        // Parser accepts both options and stores them in the request.
        var parsed = RenderCommandParser.ParseCore(
            ["song.vgz", "--scope-fps", "30", "--scope-opacity", "0.5"]);
        Assert.Equal(30.0, parsed.Request.View.ScopeFps);
        Assert.Equal(0.5, parsed.Request.Style.ScopeOpacity);

        // The formatter emits non-defaults and omits defaults, so the
        // round-trip is stable in both directions.
        CanonicalCommand command = new VisualizationCommandFormatter()
            .Format(new VisualizationRequest
            {
                InputPath = "song.vgz",
                OutputPath = "song.mp4",
                View = new ViewSettings { ScopeFps = 29.97 },
                Style = new StyleSettings { ScopeOpacity = 0.85 },
            }, CommandDisplayMode.FullyResolved);
        string[] emitted = command.Arguments.ToArray();
        Assert.Contains("--scope-fps", emitted);
        Assert.Contains("--scope-opacity", emitted);
        var reparsed = RenderCommandParser.ParseCore(emitted.Skip(1).ToArray());
        Assert.Equal(29.97, reparsed.Request.View.ScopeFps);
        Assert.Equal(0.85, reparsed.Request.Style.ScopeOpacity);

        var defaults = new VisualizationCommandFormatter().Format(
            new VisualizationRequest { InputPath = "song.vgz", OutputPath = "song.mp4" },
            CommandDisplayMode.Compact);
        Assert.DoesNotContain(defaults.Arguments, a => a == "--scope-fps" || a == "--scope-opacity");

        // FullyResolved mode emits the options bare (no value) when unset, and
        // the parser must accept the bare form (the `--title` contract).
        var bare = new VisualizationCommandFormatter().Format(
            new VisualizationRequest { InputPath = "song.vgz", OutputPath = "song.mp4" },
            CommandDisplayMode.FullyResolved);
        var bareParsed = RenderCommandParser.ParseCore(bare.Arguments.Skip(1).ToArray());
        Assert.Null(bareParsed.Request.View.ScopeFps);
        Assert.Equal(1.0, bareParsed.Request.Style.ScopeOpacity);

        // Validation: out-of-range opacity and non-positive fps are argument
        // errors (exit 2), matching the parser contract.
        Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseCore(["song.vgz", "--scope-opacity", "0"]));
        Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseCore(["song.vgz", "--scope-opacity", "1.1"]));
        Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseCore(["song.vgz", "--scope-fps", "0"]));
        Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseCore(["song.vgz", "--scope-fps", "-5"]));
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

    [Theory]
    [InlineData(new[] { "song.vgz" }, "auto")]
    [InlineData(new[] { "song.vgz", "--render-backend", "cpu" }, "cpu")]
    [InlineData(new[] { "song.vgz", "--render-backend", "gpu" }, "gpu")]
    [InlineData(new[] { "song.vgz", "--render-backend", "skia-gpu" }, "skia-gpu")]
    public void RenderBackendOptionIsRuntimeOnly(string[] args, string expected)
    {
        var parsed = RenderCommandParser.ParseCore(args);
        Assert.Equal(expected, parsed.Runtime.RenderBackend);
        // The render-backend selector is a runtime tool option: it never
        // enters the serialized request schema.
        Assert.DoesNotContain("render-backend", VisualizationRequestSerializer.Serialize(parsed.Request), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderBackendOptionRejectsUnknownValues()
        => Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseCore(["song.vgz", "--render-backend", "vulkan"]));

    [Theory]
    [InlineData(new[] { "song.vgz" }, null)]
    [InlineData(new[] { "song.vgz", "--scope", "off" }, "off")]
    [InlineData(new[] { "song.vgz", "--scope", "channel" }, "channel")]
    [InlineData(new[] { "song.vgz", "--scope", "master" }, "master")]
    [InlineData(new[] { "song.vgz", "--scope", "AUTO" }, "auto")]
    public void ScopeModeOptionFlowsIntoTheRequest(string[] args, string? expected)
    {
        var parsed = RenderCommandParser.ParseCore(args);
        Assert.Equal(expected, parsed.Request.View.ScopeMode);
    }

    [Fact]
    public void ScopeModeOptionRejectsUnknownValues()
        => Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseCore(["song.vgz", "--scope", "vaporwave"]));

    [Theory]
    [InlineData("gpu", "Gpu")]
    [InlineData("skia-gpu", "Gpu")]
    [InlineData("cpu", "Cpu")]
    [InlineData("skia-cpu", "Cpu")]
    [InlineData("auto", "Auto")]
    [InlineData(null, "Auto")]
    [InlineData("", "Auto")]
    [InlineData("unknown", "Auto")]
    public void RenderBackendKeyResolvesFamilies(string? key, string expected)
        => Assert.Equal(expected, RenderBackendKey.Resolve(key).ToString());

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
                ScopeFps = 30,
            },
            Style = new StyleSettings
            {
                Effects = VisualEffects.Cinematic,
                NoteColor = NoteColorMode.Channel,
                Palette = PaletteKind.Accessible,
                ScopeOpacity = 0.5,
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

    [Fact]
    public void BareIncludeTrack_ImpliesCustomSelection()
    {
        // A bare --include-track without --tracks must select Custom: the
        // include/exclude lists are only applied in Custom mode, so otherwise
        // the argument would be silently ignored under the default Active mode.
        var parsed = RenderCommandParser.ParseCore([
            "song.vgz", "--include-track", "ym2608.0.fm.1"]);

        Assert.Equal(TrackSelectionMode.Custom, parsed.Request.Tracks.Selection);
        Assert.Equal(new[] { "ym2608.0.fm.1" }, parsed.Request.Tracks.IncludedIds);
    }

    [Fact]
    public void ExplicitTrackMode_OverridesIncludeTrackImplication()
    {
        // An explicit --tracks mode wins over the Custom implication.
        var parsed = RenderCommandParser.ParseCore([
            "song.vgz", "--tracks", "all", "--include-track", "ym2608.0.fm.1"]);

        Assert.Equal(TrackSelectionMode.All, parsed.Request.Tracks.Selection);
        Assert.Equal(new[] { "ym2608.0.fm.1" }, parsed.Request.Tracks.IncludedIds);
    }
}
