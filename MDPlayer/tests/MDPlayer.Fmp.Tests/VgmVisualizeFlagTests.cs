using System.Text;
using Fmp.Cli;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Flag-parity tests for the generic visualize path (VgmVisualizeCommand): the
/// --layout and --analysis* options documented in the shared usage text must
/// parse for register-log inputs (vgm/spc/s98/mdx/xgm/mid), not only .ovi.
/// </summary>
public sealed class VgmVisualizeFlagTests
{
    [Fact]
    public void Parse_ParsesLayoutDiagnostic()
    {
        var options = VisualizeCommand.ParseArgs(["track.vgm", "--layout", "diagnostic"]);

        Assert.NotNull(options);
        Assert.Equal(VisualizationLayoutMode.Diagnostic, options.LayoutMode);
    }

    [Fact]
    public void Parse_LayoutDefaultsToBalancedAutoPublishing()
    {
        var options = VisualizeCommand.ParseArgs(["track.vgm"]);

        Assert.NotNull(options);
        Assert.Equal(VisualizationLayoutMode.Auto, options.LayoutMode);
        Assert.Equal(VisualizationChannelFilter.Active, options.Channels);
    }

    [Fact]
    public void Parse_RejectsUnknownLayout()
    {
        (VisualizeOptions options, string stderr) = ParseCapture(["track.vgm", "--layout", "mosaic"]);

        Assert.Null(options);
        Assert.Contains("auto or diagnostic", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ParsesAnalysisFlagFamily()
    {
        var options = VisualizeCommand.ParseArgs(
        [
            "track.spc",
            "--analysis",
            "--analysis-python", "/usr/bin/python3",
            "--analysis-output", "out/analysis.json",
            "--analysis-cache", "cache",
            "--analysis-detail", "full",
            "--analysis-force",
            "--analysis-timeout-minutes", "20",
            "--analysis-overlay", "standard",
        ]);

        Assert.NotNull(options);
        Assert.True(options.Analysis);
        Assert.Equal("/usr/bin/python3", options.AnalysisPython);
        Assert.Equal("out/analysis.json", options.AnalysisOutput);
        Assert.Equal("cache", options.AnalysisCache);
        Assert.Equal(AnalysisDetail.Full, options.AnalysisDetail);
        Assert.True(options.AnalysisForce);
        Assert.Equal(20, options.AnalysisTimeoutMinutes);
        Assert.Equal("standard", options.AnalysisOverlay);
    }

    [Fact]
    public void Parse_AnalysisDefaultsMatchOviPath()
    {
        var options = VisualizeCommand.ParseArgs(["track.vgm"]);

        Assert.NotNull(options);
        Assert.False(options.Analysis);
        Assert.Equal(AnalysisDetail.Standard, options.AnalysisDetail);
        Assert.False(options.AnalysisForce);
        Assert.Equal(10, options.AnalysisTimeoutMinutes);
        Assert.Equal("minimal", options.AnalysisOverlay);
    }

    [Fact]
    public void Parse_RejectsUnknownAnalysisDetail()
    {
        (VisualizeOptions options, string stderr) = ParseCapture(["track.vgm", "--analysis-detail", "verbose"]);

        Assert.Null(options);
        Assert.Contains("minimal, standard, or full", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnknownAnalysisOverlay()
    {
        (VisualizeOptions options, string stderr) = ParseCapture(["track.vgm", "--analysis-overlay", "everything"]);

        Assert.Null(options);
        Assert.Contains("none, minimal, or standard", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsNonPositiveAnalysisTimeout()
    {
        (VisualizeOptions options, string stderr) = ParseCapture(["track.vgm", "--analysis-timeout-minutes", "0"]);

        Assert.Null(options);
        Assert.Contains("invalid visualization numeric option", stderr, StringComparison.Ordinal);
    }

    private static (VisualizeOptions, string) ParseCapture(string[] args)
    {
        var original = Console.Error;
        var buffer = new StringWriter();
        Console.SetError(buffer);
        try
        {
            return (VisualizeCommand.ParseArgs(args), buffer.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
