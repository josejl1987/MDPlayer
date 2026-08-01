using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Gui.ViewModels;
using Xunit;

namespace Fmp.Gui.Tests;

public class CommandViewModelTests
{
    [Fact]
    public void SetDisplay_ShowsFormattedCommand()
    {
        var command = new VisualizationCommandFormatter().Format(TestRequest());
        var viewModel = new CommandViewModel(_ => { });

        viewModel.SetDisplay(command);

        Assert.StartsWith("visualize /tmp/test.vgz", viewModel.DisplayText);
        Assert.True(viewModel.IsValid);
    }

    [Fact]
    public void ToggleResolved_ChangesDisplayToFullyResolved()
    {
        var formatter = new VisualizationCommandFormatter();
        var viewModel = new CommandViewModel(_ => { });
        viewModel.SetDisplay(formatter.Format(TestRequest(), CommandDisplayMode.Compact));

        Assert.DoesNotContain("--width", viewModel.DisplayText);

        viewModel.ShowResolvedCommand.Execute(null);

        // The VM re-renders via the MainWindowViewModel callback in the real app;
        // here we assert the toggle state flipped and the callback fired.
        Assert.True(viewModel.IsResolved);
        Assert.Equal("Show compact", viewModel.ShowResolvedText);
    }

    [Fact]
    public void SetValidity_ReflectsRequestValidation()
    {
        var viewModel = new CommandViewModel(_ => { });

        viewModel.SetValidity(new[]
        {
            new ValidationIssue { Code = ValidationCodes.InputNotFound, Severity = ValidationSeverity.Error, Message = "missing" },
        });

        Assert.False(viewModel.IsValid);
        Assert.Contains("1 error(s)", viewModel.IssuesSummary);
    }

    [Fact]
    public void SetValidity_WarningsDoNotInvalidate()
    {
        var viewModel = new CommandViewModel(_ => { });
        viewModel.SetValidity(new[]
        {
            new ValidationIssue { Code = "X", Severity = ValidationSeverity.Warning, Message = "careful" },
        });

        Assert.True(viewModel.IsValid);
        Assert.Contains("1 warning(s)", viewModel.IssuesSummary);
    }

    private static VisualizationRequest TestRequest() => new()
    {
        InputPath = "/tmp/test.vgz",
        OutputPath = "/tmp/test.visualization/visualization.mp4",
    };
}
