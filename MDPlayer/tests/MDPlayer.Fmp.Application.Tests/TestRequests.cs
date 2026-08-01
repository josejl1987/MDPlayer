using Fmp.Application.Contracts;

namespace Fmp.Application.Tests;

/// <summary>Shared request builders for the final (schema 1 / greenfield) contract.</summary>
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
            Composition = CompositionKind.Diagnostic,
            Output = new OutputSettings
            {
                Quality = RenderQuality.Final,
                Width = 1920,
                Height = 1080,
                FpsNumerator = 60000,
                FpsDenominator = 1001,
                Encoder = VideoEncoder.Nvenc,
                Overwrite = true,
            },
            Tracks = new TrackSettings
            {
                Selection = TrackSelectionMode.Custom,
                IncludedIds = new[] { "ym2608.0.fm.1", "ym2608.0.fm.2" },
                ExcludedIds = new[] { "ym2608.0.rhythm.1" },
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
                FadeSeconds = 3.0,
                TailSeconds = 1.0,
                MaximumDurationSeconds = 120,
                SampleRate = 48_000,
            },
        };
    }
}
