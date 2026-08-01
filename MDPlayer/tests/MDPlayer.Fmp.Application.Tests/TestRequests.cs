using Fmp.Application.Contracts;

namespace Fmp.Application.Tests;

/// <summary>Shared request builders.</summary>
internal static class TestRequests
{
    public static VisualizationRequest Valid(string? inputPath = null, string? outputPath = null)
    {
        string input = inputPath ?? Path.Combine(Path.GetTempPath(), "mdplayer-test-input.vgz");
        string output = outputPath ?? Path.Combine(Path.GetTempPath(), "mdplayer-test-out", "visualization.mp4");
        // Create the input file when the parent directory is writable, so the
        // request validator sees an existing input. Formatter-only tests may
        // pass placeholder paths whose directory does not exist — ignore.
        try
        {
            if (!File.Exists(input))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(input)!);
                File.WriteAllBytes(input, new byte[] { 0x56, 0x67, 0x6d });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new VisualizationRequest
        {
            InputPath = input,
            OutputPath = output,
        };
    }

    public static VisualizationRequest FullyPopulated()
    {
        VisualizationRequest request = Valid();
        return request with
        {
            Preset = VisualizationPreset.Final,
            Layout = VisualizationLayout.Hybrid,
            Width = 1920,
            Height = 1080,
            FpsNumerator = 60000,
            FpsDenominator = 1001,
            ChannelSelection = ChannelSelectionMode.Custom,
            IncludedTrackIds = new[] { "ym2608.0.fm.1", "ym2608.0.fm.2" },
            ExcludedTrackIds = new[] { "ym2608.0.rhythm.1" },
            PastSeconds = 0.4,
            FutureSeconds = 1.6,
            Effects = VisualizationEffects.Cinematic,
            NoteColor = NoteColorMode.Channel,
            ScopeRatio = 0.3,
            ScopePosition = ScopePosition.Left,
            Grouping = TrackGroupingMode.Device,
            TimeGrid = TimeGridMode.Analytical,
            RollZoom = 1.25,
            AnalysisEnabled = true,
            AnalysisDetail = AnalysisDetail.Full,
            AnalysisOverlay = AnalysisOverlayMode.Standard,
            Title = "My Song",
            Subtitle = "Sub",
            Credits = "Cred",
            FontPath = "/fonts/noto.ttf",
            LoopCount = 4,
            FadeSeconds = 3.0,
            TailSeconds = 1.0,
            MaximumDurationSeconds = 120,
            TimeoutSeconds = 90,
            SampleRate = 48000,
            SsgGainDb = 2.0,
            Encoder = VideoEncoder.Nvenc,
            Backend = BackendPreference.Auto,
            ScopeMode = ScopeMode.Channel,
            FinalQuality = true,
            StemsOnly = false,
            Overwrite = true,
            Tools = new ToolOverrides
            {
                CorrscopePath = "/opt/corrscope/corrscope",
                FfmpegPath = "/usr/bin/ffmpeg",
                AnalysisPython = "/usr/bin/python3",
                AnalysisCache = "/cache/analysis",
                AnalysisForce = true,
                AnalysisTimeoutMinutes = 20,
                ToolTimeoutMinutes = 45,
                FmpComPath = "/opt/fmp/FMP.COM",
            },
        };
    }
}
