using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
        Assert.False(window.FindControl<Control>("SettingsSidebar")!.IsEffectivelyEnabled);
        Assert.False(window.FindControl<Control>("TimelineTransport")!.IsEffectivelyEnabled);
        Assert.True(window.FindControl<Control>("EmptyState")!.IsVisible);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame!.PixelSize);
        AssertFrameContainsPaintedPixels(frame);

        await viewModel.ShutdownAsync();
        File.Delete(settingsPath);
    }

    [AvaloniaFact]
    public async Task EmptyState_RendersInDarkTheme()
    {
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

            Assert.True(window.FindControl<Control>("SettingsSidebar")!.IsEffectivelyEnabled);
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
    public async Task LoadedInput_CompositionSelectionAppliesToRequest()
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

            IReadOnlyList<CompositionOptionViewModel> options = viewModel.Settings.Basic.Compositions;
            Assert.NotEmpty(options);
            CompositionOptionViewModel option = options[0];
            viewModel.Settings.Basic.SelectedComposition = option;
            await viewModel.WaitForPreviewRefreshAsync();

            Assert.Equal(option.Value, viewModel.Request!.Composition);
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

        public PreviewRequestImpact ClassifyChange(
            VisualizationRequest previous,
            VisualizationRequest next)
        {
            if (previous.InputPath != next.InputPath
                || previous.Playback != next.Playback)
                return PreviewRequestImpact.TimelineCapture;
            if (previous.Tracks != next.Tracks
                || previous.Composition != next.Composition
                || previous.View != next.View
                || previous.Output.Width != next.Output.Width
                || previous.Output.Height != next.Output.Height
                || previous.Output.FpsNumerator != next.Output.FpsNumerator
                || previous.Output.FpsDenominator != next.Output.FpsDenominator)
                return PreviewRequestImpact.Plan;
            if (previous.Output.Quality != next.Output.Quality
                || previous.Style != next.Style
                || previous.Presentation != next.Presentation)
                return PreviewRequestImpact.Frame;
            if (previous.OutputPath != next.OutputPath
                || previous.Output.Encoder != next.Output.Encoder
                || previous.Output.Overwrite != next.Output.Overwrite)
                return PreviewRequestImpact.ExportOnly;
            return PreviewRequestImpact.None;
        }

        public Task<ReusableCaptureLease> AcquireReusableCaptureAsync(
            VisualizationRequest request,
            CancellationToken cancellationToken)
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "mdplayer-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var lease = new ReusableCaptureLease(
                dir,
                "headless-capture-key",
                () => { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } });
            return Task.FromResult(lease);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
