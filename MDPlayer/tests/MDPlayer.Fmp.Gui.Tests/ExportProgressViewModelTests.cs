using Fmp.Application.Contracts;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Xunit;

namespace Fmp.Gui.Tests;

public class ExportProgressViewModelTests
{
    private static ExportProgressViewModel Create()
        => new(new ClipboardService(), new DiagnosticsViewModel(new ClipboardService()));

    [Fact]
    public void Started_SetsRunning()
    {
        ExportProgressViewModel viewModel = Create();
        viewModel.OnEvent(Started());
        Assert.True(viewModel.IsRunning);
        Assert.True(viewModel.IsActive);
    }

    [Fact]
    public void StageStarted_SetsStageAndIndeterminate()
    {
        ExportProgressViewModel viewModel = Create();
        viewModel.OnEvent(Started());
        viewModel.OnEvent(new ExportProgressEvent
        {
            Type = ExportEventTypes.StageStarted,
            Stage = "capturingSemanticTimeline",
        });

        Assert.Equal("capturingSemanticTimeline", viewModel.CurrentStage);
        Assert.True(viewModel.IsIndeterminate);
    }

    [Fact]
    public void EncoderAndFrames_UpdateMetrics()
    {
        ExportProgressViewModel viewModel = Create();
        viewModel.OnEvent(Started());
        viewModel.OnEvent(new ExportProgressEvent
        {
            Type = ExportEventTypes.EncoderSelected,
            Encoder = "libx264",
        });
        viewModel.OnEvent(new ExportProgressEvent
        {
            Type = ExportEventTypes.StageProgress,
            Stage = "composingFrames",
            Progress = 0.5,
            FramesCompleted = 120,
            ElapsedSeconds = 4.0,
        });

        Assert.Equal("libx264", viewModel.Encoder);
        Assert.Equal(120, viewModel.FramesCompleted);
        Assert.False(viewModel.IsIndeterminate);
        Assert.Equal(30.0, viewModel.EffectiveFps, precision: 1);
    }

    [Fact]
    public void Completed_SetsSummary()
    {
        ExportProgressViewModel viewModel = Create();
        viewModel.OnEvent(Started());
        viewModel.OnEvent(new ExportProgressEvent
        {
            Type = ExportEventTypes.Completed,
            OutputPath = "/tmp/out.mp4",
            ElapsedSeconds = 61.0,
        });

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.HasCompleted);
        Assert.False(viewModel.HasFailed);
        Assert.Contains("/tmp/out.mp4", viewModel.CompletionSummary);
        Assert.Contains("1m 1s", viewModel.CompletionSummary);
    }

    [Fact]
    public void Failed_SetsFailureSummaryWithExitCode()
    {
        ExportProgressViewModel viewModel = Create();
        viewModel.OnEvent(Started());
        viewModel.OnEvent(new ExportProgressEvent
        {
            Type = ExportEventTypes.Failed,
            Stage = "composingFrames",
            Message = "ffmpeg not found",
            ExitCode = 4,
        });

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.HasFailed);
        Assert.Contains("ffmpeg not found", viewModel.FailureSummary);
        Assert.Contains("4", viewModel.FailureSummary);
        Assert.Equal(4, viewModel.ExitCode);
    }

    [Fact]
    public void Warning_IsLoggedInDiagnostics()
    {
        ExportProgressViewModel viewModel = Create();
        viewModel.OnEvent(new ExportProgressEvent
        {
            Type = ExportEventTypes.Warning,
            Message = "encoder fallback",
        });

        Assert.Contains(
            viewModel.Diagnostics.Log,
            line => line.Contains("encoder fallback", StringComparison.Ordinal));
    }

    private static ExportProgressEvent Started() => new()
    {
        Type = ExportEventTypes.Started,
    };
}
