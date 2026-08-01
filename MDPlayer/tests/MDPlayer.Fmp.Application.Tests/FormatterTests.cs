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
        CanonicalCommand command = Format(TestRequests.Valid());
        Assert.Equal("mdplayer-render", command.Executable);
        Assert.Equal("visualize", command.Arguments[0]);
        Assert.Equal(TestRequests.Valid().InputPath, command.Arguments[1]);

        // Default-valued options are omitted in compact mode.
        Assert.DoesNotContain("--width", command.Arguments);
        Assert.DoesNotContain("--height", command.Arguments);
        Assert.DoesNotContain("--fps", command.Arguments);
        Assert.DoesNotContain("--channels", command.Arguments);
        Assert.DoesNotContain("--past-seconds", command.Arguments);
        Assert.DoesNotContain("--effects", command.Arguments);
        Assert.DoesNotContain("--preset", command.Arguments);
        Assert.DoesNotContain("--layout", command.Arguments);
    }

    [Fact]
    public void Compact_EmitsNonDefaultValues()
    {
        VisualizationRequest request = TestRequests.Valid() with
        {
            Width = 1920,
            Height = 1080,
            Effects = VisualizationEffects.Cinematic,
            Title = "My Song",
            ChannelSelection = ChannelSelectionMode.All,
            Overwrite = true,
        };
        CanonicalCommand command = Format(request);

        int widthIndex = Assert.Single(FindAll(command.Arguments, "--width"));
        Assert.Equal("1920", command.Arguments[widthIndex + 1]);
        Assert.Contains("--effects", command.Arguments);
        Assert.Contains("--title", command.Arguments);
        Assert.Contains("--overwrite", command.Arguments);
        Assert.Contains("--channels", command.Arguments);
    }

    [Fact]
    public void FullyResolved_EmitsEverySetting()
    {
        CanonicalCommand command = Format(TestRequests.FullyPopulated(), CommandDisplayMode.FullyResolved);

        Assert.Contains("--preset", command.Arguments);
        Assert.Contains("--layout", command.Arguments);
        Assert.Contains("--width", command.Arguments);
        Assert.Contains("--height", command.Arguments);
        Assert.Contains("--fps", command.Arguments);
        Assert.Contains("--fps-denominator", command.Arguments);
        Assert.Contains("--past-seconds", command.Arguments);
        Assert.Contains("--future-seconds", command.Arguments);
        Assert.Contains("--scope-ratio", command.Arguments);
        Assert.Contains("--effects", command.Arguments);
        Assert.Contains("--analysis", command.Arguments);
        Assert.Contains("--title", command.Arguments);
        Assert.Contains("--loops", command.Arguments);
        Assert.Contains("--encoder", command.Arguments);
        Assert.Contains("--final-quality", command.Arguments);
        Assert.Contains("--overwrite", command.Arguments);
        Assert.Contains("--include-track", command.Arguments);
        Assert.Contains("--exclude-track", command.Arguments);
        Assert.Contains("--analysis-detail", command.Arguments);
        Assert.Contains("--time-grid", command.Arguments);
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
            "--preset", "--layout", "--width", "--height", "--fps", "--fps-denominator",
            "--channels", "--include-track", "--exclude-track", "--past-seconds", "--future-seconds",
            "--scope-ratio", "--scope-position", "--group-by", "--time-grid", "--roll-zoom",
            "--effects", "--note-color", "--font",
            "--analysis", "--analysis-detail", "--analysis-overlay", "--analysis-python", "--analysis-cache",
            "--analysis-force", "--analysis-timeout-minutes",
            "--title", "--subtitle", "--credits",
            "--loops", "--fade", "--tail", "--max-duration", "--timeout", "--sample-rate",
            "--ssg-gain-db", "--spc-pitch", "--backend", "--scopes",
            "--encoder", "--corrscope", "--ffmpeg", "--final-quality", "--tool-timeout-minutes",
            "--overwrite",
        ];

        int[] positions = expectedOrder
            .Select(option => Array.IndexOf(options, option))
            .ToArray();
        // Every expected option is present and strictly increasing.
        Assert.All(positions, position => Assert.True(position >= 0, $"missing option"));
        for (int i = 1; i < positions.Length; i++)
            Assert.True(positions[i] > positions[i - 1], $"order violated at index {i} ({expectedOrder[i]})");
    }

    [Fact]
    public void CustomChannels_EmitsIncludeAndExclude()
    {
        VisualizationRequest request = TestRequests.Valid() with
        {
            ChannelSelection = ChannelSelectionMode.Custom,
            IncludedTrackIds = new[] { "ym2608.0.fm.1" },
            ExcludedTrackIds = new[] { "ym2608.0.rhythm.1" },
        };
        CanonicalCommand command = Format(request);
        Assert.Contains("--include-track", command.Arguments);
        Assert.Contains("ym2608.0.fm.1", command.Arguments);
        Assert.Contains("--exclude-track", command.Arguments);
        Assert.Contains("ym2608.0.rhythm.1", command.Arguments);
    }

    [Fact]
    public void DisplayText_QuotesArgumentsWithSpaces()
    {
        VisualizationRequest request = TestRequests.Valid() with { Title = "My Song Title" };
        CanonicalCommand command = Format(request);
        // Arguments list is unquoted; display text quotes the value.
        Assert.Contains("My Song Title", command.Arguments);
        Assert.Contains("\"My Song Title\"", command.DisplayText);
    }

    [Fact]
    public void FinalPreset_AlwaysEmitsFinalQuality()
    {
        VisualizationRequest request = TestRequests.Valid() with { Preset = VisualizationPreset.Final };
        CanonicalCommand command = Format(request, CommandDisplayMode.FullyResolved);
        Assert.Contains("--final-quality", command.Arguments);
    }

    [Fact]
    public void OutputDirectory_MatchesDefault_OmitsDashO()
    {
        string input = "/music/song.vgz";
        string output = "/music/song.visualization/visualization.mp4";
        VisualizationRequest request = TestRequests.Valid(input, output);
        CanonicalCommand command = Format(request);
        // Both -o and --video equal the CLI defaults, so compact mode omits them.
        Assert.DoesNotContain("-o", command.Arguments);
        Assert.DoesNotContain("--video", command.Arguments);
        Assert.Equal(new[] { "visualize", input }, command.Arguments);
    }

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
