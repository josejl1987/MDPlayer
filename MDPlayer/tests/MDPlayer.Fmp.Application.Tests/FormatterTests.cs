using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Xunit;

namespace Fmp.Application.Tests;

public class FormatterTests
{
    private static CanonicalCommand Format(VisualizationRequest request, CommandDisplayMode mode = CommandDisplayMode.Compact)
        => new VisualizationCommandFormatter().Format(request, mode);

    [Fact]
    public void Compact_OmitsDefaultValues()
    {
        VisualizationRequest request = TestRequests.Valid();
        CanonicalCommand command = Format(request);
        Assert.Equal("mdplayer-render", command.Executable);
        Assert.Equal("render", command.Arguments[0]);
        Assert.Equal(request.InputPath, command.Arguments[1]);

        // Default-valued options are omitted in compact mode.
        Assert.DoesNotContain("--composition", command.Arguments);
        Assert.DoesNotContain("--quality", command.Arguments);
        Assert.DoesNotContain("--width", command.Arguments);
        Assert.DoesNotContain("--height", command.Arguments);
        Assert.DoesNotContain("--fps", command.Arguments);
        Assert.DoesNotContain("--fps-denominator", command.Arguments);
        Assert.DoesNotContain("--tracks", command.Arguments);
        Assert.DoesNotContain("--past", command.Arguments);
        Assert.DoesNotContain("--future", command.Arguments);
        Assert.DoesNotContain("--time-grid", command.Arguments);
        Assert.DoesNotContain("--structure", command.Arguments);
        Assert.DoesNotContain("--effects", command.Arguments);
        Assert.DoesNotContain("--note-color", command.Arguments);
        Assert.DoesNotContain("--palette", command.Arguments);
        Assert.DoesNotContain("--encoder", command.Arguments);
        Assert.DoesNotContain("--overwrite", command.Arguments);
        // The output path is always emitted.
        Assert.Contains("--output", command.Arguments);
    }

    [Fact]
    public void Compact_EmitsNonDefaultValues()
    {
        VisualizationRequest request = TestRequests.Valid() with
        {
            Output = new OutputSettings { Width = 1280, Height = 720, Overwrite = true },
            Tracks = new TrackSettings { Selection = TrackSelectionMode.All },
            Style = new StyleSettings { Effects = VisualEffects.Cinematic },
            Presentation = new PresentationSettings { Title = "My Song" },
        };
        CanonicalCommand command = Format(request);

        int widthIndex = Assert.Single(FindAll(command.Arguments, "--width"));
        Assert.Equal("1280", command.Arguments[widthIndex + 1]);
        Assert.Contains("--effects", command.Arguments);
        Assert.Contains("--title", command.Arguments);
        Assert.Contains("--overwrite", command.Arguments);
        int tracksIndex = Assert.Single(FindAll(command.Arguments, "--tracks"));
        Assert.Equal("all", command.Arguments[tracksIndex + 1]);
    }

    [Fact]
    public void FullyResolved_EmitsEverySetting_InStableOrder()
    {
        VisualizationRequest request = TestRequests.FullyPopulated();
        CanonicalCommand command = Format(request, CommandDisplayMode.FullyResolved);

        string[] expected =
        [
            "render",
            request.InputPath,
            "--composition", "scope-stage",
            "--output", request.OutputPath,
            "--quality", "final",
            "--width", "1920",
            "--height", "1080",
            "--fps", "60000",
            "--fps-denominator", "1001",
            "--tracks", "custom",
            "--include-track", "ym2608.0.fm.1",
            "--include-track", "ym2608.0.fm.2",
            "--exclude-track", "ym2608.0.rhythm.1",
            "--include-inactive",
            "--past", "0.4",
            "--future", "1.6",
            "--time-grid", "analytical",
            "--structure", "off",
            "--signal-strip",
            "--effects", "cinematic",
            "--note-color", "channel",
            "--palette", "accessible",
            "--title", "My Song",
            "--subtitle", "Sub",
            "--credits", "Cred",
            "--font", "/fonts/noto.ttf",
            "--loops", "4",
            "--fade", "3",
            "--tail", "1",
            "--max-duration", "120",
            "--sample-rate", "48000",
            "--encoder", "nvenc",
            "--overwrite",
        ];

        Assert.Equal(expected, command.Arguments.ToArray());
    }

    [Fact]
    public void OptionOrder_FollowsSpecSection202()
    {
        CanonicalCommand command = Format(TestRequests.FullyPopulated(), CommandDisplayMode.FullyResolved);
        string[] options = command.Arguments
            .Where(argument => argument.StartsWith("--", StringComparison.Ordinal))
            .ToArray();

        string[] expectedOrder =
        [
            "--composition", "--output",
            "--quality", "--width", "--height", "--fps", "--fps-denominator",
            "--tracks", "--include-track", "--exclude-track", "--include-inactive",
            "--past", "--future", "--time-grid", "--structure", "--signal-strip",
            "--effects", "--note-color", "--palette",
            "--title", "--subtitle", "--credits", "--font",
            "--loops", "--fade", "--tail", "--max-duration", "--sample-rate",
            "--encoder", "--overwrite",
        ];

        int[] positions = expectedOrder
            .Select(option => Array.IndexOf(options, option))
            .ToArray();
        // Every expected option is present and strictly increasing.
        Assert.All(positions, position => Assert.True(position >= 0, "missing option"));
        for (int i = 1; i < positions.Length; i++)
            Assert.True(positions[i] > positions[i - 1], $"order violated at index {i} ({expectedOrder[i]})");
    }

    [Fact]
    public void CustomTracks_EmitsIncludeAndExclude()
    {
        VisualizationRequest request = TestRequests.Valid() with
        {
            Tracks = new TrackSettings
            {
                Selection = TrackSelectionMode.Custom,
                IncludedIds = new[] { "ym2608.0.fm.1" },
                ExcludedIds = new[] { "ym2608.0.rhythm.1" },
            },
        };
        CanonicalCommand command = Format(request);
        Assert.Contains("--tracks", command.Arguments);
        Assert.Contains("custom", command.Arguments);
        Assert.Contains("--include-track", command.Arguments);
        Assert.Contains("ym2608.0.fm.1", command.Arguments);
        Assert.Contains("--exclude-track", command.Arguments);
        Assert.Contains("ym2608.0.rhythm.1", command.Arguments);
    }

    [Fact]
    public void DisplayText_QuotesArgumentsWithSpaces()
    {
        VisualizationRequest request = TestRequests.Valid() with
        {
            Presentation = new PresentationSettings { Title = "My Song Title" },
        };
        CanonicalCommand command = Format(request);
        // Arguments list is unquoted; display text quotes the value.
        Assert.Contains("My Song Title", command.Arguments);
        Assert.Contains("\"My Song Title\"", command.DisplayText);
    }

    [Fact]
    public void FinalQuality_EmitsQualityFinal()
    {
        VisualizationRequest request = TestRequests.Valid() with
        {
            Output = new OutputSettings { Quality = RenderQuality.Final },
        };
        CanonicalCommand command = Format(request, CommandDisplayMode.FullyResolved);
        Assert.Contains("--quality", command.Arguments);
        Assert.Contains("final", command.Arguments);
    }

    [Fact]
    public void OutputOption_IsAlwaysEmitted()
    {
        string input = "/music/song.vgz";
        string output = "/music/song.visualization/visualization.mp4";
        VisualizationRequest request = TestRequests.Valid(input, output);
        CanonicalCommand command = Format(request);
        // The render command always carries the explicit output path.
        Assert.Equal(new[] { "render", input, "--output", output }, command.Arguments.ToArray());
    }

    // ---- CLI-name mapping of enum values (formatter static helpers) ----

    [Theory]
    [InlineData(CompositionKind.Performance, "performance")]
    [InlineData(CompositionKind.ScopeStage, "scope-stage")]
    [InlineData(CompositionKind.Diagnostic, "diagnostic")]
    public void CompositionName_MapsCliNames(CompositionKind value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.CompositionName(value));

    [Theory]
    [InlineData(RenderQuality.Draft, "draft")]
    [InlineData(RenderQuality.Standard, "standard")]
    [InlineData(RenderQuality.Final, "final")]
    public void QualityName_MapsCliNames(RenderQuality value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.QualityName(value));

    [Theory]
    [InlineData(TimeGridMode.None, "none")]
    [InlineData(TimeGridMode.Automatic, "automatic")]
    [InlineData(TimeGridMode.Authoritative, "authoritative")]
    [InlineData(TimeGridMode.Analytical, "analytical")]
    public void TimeGridName_MapsCliNames(TimeGridMode value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.TimeGridName(value));

    [Theory]
    [InlineData(StructureOverlayMode.Off, "off")]
    [InlineData(StructureOverlayMode.Automatic, "automatic")]
    public void StructureName_MapsCliNames(StructureOverlayMode value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.StructureName(value));

    [Theory]
    [InlineData(VisualEffects.Off, "off")]
    [InlineData(VisualEffects.Subtle, "subtle")]
    [InlineData(VisualEffects.Cinematic, "cinematic")]
    public void EffectsName_MapsCliNames(VisualEffects value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.EffectsName(value));

    [Theory]
    [InlineData(NoteColorMode.Instrument, "instrument")]
    [InlineData(NoteColorMode.Channel, "channel")]
    [InlineData(NoteColorMode.PitchClass, "pitch")]
    public void NoteColorName_MapsCliNames(NoteColorMode value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.NoteColorName(value));

    [Theory]
    [InlineData(PaletteKind.Default, "default")]
    [InlineData(PaletteKind.Accessible, "accessible")]
    [InlineData(PaletteKind.Monochrome, "monochrome")]
    public void PaletteName_MapsCliNames(PaletteKind value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.PaletteName(value));

    [Theory]
    [InlineData(VideoEncoder.Auto, "auto")]
    [InlineData(VideoEncoder.LibX264, "x264")]
    [InlineData(VideoEncoder.Nvenc, "nvenc")]
    public void EncoderName_MapsCliNames(VideoEncoder value, string expected)
        => Assert.Equal(expected, VisualizationCommandFormatter.EncoderName(value));

    private static int[] FindAll(IReadOnlyList<string> arguments, string value)
    {
        var indices = new List<int>();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == value)
                indices.Add(i);
        }
        return indices.ToArray();
    }
}
