using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Fmp.Gui.Views;
using Xunit;

namespace Fmp.Gui.Tests;

/// <summary>
/// PR7 headless layout tests: render the real <see cref="MainWindow"/> at the
/// supported sizes and assert the studio layout invariants (sidebar width,
/// preview dominance, no overlap, compact chrome) from actual control Bounds.
/// </summary>
public sealed class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1024, 700)]
    [InlineData(1440, 900)]
    [InlineData(1920, 1080)]
    public async Task MainLayout_UsesExpectedProportions(double width, double height)
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = width;
        window.Height = height;

        window.Show();
        window.UpdateLayout();

        Grid mainContent =
            Required<Grid>(window, "MainContent");

        Grid sidebar =
            Required<Grid>(window, "SettingsSidebar");

        Grid preview =
            Required<Grid>(window, "PreviewPanel");

        Border toolbar =
            Required<Border>(window, "TopToolbar");

        Border status =
            Required<Border>(window, "StatusBar");

        GridSplitter splitter =
            Required<GridSplitter>(window, "MainSplitter");

        Button render =
            Required<Button>(window, "RenderButton");

        Border previewViewport =
            Required<Border>(window, "PreviewViewport");

        Control transport =
            Required<Control>(window, "TimelineTransport");

        Slider slider =
            Required<Slider>(window, "PreviewSlider");

        Assert.InRange(sidebar.Bounds.Width, 280, 400);

        Assert.True(
            preview.Bounds.Width
            >= mainContent.Bounds.Width * 0.60,
            $"Preview width {preview.Bounds.Width} was less than "
            + $"60% of main width {mainContent.Bounds.Width}.");

        Assert.True(
            preview.Bounds.Height
            >= mainContent.Bounds.Height * 0.65,
            $"Preview height {preview.Bounds.Height} was less than "
            + $"65% of main height {mainContent.Bounds.Height}.");

        Assert.True(
            sidebar.Bounds.Right <= preview.Bounds.Left,
            "Sidebar overlaps the preview.");

        Assert.InRange(toolbar.Bounds.Height, 40, 72);
        Assert.InRange(status.Bounds.Height, 24, 56);
        Assert.InRange(splitter.Bounds.Width, 4, 8);

        Assert.True(render.IsEffectivelyVisible);
        Assert.True(render.Bounds.Width >= 90);
        Assert.True(render.Bounds.Height >= 32);

        Assert.True(previewViewport.IsEffectivelyVisible);
        Assert.True(transport.IsEffectivelyVisible);
        Assert.True(slider.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task MinimumWindow_KeepsPrimaryWorkflowVisible()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = 1024;
        window.Height = 700;

        window.Show();
        window.UpdateLayout();

        AssertVisible(window, "OpenInputButton");
        AssertVisible(window, "CompositionSelector");
        AssertVisible(window, "PreviewViewport");
        AssertVisible(window, "TimelineTransport");
        AssertVisible(window, "PreviewSlider");
        AssertVisible(window, "RenderButton");
    }

    [AvaloniaFact]
    public async Task Preview_IsDominantRegion()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = 1440;
        window.Height = 900;

        window.Show();
        window.UpdateLayout();

        Grid sidebar =
            Required<Grid>(window, "SettingsSidebar");

        Grid preview =
            Required<Grid>(window, "PreviewPanel");

        Assert.True(
            preview.Bounds.Width >= sidebar.Bounds.Width * 2,
            "Preview should be at least twice as wide as the sidebar.");
    }

    [AvaloniaFact]
    public async Task ReadyWindow_ArrangesAllMajorRegions()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Show();
        window.UpdateLayout();

        string[] required =
        [
            "TopToolbar",
            "SettingsSidebar",
            "PreviewPanel",
            "PreviewViewport",
            "TimelineTransport",
            "StatusBar",
        ];

        foreach (string name in required)
            AssertVisible(window, name);
    }

    [AvaloniaFact]
    public async Task Chrome_SidebarAndStatusStayWithinWindow()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = 1024;
        window.Height = 700;

        window.Show();
        window.UpdateLayout();

        StackPanel lastSection =
            Required<StackPanel>(window, "SidebarLastSection");

        StackPanel sidebarContent =
            Required<StackPanel>(window, "SidebarScrollContent");

        Border statusBar =
            Required<Border>(window, "StatusBar");

        Grid mainContent =
            Required<Grid>(window, "MainContent");

        ScrollViewer settingsScroll =
            Required<ScrollViewer>(window, "SettingsScroll");

        Assert.True(
            settingsScroll.Bounds.Bottom <= statusBar.Bounds.Top,
            "The settings scroll viewport must end above the status bar.");

        Assert.True(
            lastSection.Bounds.Bottom <= sidebarContent.Bounds.Bottom,
            "The last sidebar section must not overflow the scroll content.");

        Assert.True(
            statusBar.Bounds.Bottom <= window.ClientSize.Height,
            "The status bar must not extend past the window bottom.");

        Assert.True(
            mainContent.Bounds.Bottom <= statusBar.Bounds.Top,
            "The main content must not overlap the status bar.");
    }

    private static void AssertVisible(Control root, string name)
    {
        Control? control = root.FindControl<Control>(name);

        Assert.NotNull(control);
        Assert.True(control!.IsEffectivelyVisible);
        Assert.True(control.Bounds.Width > 0);
        Assert.True(control.Bounds.Height > 0);
    }

    private static T Required<T>(Control root, string name)
        where T : Control
    {
        T? control = root.FindControl<T>(name);

        Assert.NotNull(control);
        Assert.True(
            control!.Bounds.Width > 0,
            $"{name} has zero width.");
        Assert.True(
            control.Bounds.Height > 0,
            $"{name} has zero height.");

        return control;
    }

    /// <summary>
    /// Ready-state window fixture: input loaded, plan + preview image available,
    /// output path set, no fatal validation error, render enabled.
    /// </summary>
    private sealed class TestWindowFixture : IAsyncDisposable
    {
        private readonly string _inputPath;
        private readonly string _settingsPath;

        public MainWindow Window { get; }
        public MainWindowViewModel ViewModel { get; }

        public static async Task<TestWindowFixture> CreateReadyAsync()
        {
            string inputPath = Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-layout-" + Guid.NewGuid() + ".vgz");
            await File.WriteAllBytesAsync(inputPath, new byte[] { 0x56, 0x67, 0x6d });
            string settingsPath = Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");

            var factory = new RecordingPreviewFactory();
            var viewModel = new MainWindowViewModel(
                new GuiSettingsStore(settingsPath),
                new FileDialogService(),
                new ClipboardService(),
                new ExportProcessService(null),
                factory,
                initialInputPath: null);

            var fixture = new TestWindowFixture(
                new MainWindow(viewModel), viewModel, inputPath, settingsPath);

            await viewModel.OpenInputAsync(inputPath);
            await viewModel.WaitForPreviewRefreshAsync();
            viewModel.SetOutputPath(Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-layout-output-" + Guid.NewGuid() + ".mp4"));

            Assert.Equal(GuiState.Ready, viewModel.State);
            Assert.NotNull(viewModel.Preview.CurrentImage);
            Assert.False(viewModel.HasFatalValidationIssues);
            Assert.True(viewModel.CanRender);

            return fixture;
        }

        private TestWindowFixture(
            MainWindow window,
            MainWindowViewModel viewModel,
            string inputPath,
            string settingsPath)
        {
            Window = window;
            ViewModel = viewModel;
            _inputPath = inputPath;
            _settingsPath = settingsPath;
        }

        public async ValueTask DisposeAsync()
        {
            await ViewModel.ShutdownAsync();
            Window.Hide();
            File.Delete(_inputPath);
            File.Delete(_settingsPath);
        }
    }
}
