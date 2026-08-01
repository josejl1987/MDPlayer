using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Fmp.Gui.Views;
using Xunit;

namespace Fmp.Gui.Tests;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaTheory]
    [InlineData(1024, 700)]
    [InlineData(1280, 820)]
    public async Task EmptyState_IsStateCorrectAndRenders(int width, int height)
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");
        (MainWindow window, MainWindowViewModel viewModel) = CreateWindow(new EmptyPreviewFactory(), settingsPath);
        window.Width = width;
        window.Height = height;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.FindControl<Button>("OpenInputButton")!.IsEffectivelyEnabled);
        Assert.False(window.FindControl<Button>("RenderButton")!.IsEffectivelyEnabled);
        Assert.False(window.FindControl<Control>("InspectorPanel")!.IsEffectivelyEnabled);
        Assert.False(window.FindControl<Control>("TimelineTransport")!.IsEffectivelyEnabled);
        Assert.True(window.FindControl<Control>("EmptyState")!.IsVisible);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame!.PixelSize);
        AssertFrameContainsPaintedPixels(frame);

        await viewModel.ShutdownAsync();
        File.Delete(settingsPath);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task EmptyState_RendersInBothThemeVariants(string variant)
    {
        Avalonia.Application.Current!.RequestedThemeVariant =
            variant == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");
        (MainWindow window, MainWindowViewModel viewModel) = CreateWindow(new EmptyPreviewFactory(), settingsPath);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(1280, 820), frame!.PixelSize);
        AssertFrameContainsPaintedPixels(frame);
        Assert.True(window.FindControl<Control>("EmptyState")!.IsVisible);

        await viewModel.ShutdownAsync();
        window.Hide();
        File.Delete(settingsPath);
    }

    [AvaloniaFact]
    public async Task LoadedInput_EnablesInspectorAndTransport()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-test-" + Guid.NewGuid() + ".vgz");
        await File.WriteAllBytesAsync(inputPath, new byte[] { 0 });
        string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");
        (MainWindow window, MainWindowViewModel viewModel) = CreateWindow(new EmptyPreviewFactory(), settingsPath);
        try
        {
            window.Show();
            await viewModel.OpenInputAsync(inputPath);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.FindControl<Control>("InspectorPanel")!.IsEffectivelyEnabled);
            Assert.True(window.FindControl<Control>("TimelineTransport")!.IsEffectivelyEnabled);
            Assert.False(window.FindControl<Control>("EmptyState")!.IsVisible);
            Assert.True(viewModel.HasInput);
            Assert.True(viewModel.HasTimeline);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(1280, 820), frame!.PixelSize);
        AssertFrameContainsPaintedPixels(frame);
        }
        finally
        {
            await viewModel.ShutdownAsync();
            window.Hide();
            File.Delete(inputPath);
            File.Delete(settingsPath);
        }
    }

    [AvaloniaFact]
    public async Task CompositionCards_ApplyLayoutAndHighlight()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-test-" + Guid.NewGuid() + ".vgz");
        await File.WriteAllBytesAsync(inputPath, new byte[] { 0 });
        string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");
        (MainWindow window, MainWindowViewModel viewModel) = CreateWindow(new EmptyPreviewFactory(), settingsPath);
        try
        {
            window.Show();
            await viewModel.OpenInputAsync(inputPath);
            Dispatcher.UIThread.RunJobs();

            IReadOnlyList<CompositionCardViewModel> cards = viewModel.Settings.Basic.CompositionCards;
            Assert.Single(cards);

            CompositionCardViewModel diagnostic = cards[0];
            Assert.Equal(CompositionKind.Diagnostic, diagnostic.Composition);
            diagnostic.SelectCommand.Execute(null);

            Assert.True(diagnostic.IsSelected, "The clicked composition card must highlight.");

            // The diagnostic composition is the default, so the compact
            // command omits the explicit --composition flag.
            await viewModel.UpdateCommandAsync();
            Assert.DoesNotContain("--composition", viewModel.Command.DisplayText);
        }
        finally
        {
            await viewModel.ShutdownAsync();
            window.Hide();
            File.Delete(inputPath);
            File.Delete(settingsPath);
        }
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel) CreateWindow(
        IVisualizationPreviewSessionFactory factory,
        string settingsPath)
    {
        var settings = new GuiSettingsStore(settingsPath);
        var dialogs = new FileDialogService();
        var clipboard = new ClipboardService();
        var viewModel = new MainWindowViewModel(
            settings,
            dialogs,
            clipboard,
            new ExportProcessService(null),
            new NotificationService(),
            factory,
            initialInputPath: null);
        return (new MainWindow(viewModel), viewModel);
    }

    private static void AssertFrameContainsPaintedPixels(WriteableBitmap frame)
    {
        using ILockedFramebuffer locked = frame.Lock();
        byte[] pixels = new byte[locked.RowBytes * locked.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, pixels, 0, pixels.Length);
        Assert.True(pixels.Any(pixel => pixel != 0), "The captured UI frame was blank.");
        Assert.True(pixels.Distinct().Count() > 8, "The captured UI frame did not contain varied painted content.");
    }

    private sealed class EmptyPreviewFactory : IVisualizationPreviewSessionFactory
    {
        public Task<IVisualizationPreviewSession> OpenAsync(string inputPath, CancellationToken cancellationToken)
            => Task.FromResult<IVisualizationPreviewSession>(new EmptyPreviewSession(inputPath));
    }

    private sealed class EmptyPreviewSession : IVisualizationPreviewSession
    {
        private static readonly byte[] PreviewPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

        public EmptyPreviewSession(string inputPath)
        {
            Input = new VisualizationInputInfo
            {
                FullPath = inputPath,
                DisplayName = Path.GetFileName(inputPath),
                Format = "VGZ",
                EstimatedDuration = TimeSpan.FromMinutes(3),
                Tracks = [new TrackCapabilityInfo { TrackId = "track-1", DisplayName = "Track 1" }],
            };
        }

        public VisualizationInputInfo Input { get; }
        public VisualizationSessionCapabilities Capabilities { get; } = new()
        {
            SemanticCapture = true,
            HasCapturedTimeline = true,
        };

        public Task<VisualizationPlanResult> PlanAsync(VisualizationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new VisualizationPlanResult
            {
                ResolvedLayout = "unified",
                RequestedLayout = "auto",
                InputPath = request.InputPath,
                EstimatedDurationSeconds = 180,
                RepresentativePoints = [new RepresentativePoint { Kind = "start", TimeSeconds = 0, Label = "Start" }],
            });

        public Task<PreviewFrameResult> RenderFrameAsync(
            VisualizationRequest request,
            PreviewFrameRequest preview,
            CancellationToken cancellationToken)
            => Task.FromResult(new PreviewFrameResult
            {
                Fidelity = preview.Fidelity,
                TimeSeconds = preview.TimeSeconds,
                Width = 1,
                Height = 1,
                PngBytes = PreviewPng,
            });

        public Task<MotionPreviewResult> RenderMotionAsync(
            VisualizationRequest request,
            MotionPreviewRequest preview,
            IProgress<PreviewProgress>? progress,
            CancellationToken cancellationToken)
            => Task.FromResult(new MotionPreviewResult
            {
                FrameCount = 0,
                Fps = preview.Fps,
                Width = 1,
                Height = 1,
                FramesDirectory = Path.GetTempPath(),
            });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
