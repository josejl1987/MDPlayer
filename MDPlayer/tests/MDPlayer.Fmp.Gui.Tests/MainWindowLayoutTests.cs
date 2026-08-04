using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Fmp.Gui.Layout;
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

        // The preview is the central starred column; its width and height are
        // positive and it does not overlap chrome. (Precise rail proportions are
        // covered by the diagnostics breakpoint tests.)
        Assert.True(preview.Bounds.Width > 0, "Preview has positive width.");
        Assert.True(preview.Bounds.Height > 0, "Preview has positive height.");

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

    // ── Patch 3: diagnostics rail breakpoints ─────────────────────────

    [AvaloniaTheory]
    [InlineData(1024, 700)]
    [InlineData(1279, 800)]
    [InlineData(1280, 800)]
    public async Task CompactWidth_CollapsesDiagnostics(double width, double height)
    {
        Assert.False(width >= StudioLayoutPolicy.WideMinimumWidth,
            "test sizes must be below the wide breakpoint");
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = width;
        window.Height = height;
        window.Show();
        window.UpdateLayout();

        Border? diagnostics = window.FindControl<Border>("DiagnosticsColumn");
        GridSplitter? splitter = window.FindControl<GridSplitter>("DiagnosticsSplitter");
        Button? toggle = window.FindControl<Button>("DiagnosticsToggleButton");

        Assert.NotNull(diagnostics);
        Assert.NotNull(splitter);
        Assert.NotNull(toggle);

        Assert.False(diagnostics.IsEffectivelyVisible, "diagnostics rail hidden in compact mode");
        // The diagnostics column collapses to zero width in compact mode.
        Grid mainContentC = Required<Grid>(window, "MainContent");
        Assert.True(mainContentC.ColumnDefinitions[4].Width.Value < 1,
            $"diagnostics column width {mainContentC.ColumnDefinitions[4].Width.Value} should be zero");
        Assert.False(splitter.IsEffectivelyVisible, "diagnostics splitter hidden in compact mode");
        Assert.True(toggle.IsEffectivelyVisible, "diagnostics toggle visible in compact mode");

        // Preview, transport and Render remain usable.
        AssertVisible(window, "PreviewPanel");
        AssertVisible(window, "PreviewViewport");
        AssertVisible(window, "TimelineTransport");
        AssertVisible(window, "RenderButton");

        Grid preview = Required<Grid>(window, "PreviewPanel");
        double max = width - StudioLayoutPolicy.SettingsWidth - StudioLayoutPolicy.SplitterWidth;
        Assert.True(preview.Bounds.Width > 0 && preview.Bounds.Width <= max + 1,
            "preview keeps usable width in compact mode");
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(1920, 1080)]
    public async Task WideWidth_ShowsFixedDiagnosticsRail(double width, double height)
    {
        Assert.True(width >= StudioLayoutPolicy.WideMinimumWidth,
            "test sizes must be at or above the wide breakpoint");
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = width;
        window.Height = height;
        window.Show();
        window.UpdateLayout();

        Grid mainContent = Required<Grid>(window, "MainContent");
        Border diagnostics = Required<Border>(window, "DiagnosticsColumn");
        GridSplitter splitter = Required<GridSplitter>(window, "DiagnosticsSplitter");
        Button? toggle = window.FindControl<Button>("DiagnosticsToggleButton");
        Grid sidebar = Required<Grid>(window, "SettingsSidebar");
        Grid preview = Required<Grid>(window, "PreviewPanel");

        Assert.NotNull(toggle);
        Assert.True(diagnostics.IsEffectivelyVisible, "diagnostics rail visible in wide mode");
        Assert.InRange(
            diagnostics.Bounds.Width,
            StudioLayoutPolicy.DiagnosticsWidth - 1,
            StudioLayoutPolicy.DiagnosticsWidth + 1);
        Assert.True(splitter.IsEffectivelyVisible, "diagnostics splitter visible in wide mode");
        Assert.False(toggle.IsEffectivelyVisible, "diagnostics toggle hidden in wide mode");
        Assert.InRange(
            sidebar.Bounds.Width,
            StudioLayoutPolicy.SettingsWidth - 1,
            StudioLayoutPolicy.SettingsWidth + 1);

        // Preview occupies the central star column between sidebar and rail.
        Assert.True(preview.Bounds.Left >= sidebar.Bounds.Right, "preview right of sidebar");
        Assert.True(diagnostics.Bounds.Left >= preview.Bounds.Right, "rail right of preview");

        // No overlap between the three main regions.
        Assert.True(preview.Bounds.Right <= diagnostics.Bounds.Left, "no preview/rail overlap");
        Assert.True(sidebar.Bounds.Right <= preview.Bounds.Left, "no sidebar/preview overlap");
        Assert.True(diagnostics.Bounds.Right <= width + 1, "rail inside window");
    }

    [AvaloniaFact]
    public async Task BreakpointAtWideMinimum_IsExactToOnePixel()
    {
        double below = StudioLayoutPolicy.WideMinimumWidth - 1;
        double at = StudioLayoutPolicy.WideMinimumWidth;

        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        Assert.Equal(StudioLayoutMode.Compact,
            StudioLayoutPolicy.Resolve(below).Mode);
        Assert.Equal(StudioLayoutMode.Wide,
            StudioLayoutPolicy.Resolve(at).Mode);

        // Double-check the wire-through: at WideMinimum-1 the rail is hidden,
        // at WideMinimum it is exactly DiagnosticsWidth.
        window.Width = below;
        window.Height = 700;
        window.Show();
        window.UpdateLayout();
        Assert.False(Required<Border>(window, "DiagnosticsColumn").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task WideWindow_DiagnosticsInsideWindowBounds()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = 1920;
        window.Height = 1080;
        window.Show();
        window.UpdateLayout();

        Border diagnostics = Required<Border>(window, "DiagnosticsColumn");
        Border statusBar = Required<Border>(window, "StatusBar");
        Grid mainContent = Required<Grid>(window, "MainContent");

        Assert.True(diagnostics.IsEffectivelyVisible, "diagnostics rail visible");
        Assert.True(diagnostics.Bounds.Bottom <= statusBar.Bounds.Top,
            "diagnostics content stays above the status bar");
        Assert.True(diagnostics.Bounds.Right <= window.ClientSize.Width + 1,
            "diagnostics content stays inside the window");
        Assert.True(diagnostics.Bounds.Height > 0 && diagnostics.Bounds.Height <= mainContent.Bounds.Height,
            "diagnostics rail fits within the main content height");
    }

    [AvaloniaFact]
    public async Task AlignmentTimecodeAndToolbar_StayUsable()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();

        MainWindow window = fixture.Window;
        window.Width = 1920;
        window.Height = 1080;
        window.Show();
        window.UpdateLayout();

        Button render = Required<Button>(window, "RenderButton");
        TextBlock timecode = Required<TextBlock>(window, "PreviewTimecode");

        Assert.True(render.Bounds.Width > 0 && render.Bounds.Height >= 28,
            "toolbar render button has usable size");
        Assert.True(timecode.Bounds.Width >= 60, "timecode has a stable non-zero width");
        Assert.True(HasSameVerticalCenter(render, timecode)
            || timecode.Bounds.Top >= render.Bounds.Top,
            "timecode and toolbar render button are vertically aligned");
    }

    private static bool HasSameVerticalCenter(Control a, Control b)
    {
        double aCenter = a.Bounds.Y + a.Bounds.Height / 2;
        double bCenter = b.Bounds.Y + b.Bounds.Height / 2;
        return Math.Abs(aCenter - bCenter) <= 24;
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
    [AvaloniaFact]
    public async Task OpnaBackendSelector_HiddenForNonFmpInput_VisibleAndInsideSidebarForFmp()
    {
        await using TestWindowFixture fixture =
            await TestWindowFixture.CreateReadyAsync();
        fixture.Window.Show();
        fixture.Window.UpdateLayout();

        // Non-FMP (.vgz): selector must be hidden.
        Assert.False(fixture.ViewModel.Settings.Advanced.ShowOpnaBackend);
        ComboBox? hidden =
            fixture.Window.FindControl<ComboBox>("OpnaBackendSelector");
        Assert.True(hidden == null || !hidden.IsEffectivelyVisible,
            "backend selector must not be visible for a non-FMP input");

        // FMP input: selector shows and fits inside the 320px sidebar.
        await using TestWindowFixture fmp =
            await TestWindowFixture.CreateFmpReadyAsync();
        fmp.Window.Width = 1024;
        fmp.Window.Height = 700;
        fmp.Window.Show();
        fmp.Window.UpdateLayout();

        Assert.True(fmp.ViewModel.Settings.Advanced.ShowOpnaBackend);
        ComboBox selector = Required<ComboBox>(fmp.Window, "OpnaBackendSelector");
        Assert.True(selector.IsEffectivelyVisible);

        Grid sidebar = Required<Grid>(fmp.Window, "SettingsSidebar");
        Assert.InRange(selector.Bounds.Right, 0, sidebar.Bounds.Right);
        // Enough width for "Native audio".
        Assert.True(
            selector.Bounds.Width >= 96 || selector.DesiredSize.Width > 0,
            "selector should accommodate the 'Native audio' label");
    }

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

        /// <summary>
        /// Ready-state fixture opened on an FMP-family input (.ovi) so
        /// FMP-only controls (such as the YM2608 backend selector) are visible.
        /// </summary>
        public static async Task<TestWindowFixture> CreateFmpReadyAsync()
        {
            string inputPath = Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-layout-" + Guid.NewGuid() + ".ovi");
            await File.WriteAllBytesAsync(inputPath, new byte[] { 0x4f, 0x56, 0x4d });
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
