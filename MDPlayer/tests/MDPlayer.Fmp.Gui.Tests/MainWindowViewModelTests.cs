using Fmp.Application.Contracts;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Avalonia;
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
    public async Task SelectingSameComposition_DoesNotSchedulePreviewRefresh()
    {
        // The GUI currently exposes exactly one composition (Diagnostic) and
        // CompositionOptionViewModel is a value record, so assigning the
        // already-selected composition is a no-op and must NOT schedule a
        // preview refresh. (When a second composition kind exists this becomes
        // a change test again; today it pins the no-op contract. The previous
        // version "passed" only because WaitForPreviewRefreshAsync refreshes
        // unconditionally, masking the no-op.)
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Basic.SelectedComposition = h.Compositions[0];

            // No debounce tick fires for a no-op assignment.
            await Task.Delay(400);
            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);

            // An explicit refresh still renders the (single) composition.
            await h.VM.RefreshPreviewManuallyAsync();
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
    public async Task CommittedSeekRendersFrameWithoutPlanning()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.PreviewScrubTime = 12;
            h.VM.CommitScrub();
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(12, h.Factory.LastSession.LastFrameTime);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RapidSeeksCoalesceToOneFrame()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.PreviewScrubTime = 10;
            h.VM.CommitScrub();
            h.VM.PreviewScrubTime = 11;
            h.VM.CommitScrub();
            h.VM.PreviewScrubTime = 12;
            h.VM.CommitScrub();

            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(12, h.Factory.LastSession.LastFrameTime);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task SeekThenVisualChange_ProducesOnePlanAndFrame()
    {
        // A seek followed by a visual change must coalesce into a single
        // plan+frame refresh: the pending seek must never downgrade the
        // stronger plan refresh.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.PreviewScrubTime = 5;
            h.VM.CommitScrub();
            h.VM.Settings.Style.PastSeconds = 0.5m;

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
    public async Task VisualChangeThenSeek_ProducesOnePlanAndFrame()
    {
        // A visual change followed by a seek must coalesce into a single
        // plan+frame refresh for the newer time.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Style.PastSeconds = 0.5m;
            h.VM.PreviewScrubTime = 5;
            h.VM.CommitScrub();

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
    public async Task WaitingWithoutPendingWorkIsNoOp()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task StaleFrameIsNotApplied()
    {
        // A superseded refresh must never overwrite the newest frame. Frame A is
        // held in flight (gated) while a newer refresh B starts; releasing A
        // first must not change CurrentImage, and only B's frame is applied.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            // No ResetCalls here: the per-frame PNG identity is derived from the
            // session's monotonic frame-call index, so opening=0, refresh A=1,
            // refresh B=2.
            h.Factory.LastSession.GateFrames = true;

            Task refreshA = h.VM.RefreshPreviewManuallyAsync();
            Assert.Single(h.Factory.LastSession.FrameGates);

            Task refreshB = h.VM.RefreshPreviewManuallyAsync();
            Assert.Equal(2, h.Factory.LastSession.FrameGates.Count);

            // Release A's gate first; A is superseded so its 2x2 frame must not
            // replace the 1x1 frame displayed from opening.
            h.Factory.LastSession.FrameGates[0].SetResult();
            await refreshA;
            Assert.Equal(new PixelSize(1, 1), h.VM.Preview.CurrentImage!.PixelSize);

            // Release B's gate; only B's 3x1 frame becomes CurrentImage.
            h.Factory.LastSession.FrameGates[1].SetResult();
            await refreshB;
            Assert.Equal(new PixelSize(3, 1), h.VM.Preview.CurrentImage!.PixelSize);
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

    [AvaloniaFact]
    public async Task FailedOpen_ShowsErrorAndReturnsToUsableState()
    {
        // No prior input: a failed open must not leave the window stuck in LoadingInput.
        Harness h = Harness.Create();
        try
        {
            h.Factory.FailNextOpen = new InvalidOperationException("boom");
            await h.VM.OpenInputAsync(h.InputPath);

            Assert.Equal(GuiState.Empty, h.VM.State);
            Assert.NotNull(h.VM.Error);
        }
        finally
        {
            await h.DisposeAsync();
        }

        // With a previously opened input, a failed re-open must be
        // transactional: the old request stays active with its session intact.
        // (The previous version never created the "second file", so
        // OpenInputAsync exited at the File.Exists check and never exercised
        // FailNextOpen at all.)
        Harness h2 = Harness.Create();
        await h2.OpenAsync();
        try
        {
            string secondPath = Path.Combine(Path.GetTempPath(), "other-" + Guid.NewGuid() + ".vgz");
            await File.WriteAllTextAsync(secondPath, "not really vgz");
            try
            {
                h2.Factory.FailNextOpen = new InvalidOperationException("boom");
                await h2.VM.OpenInputAsync(secondPath);

                Assert.Equal(GuiState.Ready, h2.VM.State);
                Assert.NotNull(h2.VM.Error);
                // The previous request remains fully active...
                Assert.NotNull(h2.VM.Request);
                Assert.Equal(Path.GetFullPath(h2.InputPath), h2.VM.Request!.InputPath);

                // ...and its session is still usable: a preview refresh still works.
                h2.ResetCalls();
                await h2.VM.RefreshPreviewManuallyAsync();
                Assert.Equal(1, h2.PlanCalls);
            }
            finally
            {
                File.Delete(secondPath);
            }
        }
        finally
        {
            await h2.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ExportSettingChangeDuringInFlightPreview_ClearsLoading()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            // Gate the next refresh so it stays in flight while an export-only
            // setting changes.
            h.Factory.LastSession.PlanGate = new TaskCompletionSource();
            Task refreshTask = h.VM.RefreshPreviewManuallyAsync();

            // PlanAsync runs synchronously up to the gate, so the refresh is in flight now.
            Assert.True(h.PlanCalls >= 1);

            h.VM.SetOutputPath("/tmp/in-flight-output.mp4");

            h.Factory.LastSession.PlanGate.SetResult();
            await refreshTask;

            Assert.False(h.VM.Preview.IsLoading, "an export-only change must not leave the preview spinner stuck");
            Assert.Equal("/tmp/in-flight-output.mp4", h.VM.Request!.OutputPath);
        }
        finally
        {
            await h.DisposeAsync();
        }
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
        public string InputPath => _inputPath;

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