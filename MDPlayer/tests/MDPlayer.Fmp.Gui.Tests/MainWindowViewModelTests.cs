using Fmp.Application.Contracts;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Fmp.Gui.Tests;

/// <summary>
/// PR6 workflow tests driving a real MainWindowViewModel against a recording
/// fake preview session: open input, composition changes refresh preview, output
/// changes do not refresh, rapid visual changes coalesce, and the GUI exposes
/// every composition kind.
/// </summary>
public sealed class MainWindowViewModelTests
{
    [AvaloniaFact]
    public async Task OpeningInputCreatesRequestPlanAndPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            Assert.True(h.VM.HasInput);
            Assert.NotNull(h.VM.Request);
            Assert.True(h.PlanCalls >= 1);
            Assert.True(h.FrameCalls >= 1);
            Assert.NotNull(h.VM.Preview.CurrentImage);
            Assert.Equal(GuiState.Ready, h.VM.State);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingCompositionRefreshesPlanAndFrame()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Basic.SelectedComposition = h.Compositions[0];
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(h.Compositions[0].Value, h.LastRequest!.Composition);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingOutputPathDoesNotRefreshPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.SetOutputPath("/tmp/new-output.mp4");

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingOverwriteDoesNotRefreshPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Advanced.Overwrite = true;

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingEncoderDoesNotRefreshPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Advanced.SelectedEncoder = "Nvenc";

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RapidVisualChangesProduceOneRefresh()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Style.PastSeconds = 0.7m;
            h.VM.Settings.Style.PastSeconds = 0.8m;
            h.VM.Settings.Style.PastSeconds = 0.9m;

            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public void GuiExposesEveryComposition()
    {
        CompositionKind[] expected = Enum.GetValues<CompositionKind>();

        CompositionKind[] actual = SettingsViewModel.CreateCompositionOptions()
            .Select(option => option.Value)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private sealed class Harness
    {
        public MainWindowViewModel VM { get; private set; } = null!;
        public RecordingPreviewFactory Factory { get; private set; } = null!;
        public IReadOnlyList<CompositionOptionViewModel> Compositions { get; private set; } = null!;
        private string _inputPath = "";
        private string _settingsPath = "";

        public int PlanCalls => Factory.PlanCalls;
        public int FrameCalls => Factory.FrameCalls;
        public VisualizationRequest? LastRequest => Factory.LastRequest;

        public static Harness Create()
        {
            string inputPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-test-" + Guid.NewGuid() + ".vgz");
            File.WriteAllBytes(inputPath, new byte[] { 0x56, 0x67, 0x6d });
            string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");

            var factory = new RecordingPreviewFactory();
            var vm = new MainWindowViewModel(
                new GuiSettingsStore(settingsPath),
                new FileDialogService(),
                new ClipboardService(),
                new ExportProcessService(null),
                new NotificationService(),
                factory,
                initialInputPath: null);

            return new Harness
            {
                VM = vm,
                Factory = factory,
                Compositions = SettingsViewModel.CreateCompositionOptions(),
                _inputPath = inputPath,
                _settingsPath = settingsPath,
            };
        }

        public async Task OpenAsync()
        {
            await VM.OpenInputAsync(_inputPath);
            await VM.WaitForPreviewRefreshAsync();
        }

        public void ResetCalls() => Factory.ResetCalls();

        public async Task DisposeAsync()
        {
            await VM.ShutdownAsync();
            File.Delete(_inputPath);
            File.Delete(_settingsPath);
        }
    }
}