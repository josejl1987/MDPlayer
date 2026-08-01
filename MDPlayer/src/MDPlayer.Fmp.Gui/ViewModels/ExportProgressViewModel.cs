using System.Collections.ObjectModel;
using Fmp.Application.Contracts;
using Fmp.Gui.Services;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Export run progress: current stage, stage progress, elapsed time, frames,
/// encoder, output path, log and completion/failure summaries.
/// </summary>
public sealed class ExportProgressViewModel : ObservableObject
{
    private readonly DiagnosticsViewModel _diagnostics;
    private string _currentStage = "";
    private double _stageProgress;
    private bool _isIndeterminate = true;
    private string _overallMessage = "";
    private double _elapsedSeconds;
    private long _framesCompleted;
    private double _effectiveFps;
    private string _encoder = "";
    private string _outputPath = "";
    private bool _isRunning;
    private bool _hasCompleted;
    private string _completionSummary = "";
    private bool _hasFailed;
    private string _failureSummary = "";
    private int? _exitCode;
    private string _logPath = "";
    private string _workspace = "";

    public ExportProgressViewModel(ClipboardService clipboard, DiagnosticsViewModel diagnostics)
    {
        _diagnostics = diagnostics;
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke());
        OpenTerminalCommand = new RelayCommand(OpenTerminal);
    }

    /// <summary>Raised when the failure summary's Retry button is clicked.</summary>
    public event Action? RetryRequested;

    public RelayCommand RetryCommand { get; }
    public RelayCommand OpenTerminalCommand { get; }

    /// <summary>Shared log lines are also surfaced in the diagnostics panel.</summary>
    public DiagnosticsViewModel Diagnostics => _diagnostics;

    /// <summary>True while an export run is active or its summary is showing.</summary>
    public bool IsActive => IsRunning || HasCompleted || HasFailed;

    /// <summary>Stage + overall message for the export strip.</summary>
    public string StageAndProgressText =>
        string.IsNullOrEmpty(CurrentStage) ? OverallMessage : $"{CurrentStage} — {OverallMessage}";

    /// <summary>Elapsed / frames / fps metrics for the export strip.</summary>
    public string MetricsText => $"{ElapsedSeconds:0.0}s · {FramesCompleted:N0} frames · {EffectiveFps:0.0} fps";

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetProperty(ref _currentStage, value);
    }

    public double StageProgress
    {
        get => _stageProgress;
        private set => SetProperty(ref _stageProgress, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public string OverallMessage
    {
        get => _overallMessage;
        private set => SetProperty(ref _overallMessage, value);
    }

    public double ElapsedSeconds
    {
        get => _elapsedSeconds;
        private set => SetProperty(ref _elapsedSeconds, value);
    }

    public long FramesCompleted
    {
        get => _framesCompleted;
        private set => SetProperty(ref _framesCompleted, value);
    }

    public double EffectiveFps
    {
        get => _effectiveFps;
        private set => SetProperty(ref _effectiveFps, value);
    }

    public string Encoder
    {
        get => _encoder;
        private set => SetProperty(ref _encoder, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        private set => SetProperty(ref _outputPath, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public bool HasCompleted
    {
        get => _hasCompleted;
        private set => SetProperty(ref _hasCompleted, value);
    }

    public string CompletionSummary
    {
        get => _completionSummary;
        private set => SetProperty(ref _completionSummary, value);
    }

    public bool HasFailed
    {
        get => _hasFailed;
        private set => SetProperty(ref _hasFailed, value);
    }

    public string FailureSummary
    {
        get => _failureSummary;
        private set => SetProperty(ref _failureSummary, value);
    }

    public int? ExitCode
    {
        get => _exitCode;
        private set => SetProperty(ref _exitCode, value);
    }

    public string LogPath
    {
        get => _logPath;
        private set => SetProperty(ref _logPath, value);
    }

    /// <summary>Temp workspace retained for diagnostics (logs, frames).</summary>
    public string Workspace
    {
        get => _workspace;
        private set => SetProperty(ref _workspace, value);
    }

    public void Reset()
    {
        CurrentStage = "";
        StageProgress = 0;
        IsIndeterminate = true;
        OverallMessage = "";
        ElapsedSeconds = 0;
        FramesCompleted = 0;
        EffectiveFps = 0;
        Encoder = "";
        OutputPath = "";
        ExitCode = null;
        LogPath = "";
        Workspace = "";
        IsRunning = false;
        HasCompleted = false;
        HasFailed = false;
        CompletionSummary = "";
        FailureSummary = "";
        _diagnostics.Clear();
        OnPropertyChanged(nameof(IsActive), nameof(StageAndProgressText), nameof(MetricsText));
    }

    public void SetWorkspace(string workspace)
    {
        Workspace = workspace;
        LogPath = Path.Combine(workspace, "export.log");
    }

    public void OnEvent(ExportProgressEvent evt)
    {
        if (evt.Stage is { Length: > 0 } stage)
            CurrentStage = stage;
        if (evt.Progress is double p)
        {
            StageProgress = p;
            IsIndeterminate = false;
        }
        if (evt.Message is { Length: > 0 } message)
        {
            OverallMessage = message;
            _diagnostics.AddLine($"[export] {message}");
        }
        if (evt.ElapsedSeconds is double elapsed)
            ElapsedSeconds = elapsed;
        if (evt.FramesCompleted is long frames)
        {
            FramesCompleted = frames;
            EffectiveFps = ElapsedSeconds > 0.5 ? frames / ElapsedSeconds : 0;
        }
        if (evt.Encoder is { Length: > 0 } encoder)
            Encoder = encoder;
        if (evt.OutputPath is { Length: > 0 } outputPath)
            OutputPath = outputPath;

        switch (evt.Type)
        {
            case ExportEventTypes.Started:
                IsRunning = true;
                break;
            case ExportEventTypes.StageStarted:
                IsIndeterminate = true;
                break;
            case ExportEventTypes.StageProgress:
                IsIndeterminate = false;
                break;
            case ExportEventTypes.Completed:
                SetCompleted(evt);
                break;
            case ExportEventTypes.Failed:
                SetFailed(evt, null);
                break;
            case ExportEventTypes.Cancelled:
                SetFailed(evt, "cancelled");
                break;
            case ExportEventTypes.Warning:
                _diagnostics.AddLine("[warning] " + (evt.Message ?? evt.Code ?? ""));
                break;
        }

        OnPropertyChanged(nameof(IsActive), nameof(StageAndProgressText), nameof(MetricsText));
    }

    public void SetCancelled()
    {
        IsRunning = false;
        HasFailed = true;
        FailureSummary = "Export was cancelled.";
        _diagnostics.AddLine("Export cancelled.");
        OnPropertyChanged(nameof(IsActive));
    }

    private void SetCompleted(ExportProgressEvent evt)
    {
        IsRunning = false;
        HasCompleted = true;
        HasFailed = false;

        string outputPath = OutputPath;
        string fileSize = "";
        string resolution = "";
        if (outputPath.Length > 0 && File.Exists(outputPath))
        {
            var file = new FileInfo(outputPath);
            fileSize = file.Length > 0 ? $"{file.Length / 1024.0 / 1024.0:0.0} MB" : "?";
        }

        CompletionSummary =
            $"Output: {outputPath}\n" +
            $"File size: {fileSize}\n" +
            $"Encoder: {Encoder}\n" +
            $"Total time: {FormatTime(ElapsedSeconds)}";
        _diagnostics.AddLine("Export completed.");
        OnPropertyChanged(nameof(IsActive));
    }

    internal void SetFailed(ExportProgressEvent? evt, string? overrideMessage)
    {
        IsRunning = false;
        HasFailed = true;
        HasCompleted = false;

        int? code = evt?.ExitCode ?? ExitCode;
        ExitCode = code;
        FailureSummary = string.IsNullOrWhiteSpace(overrideMessage)
            ? $"Stage '{CurrentStage}' failed: {evt?.Message ?? "unknown error"}"
            : overrideMessage;
        if (code is int c)
            FailureSummary += $"\nExit code: {c}";
        if (LogPath.Length > 0)
            FailureSummary += $"\nLog: {LogPath}";

        _diagnostics.AddLine("Export failed: " + FailureSummary.Replace('\n', ' '));
        OnPropertyChanged(nameof(IsActive));
    }

    private void OpenTerminal()
    {
        if (Workspace.Length > 0)
            DesktopProcessService.OpenTerminalAtDirectory(Workspace);
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m {t.Seconds}s" : $"{t.Minutes}m {t.Seconds}s";
    }
}
