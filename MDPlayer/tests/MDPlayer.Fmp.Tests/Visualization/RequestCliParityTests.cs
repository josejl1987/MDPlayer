using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// CLI parity tests (spec §33.2): for every supported GUI setting, the
/// request → serialized request-json → formatted canonical command → CLI-parse
/// path must resolve to the same options as loading the request-json directly.
/// </summary>
public class RequestCliParityTests
{
    public static TheoryData<string, VisualizationRequest> Requests => new()
    {
        { "balanced-default", Balanced() },
        { "final-with-metadata", FinalWithMetadata() },
        { "preview-custom-window", PreviewCustomWindow() },
        { "diagnostic-all", DiagnosticAll() },
        { "custom-channels", CustomChannels() },
        { "fractional-fps-and-analysis", FractionalFpsAndAnalysis() },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void FormattedCommand_ParsesToSameOptionsAsRequestJson(string name, VisualizationRequest request)
    {
        // Path A: options derived directly from the request JSON.
        string jsonPath = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequestSerializer.WriteToFile(request, jsonPath);
            VisualizeOptions optionsFromRequest = VisualizeOptionsParser.ParseForRequest(jsonPath, Array.Empty<string>());

            // Path B: options derived from the formatted canonical command.
            CanonicalCommand command = new VisualizationCommandFormatter().Format(request, CommandDisplayMode.FullyResolved);
            string[] args = command.Arguments.Skip(1).ToArray(); // drop "visualize"
            VisualizeOptions optionsFromCommand = VisualizeOptionsParser.ParseStrict(args);

            Assert.Equal(optionsFromRequest.Input, optionsFromCommand.Input);
            Assert.Equal(FullPath(optionsFromRequest.OutputDir), FullPath(optionsFromCommand.OutputDir));
            Assert.Equal(optionsFromRequest.VideoPath, optionsFromCommand.VideoPath);
            Assert.Equal(optionsFromRequest.Preset, optionsFromCommand.Preset);
            Assert.Equal(optionsFromRequest.LayoutMode, optionsFromCommand.LayoutMode);
            Assert.Equal(optionsFromRequest.Width, optionsFromCommand.Width);
            Assert.Equal(optionsFromRequest.Height, optionsFromCommand.Height);
            Assert.Equal(optionsFromRequest.Fps, optionsFromCommand.Fps);
            Assert.Equal(optionsFromRequest.FpsDenominator, optionsFromCommand.FpsDenominator);
            Assert.Equal(optionsFromRequest.Channels, optionsFromCommand.Channels);
            Assert.Equal(optionsFromRequest.IncludeTracks, optionsFromCommand.IncludeTracks);
            Assert.Equal(optionsFromRequest.ExcludeTracks, optionsFromCommand.ExcludeTracks);
            Assert.Equal(optionsFromRequest.PastSeconds, optionsFromCommand.PastSeconds);
            Assert.Equal(optionsFromRequest.FutureSeconds, optionsFromCommand.FutureSeconds);
            Assert.Equal(optionsFromRequest.RollZoom, optionsFromCommand.RollZoom);
            Assert.Equal(optionsFromRequest.ScopeRatio, optionsFromCommand.ScopeRatio);
            Assert.Equal(optionsFromRequest.ScopePosition, optionsFromCommand.ScopePosition);
            Assert.Equal(optionsFromRequest.GroupBy, optionsFromCommand.GroupBy);
            Assert.Equal(optionsFromRequest.TimeGrid, optionsFromCommand.TimeGrid);
            Assert.Equal(optionsFromRequest.Effects, optionsFromCommand.Effects);
            Assert.Equal(optionsFromRequest.NoteColor, optionsFromCommand.NoteColor);
            Assert.Equal(optionsFromRequest.Title, optionsFromCommand.Title);
            Assert.Equal(optionsFromRequest.Subtitle, optionsFromCommand.Subtitle);
            Assert.Equal(optionsFromRequest.Credits, optionsFromCommand.Credits);
            Assert.Equal(optionsFromRequest.FontPath, optionsFromCommand.FontPath);
            Assert.Equal(optionsFromRequest.Loops, optionsFromCommand.Loops);
            Assert.Equal(optionsFromRequest.Fade, optionsFromCommand.Fade);
            Assert.Equal(optionsFromRequest.Tail, optionsFromCommand.Tail);
            Assert.Equal(optionsFromRequest.MaxDuration, optionsFromCommand.MaxDuration);
            Assert.Equal(optionsFromRequest.Timeout, optionsFromCommand.Timeout);
            Assert.Equal(optionsFromRequest.SampleRate, optionsFromCommand.SampleRate);
            Assert.Equal(optionsFromRequest.SsgGainDb, optionsFromCommand.SsgGainDb);
            Assert.Equal(optionsFromRequest.SpcPitchMode, optionsFromCommand.SpcPitchMode);
            Assert.Equal(optionsFromRequest.Encoder, optionsFromCommand.Encoder);
            Assert.Equal(optionsFromRequest.Backend, optionsFromCommand.Backend);
            Assert.Equal(optionsFromRequest.ScopeMode, optionsFromCommand.ScopeMode);
            Assert.Equal(optionsFromRequest.FinalQuality, optionsFromCommand.FinalQuality);
            Assert.Equal(optionsFromRequest.StemsOnly, optionsFromCommand.StemsOnly);
            Assert.Equal(optionsFromRequest.Overwrite, optionsFromCommand.Overwrite);
            Assert.Equal(optionsFromRequest.Analysis, optionsFromCommand.Analysis);
            Assert.Equal(optionsFromRequest.AnalysisDetail, optionsFromCommand.AnalysisDetail);
            Assert.Equal(optionsFromRequest.AnalysisOverlay, optionsFromCommand.AnalysisOverlay);
            Assert.Equal(optionsFromRequest.AnalysisForce, optionsFromCommand.AnalysisForce);
            Assert.Equal(optionsFromRequest.AnalysisTimeoutMinutes, optionsFromCommand.AnalysisTimeoutMinutes);
            Assert.Equal(optionsFromRequest.CorrscopePath, optionsFromCommand.CorrscopePath);
            Assert.Equal(optionsFromRequest.FfmpegPath, optionsFromCommand.FfmpegPath);
            Assert.Equal(optionsFromRequest.AnalysisPython, optionsFromCommand.AnalysisPython);
            Assert.Equal(optionsFromRequest.AnalysisCache, optionsFromCommand.AnalysisCache);
            Assert.Equal(optionsFromRequest.ExternalToolTimeoutMinutes, optionsFromCommand.ExternalToolTimeoutMinutes);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    [Fact]
    public void RequestJsonPath_AndOverridesAfterIt_OverrideRequestFields()
    {
        VisualizationRequest request = Balanced();
        string jsonPath = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequestSerializer.WriteToFile(request, jsonPath);
            VisualizeOptions options = VisualizeOptionsParser.ParseForRequest(
                jsonPath, new[] { "--width", "640", "--height", "360", "--layout", "unified" });

            Assert.Equal(640, options.Width);
            Assert.Equal(360, options.Height);
            Assert.Equal(global::Fmp.Core.Visualization.Rendering.VisualizationLayoutMode.UnifiedRoll, options.LayoutMode);
            // Non-overridden fields still come from the request.
            Assert.Equal(request.InputPath, options.Input);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    private static string? FullPath(string? path)
        => string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);

    // ---- Fixtures ----

    private static VisualizationRequest Balanced()
    {
        string input = "/tmp/parity/song.vgz";
        return new VisualizationRequest
        {
            InputPath = input,
            OutputPath = "/tmp/parity/song.visualization/visualization.mp4",
        };
    }

    private static VisualizationRequest FinalWithMetadata()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Preset = VisualizationPreset.Final,
            FinalQuality = true,
            Title = "Final Boss",
            Subtitle = "Act 2",
            Credits = "Composed by Test",
            FontPath = "/tmp/fonts/noto.ttf",
        };
    }

    private static VisualizationRequest PreviewCustomWindow()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Preset = VisualizationPreset.Preview,
            Width = 960,
            Height = 540,
            FpsNumerator = 30,
            PastSeconds = 0.4,
            FutureSeconds = 1.6,
        };
    }

    private static VisualizationRequest DiagnosticAll()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Preset = VisualizationPreset.Diagnostic,
            Layout = VisualizationLayout.Diagnostic,
            ChannelSelection = ChannelSelectionMode.All,
            Effects = VisualizationEffects.Diagnostic,
            AnalysisOverlay = AnalysisOverlayMode.Standard,
        };
    }

    private static VisualizationRequest CustomChannels()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            ChannelSelection = ChannelSelectionMode.Custom,
            IncludedTrackIds = new[] { "ym2608.0.fm.1", "ym2608.0.fm.2" },
            ExcludedTrackIds = new[] { "ym2608.0.rhythm.1" },
        };
    }

    private static VisualizationRequest FractionalFpsAndAnalysis()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            FpsNumerator = 60000,
            FpsDenominator = 1001,
            ScopeRatio = 0.3,
            ScopePosition = ScopePosition.Left,
            Grouping = TrackGroupingMode.Device,
            TimeGrid = TimeGridMode.Analytical,
            Encoder = VideoEncoder.Nvenc,
            AnalysisEnabled = true,
            AnalysisDetail = global::Fmp.Application.Contracts.AnalysisDetail.Full,
            AnalysisOverlay = AnalysisOverlayMode.Standard,
            LoopCount = 4,
            FadeSeconds = 3.0,
            TailSeconds = 1.0,
            MaximumDurationSeconds = 120,
            Tools = new ToolOverrides
            {
                CorrscopePath = "/opt/corrscope",
                FfmpegPath = "/usr/bin/ffmpeg",
                AnalysisPython = "/usr/bin/python3",
                AnalysisForce = true,
            },
        };
    }
}
