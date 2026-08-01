using Fmp.Gui.ViewModels;
using Xunit;

namespace Fmp.Gui.Tests;

public sealed class RemainingWorkflowTests
{
    [Fact]
    public void RecentProject_ExposesProjectKindAndDisablesMissingEntry()
    {
        var item = new RecentFileItemViewModel("/path/does-not-exist.mdpviz.json", _ => Task.CompletedTask);

        Assert.Equal("Project", item.Kind);
        Assert.False(item.IsAvailable);
        Assert.False(item.OpenCommand.CanExecute(null));
    }

    [Fact]
    public void ToolDetailsCommand_RaisesDetailsRequest()
    {
        var viewModel = new ToolStatusViewModel(() => null, () => null, null);
        bool raised = false;
        viewModel.DetailsRequested += () => raised = true;

        viewModel.ShowDetailsCommand.Execute(null);

        Assert.True(raised);
        Assert.Equal("Checking tools…", viewModel.Summary);
    }
}
