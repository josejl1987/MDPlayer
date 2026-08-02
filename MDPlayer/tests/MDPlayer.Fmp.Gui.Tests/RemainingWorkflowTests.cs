using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Xunit;

namespace Fmp.Gui.Tests;

public sealed class RemainingWorkflowTests
{
    [Fact]
    public void RecentFile_IsAvailableOnlyWhenFileExists()
    {
        string missing = Path.Combine(Path.GetTempPath(), "mdplayer-missing-" + Guid.NewGuid() + ".vgz");
        var missingItem = new RecentFileItemViewModel(missing, _ => Task.CompletedTask);

        Assert.Equal("Input", missingItem.Kind);
        Assert.False(missingItem.IsAvailable);
        Assert.False(missingItem.OpenCommand.CanExecute(null));

        string path = Path.Combine(Path.GetTempPath(), "mdplayer-recent-" + Guid.NewGuid() + ".vgz");
        File.WriteAllBytes(path, new byte[] { 0x56, 0x67, 0x6d });
        try
        {
            var item = new RecentFileItemViewModel(path, _ => Task.CompletedTask);
            Assert.True(item.IsAvailable);
            Assert.True(item.OpenCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GuiSettings_SavePersistsAndRoundTrips()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"gui-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new GuiSettingsStore(settingsPath);
            store.Update(s =>
            {
                s.RenderCliPath = "/opt/mdplayer/bin/mdplayer-render";
                s.PreviewMaxWidth = 1280;
                s.PreviewMaxHeight = 720;
                s.ThemeVariant = "Dark";
                s.RecentFiles.Add("/tmp/song.vgz");
            });
            store.Save();

            var reloaded = new GuiSettingsStore(settingsPath);
            Assert.Equal("/opt/mdplayer/bin/mdplayer-render", reloaded.Settings.RenderCliPath);
            Assert.Equal(1280, reloaded.Settings.PreviewMaxWidth);
            Assert.Equal(720, reloaded.Settings.PreviewMaxHeight);
            Assert.Equal("Dark", reloaded.Settings.ThemeVariant);
            Assert.Equal(["/tmp/song.vgz"], reloaded.Settings.RecentFiles);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }
}