using Fmp.Application.Contracts;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Cli;

/// <summary>
/// Parsing of the standalone preview command's <c>--fidelity</c> option. The
/// mapping is exact: the four supported names map to four distinct preview
/// fidelities and any other value is an argument error (exit code 2). The
/// caller must never silently fall back to accurate.
/// </summary>
public sealed class PreviewCommandParsingTests
{
    [Theory]
    [InlineData("layout", PreviewFidelity.Layout)]
    [InlineData("timeline", PreviewFidelity.TimelineStill)]
    [InlineData("interactive", PreviewFidelity.InteractiveStill)]
    [InlineData("accurate", PreviewFidelity.AccurateStill)]
    public void FidelityName_MapsToExactFidelity(string raw, PreviewFidelity expected)
    {
        Assert.Equal(expected, PreviewCommand.ParsePreviewFidelity(raw));
    }

    [Fact]
    public void FidelityParsing_IsCaseInsensitiveAndTrims()
    {
        Assert.Equal(PreviewFidelity.InteractiveStill, PreviewCommand.ParsePreviewFidelity("  Interactive "));
        Assert.Equal(PreviewFidelity.TimelineStill, PreviewCommand.ParsePreviewFidelity("TIMELINE"));
    }

    [Fact]
    public void UnknownFidelity_ReturnsExitCodeTwo()
    {
        int exitCode = PreviewCommand.Handle(["--fidelity", "bogus"]);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void UnknownFidelity_DoesNotMapToAccurate()
    {
        Assert.Throws<ArgumentException>(() => PreviewCommand.ParsePreviewFidelity("bogus"));
    }

    [Fact]
    public void HelpText_ListsAllFourFidelityNames()
    {
        string help = PreviewCommand.FidelityHelpLine;
        Assert.Contains("layout", help);
        Assert.Contains("timeline", help);
        Assert.Contains("interactive", help);
        Assert.Contains("accurate", help);
        Assert.Contains("|", help);
    }
}
