using Fmp.Application.Contracts;
using Fmp.Gui.Services;
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

    [Fact]
    public void ToolSettings_SavePersistsPathsAndRoundTrips()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"tool-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new GuiSettingsStore(settingsPath);
            var viewModel = new ToolSettingsViewModel(store);

            viewModel.RenderCliPath = "/opt/mdplayer/bin/mdplayer-render";
            viewModel.FmpComPath = "/opt/fmp/FMP.COM";
            viewModel.CorrscopePath = "/opt/corrscope/corrscope";
            viewModel.FfmpegPath = "/usr/bin/ffmpeg";
            viewModel.AnalysisPython = "/usr/bin/python3";
            viewModel.AssetsDir = "/opt/mdplayer/assets";
            Assert.True(viewModel.IsDirty);

            viewModel.SaveCommand.Execute(null);
            Assert.False(viewModel.IsDirty);

            var reloaded = new GuiSettingsStore(settingsPath);
            Assert.Equal("/opt/mdplayer/bin/mdplayer-render", reloaded.Settings.RenderCliPath);
            Assert.Equal("/opt/fmp/FMP.COM", reloaded.Settings.FmpComPath);
            Assert.Equal("/opt/corrscope/corrscope", reloaded.Settings.CorrscopePath);
            Assert.Equal("/usr/bin/ffmpeg", reloaded.Settings.FfmpegPath);
            Assert.Equal("/usr/bin/python3", reloaded.Settings.AnalysisPython);
            Assert.Equal("/opt/mdplayer/assets", reloaded.Settings.AssetsDir);

            ToolPaths paths = reloaded.Settings.ToToolPaths();
            Assert.Equal("/opt/fmp/FMP.COM", paths.FmpComPath);
            Assert.Equal("/opt/corrscope/corrscope", paths.CorrscopePath);
            Assert.Equal("/usr/bin/ffmpeg", paths.FfmpegPath);
            Assert.Equal("/usr/bin/python3", paths.AnalysisPython);
            Assert.Equal("/opt/mdplayer/assets", paths.AssetsDir);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void ToolSettings_EmptyFieldsSaveAsNull()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"tool-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new GuiSettingsStore(settingsPath);
            store.Update(s => s.RenderCliPath = "/old/path");
            var viewModel = new ToolSettingsViewModel(store);

            viewModel.RenderCliPath = "   ";
            viewModel.SaveCommand.Execute(null);

            var reloaded = new GuiSettingsStore(settingsPath);
            Assert.Null(reloaded.Settings.RenderCliPath);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }
}
