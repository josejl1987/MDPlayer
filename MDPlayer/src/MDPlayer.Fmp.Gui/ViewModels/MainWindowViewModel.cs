using System.Collections.ObjectModel;
using Avalonia.Threading;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Preview;
using Fmp.Application.Validation;
using Fmp.Gui.Services;

namespace Fmp.Gui.ViewModels;

/// <summary>Top-level application state.</summary>
public enum GuiState
{
    Empty,
    LoadingInput,
    Ready,
    Rendering,
}

/// <summary>An actionable error surfaced in the main window.</summary>
public sealed record GuiError(string Message, string? Details = null, string? LogPath = null);

/// <summary>
/// The orchestrator for the focused render workflow: open input → configure →
/// accurate still preview → render. Owns the immutable request snapshot, the
/// preview session, merged timeline state and export progress.
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private readonly GuiSettingsStore _settings;
    private readonly FileDialogService _dialogs;
    private readonly ClipboardService _clipboard;
    private readonly ExportProcessService _exportProcess;
    private readonly NotificationService _notifications;
    private readonly IVisualizationPreviewSessionFactory _previewFactory;
    private readonly string? _initialInputPath;
    private readonly CancellationTokenSource _lifeCts = new();
    private readonly DispatcherTimer _previewDebounce;

    private VisualizationRequest? _request;
    private VisualizationInputInfo? _input;
    private IVisualizationPreviewSession? _session;
    private VisualizationPlanResult? _plan;
    private int _requestRevision;
    private GuiState _state = GuiState.Empty;
    private GuiError? _error;
    private string? _completionMessage;
    private IReadOnlyList<ValidationIssue> _validationIssues = Array.Empty<ValidationIssue>();
    private bool _hasFatalValidationIssues;
    private double _previewTimeSeconds;
    private double _durationSeconds;

    private CancellationTokenSource _previewCts = new();
    private CancellationTokenSource _exportCts = new();
    private Task? _shutdownTask;

    public MainWindowViewModel(
        GuiSettingsStore settings,
        FileDialogService dialogs,
        ClipboardService clipboard,
        ExportProcessService exportProcess,
        NotificationService notifications,
        IVisualizationPreviewSessionFactory previewFactory,
        string? initialInputPath)
    {
        _settings = settings;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _exportProcess = exportProcess;
        _notifications = notifications;
        _previewFactory = previewFactory;
        _initialInputPath = initialInputPath;

        Preview = new PreviewViewModel();
        Export = new ExportProgressViewModel(clipboard);
        Settings = new SettingsViewModel(this);
        RefreshRecentFiles();

        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewDebounce.Tick += OnPreviewDebounceTick;

        WireCommands();
        WireEvents();
    }

    // ---- Child view models ----

    public SettingsViewModel Settings { get; }
    public PreviewViewModel Preview { get; }
    public ExportProgressViewModel Export { get; }

    // ---- Commands ----

    public AsyncRelayCommand OpenInputCommand { get; private set; } = null!;
    public RelayCommand RenderCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshPreviewCommand { get; private set; } = null!;
    public RelayCommand CancelCurrentCommand { get; private set; } = null!;
    public RelayCommand CancelExportCommand { get; private set; } = null!;
    public RelayCommand SeekRelativeCommand { get; private set; } = null!;
    public RelayCommand SeekStartCommand { get; private set; } = null!;
    public RelayCommand SeekEndCommand { get; private set; } = null!;
    public RelayCommand PreviousPointCommand { get; private set; } = null!;
    public RelayCommand NextPointCommand { get; private set; } = null!;
    public AsyncRelayCommand ChooseOutputPathCommand { get; private set; } = null!;
    public RelayCommand OpenOutputFolderCommand { get; private set; } = null!;
    public RelayCommand OpenOutputCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyErrorCommand { get; private set; } = null!;

    // ---- Observable state ----

    public string StatusText => _state switch
    {
        GuiState.Empty => "Open a music file to begin.",
        GuiState.LoadingInput => "Loading input…",
        GuiState.Ready => "Ready",
        GuiState.Rendering => "Rendering…",
        _ => "",
    };

    public bool HasInput => _request is not null;
    public bool IsBusy => _state is GuiState.LoadingInput or GuiState.Rendering;
    public bool CanEdit => HasInput && _state == GuiState.Ready;
    public bool CanRender => HasInput
        && _state == GuiState.Ready
        && !HasFatalValidationIssues
        && !HasOutputConflict;

    /// <summary>Current immutable request snapshot (null until an input is open).</summary>
    public VisualizationRequest? Request => _request;

    /// <summary>Current application state.</summary>
    public GuiState State => _state;

    public GuiError? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError), nameof(ErrorMessage));
        }
    }

    public bool HasError => Error is not null;
    public string ErrorMessage => Error?.Message ?? "";

    public string? CompletionMessage
    {
        get => _completionMessage;
        private set => SetProperty(ref _completionMessage, value);
    }

    public string InputTitle => _input?.Title ?? _input?.DisplayName ?? "No input open";

    public string InputSummary
    {
        get
        {
            if (_input is null)
                return "Open a music file to begin.";
            string duration = (_input.EstimatedDuration ?? _input.DeclaredDuration) is TimeSpan value
                ? FormatDuration(value)
                : "duration unknown";
            return $"{_input.Format} · {duration} · {_input.Tracks.Count} tracks";
        }
    }

    public string OutputPath => _request?.OutputPath ?? "";
    public bool HasOutputConflict => !string.IsNullOrWhiteSpace(OutputPath)
        && File.Exists(OutputPath)
        && !(_request?.Output.Overwrite ?? false);
    public bool CanOpenOutput => !string.IsNullOrWhiteSpace(OutputPath) && File.Exists(OutputPath);
    public string OutputStatusText => string.IsNullOrWhiteSpace(OutputPath)
        ? "Choose an output video before rendering."
        : HasOutputConflict
            ? "Output already exists. Enable overwrite to render here."
            : "Output path is ready.";

    public bool HasFatalValidationIssues
    {
        get => _hasFatalValidationIssues;
        private set => SetProperty(ref _hasFatalValidationIssues, value);
    }

    public ObservableCollection<RecentFileItemViewModel> RecentFiles { get; } = new();
    public bool HasRecentFiles => RecentFiles.Count > 0;

    public ObservableCollection<RepresentativePoint> RepresentativePoints { get; } = new();

    /// <summary>True once the opened input has produced a plan with a timeline.</summary>
    public bool HasTimeline => RepresentativePoints.Count > 0;

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (!SetProperty(ref _durationSeconds, value))
                return;
            OnPropertyChanged(nameof(TimeText));
            RefreshCommands();
        }
    }

    public double PreviewTimeSeconds
    {
        get => _previewTimeSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, DurationSeconds));
            if (SetProperty(ref _previewTimeSeconds, clamped))
            {
                OnPropertyChanged(nameof(TimeText));
                ScheduleSeekPreview();
            }
        }
    }

    public string TimeText => $"{FormatTime(_previewTimeSeconds)} / {FormatTime(DurationSeconds)}";

    // ---- Public API ----

    public async Task InitializeAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(_initialInputPath) && File.Exists(_initialInputPath))
                await OpenInputAsync(_initialInputPath);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during startup.
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    public async Task OpenInputAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path))
        {
            SetError($"Input file not found: {path}");
            return;
        }

        string fullPath = Path.GetFullPath(path);
        SetState(GuiState.LoadingInput);
        try
        {
            await DisposeSessionAsync();
            _session = await _previewFactory.OpenAsync(fullPath, _lifeCts.Token);
            _input = _session.Input;
            OnPropertyChanged(nameof(HasInput), nameof(InputTitle), nameof(InputSummary));
            AddRecentFile(fullPath);
            SetRequest(BuildInitialRequest(fullPath));
            SetState(GuiState.Ready);
            await RefreshPreviewAsync();
        }
        catch (OperationCanceledException)
        {
            SetState(GuiState.Ready);
        }
        catch (Exception ex)
        {
            SetError("Failed to open input: " + ex.Message);
        }
    }

    /// <summary>Applies a request delta and schedules a debounced preview refresh.</summary>
    public void ApplyVisualSetting(Func<VisualizationRequest, VisualizationRequest> transform)
    {
        if (SetNewRequest(transform))
            SchedulePreviewRefresh();
    }

    /// <summary>Applies an output-only request delta without refreshing the preview.</summary>
    public void ApplyExportSetting(Func<VisualizationRequest, VisualizationRequest> transform)
    {
        SetNewRequest(transform);
    }

    public async Task StartRenderAsync()
    {
        if (!CanRender)
            return;

        VisualizationRequest request = _request!;
        IReadOnlyList<ValidationIssue> issues = VisualizationRequestValidator.Validate(request);
        if (!VisualizationRequestValidator.IsValid(issues))
        {
            SetError("Fix the validation errors before rendering.");
            return;
        }

        SetState(GuiState.Rendering);
        Export.Reset();
        _exportCts = new CancellationTokenSource();
        CompletionMessage = null;

        try
        {
            var progress = new Progress<ExportProgressEvent>(Export.OnEvent);
            string workspace = await _exportProcess.StartAsync(request, progress, _exportCts.Token);
            Export.SetWorkspace(workspace);
            SetState(GuiState.Ready);
            if (Export.HasFailed)
                SetError("Render failed.");
            else
                CompletionMessage = "Render completed.";
        }
        catch (OperationCanceledException)
        {
            Export.SetCancelled();
            SetState(GuiState.Ready);
        }
        catch (Exception ex)
        {
            Export.SetFailed(null, "Render failed: " + ex.Message);
            SetError("Render failed: " + ex.Message);
            SetState(GuiState.Ready);
        }
    }

    public void CancelCurrent()
    {
        CancelPreview();
        _exportCts.Cancel();
    }

    public void CancelPreview() => _previewCts.Cancel();

    public void RestoreDetectedMetadata()
    {
        if (_session is null)
            return;
        ApplyVisualSetting(r => r with
        {
            Presentation = r.Presentation with
            {
                Title = _session.Input.Title ?? Path.GetFileNameWithoutExtension(_session.Input.FullPath),
                Subtitle = _session.Input.Game ?? _session.Input.System,
                Credits = _session.Input.Composer,
            },
        });
    }

    public async Task<string?> ChooseFontFileAsync() => await _dialogs.ChooseFontFileAsync();

    public async Task ChooseOutputPathAsync()
    {
        if (!CanEdit)
            return;
        string? path = await _dialogs.SaveOutputFileAsync(OutputPath);
        if (!string.IsNullOrWhiteSpace(path))
            ApplyExportSetting(r => r with { OutputPath = path });
    }

    /// <summary>
    /// Sets the output path directly (used by tests and future automation);
    /// does not open a save dialog.
    /// </summary>
    public void SetOutputPath(string path)
    {
        if (!HasInput)
            return;
        ApplyExportSetting(r => r with { OutputPath = path });
    }

    public void OpenOutputFolder()
    {
        string? directory = Path.GetDirectoryName(OutputPath);
        if (string.IsNullOrWhiteSpace(directory))
            return;
        DesktopProcessService.OpenPath(directory);
    }

    public void OpenOutput()
    {
        if (CanOpenOutput)
            DesktopProcessService.OpenPath(OutputPath);
    }

    public async Task CopyErrorAsync()
    {
        if (Error is not { } error)
            return;
        string text = error.Message
            + (string.IsNullOrWhiteSpace(error.Details) ? "" : "\n" + error.Details)
            + (string.IsNullOrWhiteSpace(error.LogPath) ? "" : "\nLog: " + error.LogPath);
        await _clipboard.SetTextAsync(text);
    }

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();
    public void Shutdown() => _ = ShutdownAsync();

    // ---- Preview refresh ----

    /// <summary>Waits for any pending debounced refresh to complete.</summary>
    public async Task WaitForPreviewRefreshAsync()
    {
        _previewDebounce.Stop();
        await RefreshPreviewAsync();
    }

    private void SchedulePreviewRefresh()
    {
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void ScheduleSeekPreview()
    {
        // Updating the displayed time immediately; rendering coalesces here.
        SchedulePreviewRefresh();
    }

    private async void OnPreviewDebounceTick(object? sender, EventArgs e)
    {
        _previewDebounce.Stop();
        await RefreshPreviewAsync();
    }

    public async Task RefreshPreviewAsync()
    {
        if (_request is null || _session is null)
            return;

        int revision = _requestRevision;
        VisualizationRequest request = _request;

        CancelPreview();
        _previewCts = new CancellationTokenSource();
        CancellationToken ct = _previewCts.Token;

        Preview.SetLoading(true);
        try
        {
            _plan = await _session.PlanAsync(request, ct);
            if (IsObsolete(revision, ct))
                return;
            ApplyPlan(_plan);

            var frame = await _session.RenderFrameAsync(
                request,
                new PreviewFrameRequest
                {
                    TimeSeconds = PreviewTimeSeconds,
                    Width = _settings.Settings.PreviewMaxWidth,
                    Height = _settings.Settings.PreviewMaxHeight,
                    Fidelity = PreviewFidelity.AccurateStill,
                },
                ct);
            if (IsObsolete(revision, ct))
                return;
            Preview.ApplyFrame(frame);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer request.
        }
        catch (Exception ex)
        {
            Preview.SetError(ex.Message);
        }
        finally
        {
            if (revision == _requestRevision)
                Preview.SetLoading(false);
        }
    }

    private bool IsObsolete(int revision, CancellationToken ct)
        => ct.IsCancellationRequested || revision != _requestRevision;

    private void ApplyPlan(VisualizationPlanResult plan)
    {
        Settings.SynchronizePlan(plan, _input);
        if (plan.EstimatedDurationSeconds is double duration)
            DurationSeconds = duration;
        else
            DurationSeconds = 0;
        RepresentativePoints.Clear();
        foreach (RepresentativePoint point in plan.RepresentativePoints)
            RepresentativePoints.Add(point);
        RefreshCommands();
    }

    // ---- Timeline / seek ----

    private void SeekRelative(object? parameter)
    {
        int delta = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out int n) => n,
            _ => 0,
        };
        PreviewTimeSeconds += delta;
    }

    private void GoToPoint(RepresentativePoint point)
    {
        if (point is not null)
            PreviewTimeSeconds = point.TimeSeconds;
    }

    private void GoPreviousPoint()
    {
        RepresentativePoint? point = RepresentativePoints
            .Where(p => p.TimeSeconds < PreviewTimeSeconds - 0.01)
            .OrderByDescending(p => p.TimeSeconds)
            .FirstOrDefault();
        if (point is not null)
            PreviewTimeSeconds = point.TimeSeconds;
    }

    private void GoNextPoint()
    {
        RepresentativePoint? point = RepresentativePoints
            .Where(p => p.TimeSeconds > PreviewTimeSeconds + 0.01)
            .OrderBy(p => p.TimeSeconds)
            .FirstOrDefault();
        if (point is not null)
            PreviewTimeSeconds = point.TimeSeconds;
    }

    // ---- Setup ----

    private void WireCommands()
    {
        OpenInputCommand = new AsyncRelayCommand(OpenInputDialogAsync);
        RenderCommand = new RelayCommand(() => _ = StartRenderAsync(), () => CanRender);
        RefreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewAsync, () => CanEdit);
        CancelCurrentCommand = new RelayCommand(CancelCurrent);
        CancelExportCommand = new RelayCommand(() => _exportCts.Cancel());
        SeekRelativeCommand = new RelayCommand(SeekRelative, _ => HasInput);
        SeekStartCommand = new RelayCommand(() => PreviewTimeSeconds = 0, () => HasInput);
        SeekEndCommand = new RelayCommand(() => PreviewTimeSeconds = DurationSeconds, () => HasInput && DurationSeconds > 0);
        PreviousPointCommand = new RelayCommand(GoPreviousPoint, () => HasInput && RepresentativePoints.Count > 0);
        NextPointCommand = new RelayCommand(GoNextPoint, () => HasInput && RepresentativePoints.Count > 0);
        ChooseOutputPathCommand = new AsyncRelayCommand(ChooseOutputPathAsync, () => CanEdit);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, () => HasInput);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => CanOpenOutput);
        CopyErrorCommand = new AsyncRelayCommand(CopyErrorAsync, () => HasError);
    }

    private void WireEvents()
    {
        Preview.RetryRequested += () => _ = (RefreshPreviewAsync());
        Export.RetryRequested += () => _ = StartRenderAsync();
    }

    private async Task OpenInputDialogAsync()
    {
        try
        {
            string? path = await _dialogs.OpenFileAsync();
            if (path is not null)
                await OpenInputAsync(path);
        }
        catch (Exception ex)
        {
            SetError(ex.Message);
        }
    }

    // ---- Request snapshot handling ----

    private bool SetNewRequest(Func<VisualizationRequest, VisualizationRequest> transform)
    {
        if (_request is null)
            return false;

        VisualizationRequest next = transform(_request);
        if (next == _request)
            return false;

        _request = next;
        _requestRevision++;
        Settings.Synchronize(next);
        ValidateCurrent();
        OnPropertyChanged(nameof(HasInput), nameof(OutputPath), nameof(HasOutputConflict),
            nameof(OutputStatusText), nameof(CanOpenOutput));
        RefreshCommands();
        return true;
    }

    private void SetRequest(VisualizationRequest request)
    {
        _request = request;
        _requestRevision++;
        Settings.Synchronize(request);
        ValidateCurrent();
        OnPropertyChanged(nameof(HasInput), nameof(OutputPath), nameof(HasOutputConflict),
            nameof(OutputStatusText), nameof(CanOpenOutput));
        RefreshCommands();
    }

    private void ValidateCurrent()
    {
        _validationIssues = _request is null
            ? Array.Empty<ValidationIssue>()
            : VisualizationRequestValidator.Validate(_request);
        HasFatalValidationIssues = !VisualizationRequestValidator.IsValid(_validationIssues);
    }

    private void SetState(GuiState state)
    {
        _state = state;
        OnPropertyChanged(nameof(StatusText), nameof(IsBusy), nameof(CanEdit), nameof(CanRender));
        RefreshCommands();
    }

    private void SetError(string message, string? details = null, string? logPath = null)
    {
        Error = new GuiError(message, details, logPath);
        OnPropertyChanged(nameof(StatusText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit), nameof(CanRender), nameof(IsBusy), nameof(HasInput),
            nameof(OutputStatusText), nameof(HasOutputConflict), nameof(CanOpenOutput));
        RenderCommand.RaiseCanExecuteChanged();
        RefreshPreviewCommand.RaiseCanExecuteChanged();
        ChooseOutputPathCommand.RaiseCanExecuteChanged();
        OpenOutputFolderCommand.RaiseCanExecuteChanged();
        OpenOutputCommand.RaiseCanExecuteChanged();
        CopyErrorCommand.RaiseCanExecuteChanged();
        SeekRelativeCommand.RaiseCanExecuteChanged();
        SeekStartCommand.RaiseCanExecuteChanged();
        SeekEndCommand.RaiseCanExecuteChanged();
        PreviousPointCommand.RaiseCanExecuteChanged();
        NextPointCommand.RaiseCanExecuteChanged();
    }

    private async Task DisposeSessionAsync()
    {
        CancelPreview();
        Preview.Clear();
        if (_session is not null)
        {
            IVisualizationPreviewSession session = _session;
            _session = null;
            try
            {
                await session.DisposeAsync();
            }
            catch
            {
                // Best effort.
            }
        }
        _input = null;
        _plan = null;
        DurationSeconds = 0;
        RepresentativePoints.Clear();
        OnPropertyChanged(nameof(HasInput), nameof(InputTitle), nameof(InputSummary));
    }

    private async Task ShutdownCoreAsync()
    {
        _lifeCts.Cancel();
        _previewDebounce.Stop();
        _previewCts.Cancel();
        _exportCts.Cancel();
        await DisposeSessionAsync();
        Preview.Dispose();
    }

    private static VisualizationRequest BuildInitialRequest(string inputPath)
    {
        string? directory = Path.GetDirectoryName(inputPath);
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string outputDir = Path.Combine(directory ?? ".", name + ".visualization");
        return new VisualizationRequest
        {
            InputPath = inputPath,
            OutputPath = Path.Combine(outputDir, "visualization.mp4"),
        };
    }

    private void AddRecentFile(string path)
    {
        _settings.Update(settings =>
        {
            settings.RecentFiles.Remove(path);
            settings.RecentFiles.Insert(0, path);
            if (settings.RecentFiles.Count > 10)
                settings.RecentFiles.RemoveAt(10);
        });
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (string path in _settings.Settings.RecentFiles.Take(10))
            RecentFiles.Add(new RecentFileItemViewModel(path, OpenInputAsync));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private static string FormatTime(double seconds)
    {
        int total = (int)Math.Max(0, seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        int totalSeconds = Math.Max(0, (int)duration.TotalSeconds);
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }
}