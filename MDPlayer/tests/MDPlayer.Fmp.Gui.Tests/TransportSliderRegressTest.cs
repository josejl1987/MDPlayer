using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Fmp.Gui.Views;
using Xunit;

namespace Fmp.Gui.Tests;

/// <summary>
/// Regression coverage for the transport scrubber. Dragging/clicking the
/// timeline slider must move the scrub position AND commit a seek frame. It
/// previously did neither in the real app: seeking was wired to the slider's
/// <c>PointerReleased</c>/<c>KeyUp</c>, but the Fluent template's thumb/track
/// consumes the pointer-release and marks it handled, so the release-driven
/// commit never fired and scrubbing did nothing. Seeking is now committed from
/// the slider's <c>ValueChanged</c> event (debounced + coalesced by the VM).
/// </summary>
public sealed class TransportSliderRegressTest
{
    private sealed class Harness : IAsyncDisposable
    {
        public MainWindowViewModel VM { get; }
        public RecordingPreviewFactory Factory { get; }
        public MainWindow Window { get; }
        private readonly string _inputPath;
        private readonly string _settingsPath;

        private Harness(
            string inputPath,
            string settingsPath,
            RecordingPreviewFactory factory,
            MainWindowViewModel vm,
            MainWindow window)
        {
            _inputPath = inputPath;
            _settingsPath = settingsPath;
            Factory = factory;
            VM = vm;
            Window = window;
        }

        public static async Task<Harness> CreateAsync()
        {
            string inputPath = Path.Combine(Path.GetTempPath(), "mdplayer-slider-" + Guid.NewGuid() + ".vgz");
            await File.WriteAllBytesAsync(inputPath, new byte[] { 0x56, 0x67, 0x6d });
            string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-slider-set-" + Guid.NewGuid() + ".json");

            var factory = new RecordingPreviewFactory();
            var vm = new MainWindowViewModel(
                new GuiSettingsStore(settingsPath),
                new FileDialogService(),
                new ClipboardService(),
                new ExportProcessService(null),
                factory,
                initialInputPath: null);
            var window = new MainWindow(vm);
            window.Show();
            window.UpdateLayout();
            await vm.OpenInputAsync(inputPath);
            await vm.WaitForPreviewRefreshAsync();
            return new Harness(inputPath, settingsPath, factory, vm, window);
        }

        public void Reset() => Factory.ResetCalls();

        public async ValueTask DisposeAsync()
        {
            await VM.ShutdownAsync();
            Window.Hide();
            File.Delete(_inputPath);
            File.Delete(_settingsPath);
        }
    }

    [AvaloniaFact]
    public async Task ClickingTransportSlider_CommitsASeekFrame()
    {
        await using var h = await Harness.CreateAsync();
        try
        {
            MainWindow window = h.Window;
            window.UpdateLayout();
            Slider slider = window.FindControl<Slider>("PreviewSlider");

            Assert.NotNull(slider);
            Assert.True(slider.IsEffectivelyVisible);
            Assert.True(slider.IsEffectivelyEnabled);
            Assert.True(slider.Maximum > 0, $"slider max {slider.Maximum}");

            double duration = h.VM.DurationSeconds;
            Assert.True(duration > 0, $"duration {duration}");

            // ---- A track click moves the scrub position and commits a frame ----
            h.Reset();
            double before = h.VM.PreviewScrubTime;
            Avalonia.Point topLeft = slider.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
            var clickAt = topLeft + new Avalonia.Point(slider.Bounds.Width * 0.6, slider.Bounds.Height / 2);

            window.MouseDown(clickAt, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(clickAt, MouseButton.Left, RawInputModifiers.None);
            window.UpdateLayout();
            await Task.Delay(50);

            Assert.InRange(h.VM.PreviewScrubTime, 0, Math.Max(0.01, duration));
            Assert.True(
                Math.Abs(h.VM.PreviewScrubTime - before) > 1e-6,
                $"click did not move the scrub position: before={before:F3} after={h.VM.PreviewScrubTime:F3} max={slider.Maximum:F3}");

            await h.VM.WaitForPreviewRefreshAsync();
            Assert.Equal(1, h.Factory.LastSession.FrameCalls);
            Assert.InRange(h.Factory.LastSession.LastFrameTime, 0, duration);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }
}