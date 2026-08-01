using Avalonia.Threading;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Preview;
using Fmp.Application.Projects;
using Fmp.Application.Validation;
using Fmp.Gui.Services;

namespace Fmp.Gui.ViewModels;

/// <summary>Top-level application state machine.</summary>
public enum GuiState
{
    Empty,
    Inspecting,
    PreparingPreview,
    Ready,
    Previewing,
    ExportPreflight,
    Exporting,
    Completed,
    Error,
}

/// <summary>
/// The orchestrator: owns the immutable request snapshot, the preview session,
/// the plan and all child view models. Every long-running operation takes a
/// CancellationToken and is awaited; the UI thread is never blocked.
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
    private readonly VisualizationCommandFormatter _formatter = new();
    private readonly CancellationTokenSource _lifeCts = new();

    private VisualizationRequest? _request;
    private IVisualizationPreviewSession? _session;
    private VisualizationPlanResult? _plan;
    private int _requestRevision;
    private string? _projectPath;
    private bool _isDirty;
    private GuiState _state = GuiState.Empty;
    private string? _errorMessage;
    private IReadOnlyList<ValidationIssue> _validationIssues = Array.Empty<ValidationIssue>();
    private bool _hasFatalValidationIssues;
    private bool _suppressSettingsSync;
    private bool _suppressPreviewRefresh;

    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _previewDebounce;
    private InvalidationCategory _pendingCategory = InvalidationCategory.ExportOnly;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private CancellationTokenSource _previewCts = new();
    private CancellationTokenSource _exportCts = new();

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
        Timeline = new TimelineViewModel();
        Command = new CommandViewModel(OnCommandModeChanged);
        Tools = new ToolStatusViewModel(() => _request, () => _plan, settings.Settings.RenderCliPath);
        Diagnostics = new DiagnosticsViewModel(clipboard);
        Export = new ExportProgressViewModel(clipboard, Diagnostics);
        Settings = new SettingsViewModel(this);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autosaveTimer.Tick += (_, _) => Autosave();
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewDebounce.Tick += OnPreviewDebounceTick;

        WireCommands();
        WireEvents();
    }

    // ---- Child view models ----

    public SettingsViewModel Settings { get; }
    public PreviewViewModel Preview { get; }
    public TimelineViewModel Timeline { get; }
    public CommandViewModel Command { get; }
    public ToolStatusViewModel Tools { get; }
    public ExportProgressViewModel Export { get; }
    public DiagnosticsViewModel Diagnostics { get; }

    // ---- Commands ----

    public AsyncRelayCommand OpenInputCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenProjectCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveProjectCommand { get; private set; } = null!;
    public AsyncRelayCommand SaveProjectAsCommand { get; private set; } = null!;
    public RelayCommand RenderCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyCommandCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyRequestJsonCommand { get; private set; } = null!;
    public AsyncRelayCommand RecheckToolsCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshPreviewCommand { get; private set; } = null!;
    public RelayCommand CancelExportCommand { get; private set; } = null!;
    public RelayCommand RenderStillCommand { get; private set; } = null!;
    public RelayCommand CancelPreviewCommand { get; private set; } = null!;
    public RelayCommand FocusCommandPanelCommand { get; private set; } = null!;

    // ---- Window-driven hooks ----

    /// <summary>The window shows a recovery prompt; true = restore.</summary>
    public Func<VisualizationProject, Task<bool>>? RecoveryPrompt { get; set; }

    /// <summary>The window confirms discarding unsaved changes; true = discard.</summary>
    public Func<Task<bool>>? DiscardConfirm { get; set; }

    /// <summary>Raised so the window can focus the command panel (Ctrl+K).</summary>
    public event Action? CommandPanelFocusRequested;

    // ---- Observable state ----

    public string StatusText => _state switch
    {
        GuiState.Empty => "Open an input file to begin.",
        GuiState.Inspecting => "Inspecting input…",
        GuiState.PreparingPreview => "Preparing preview…",
        GuiState.Ready => "Ready",
        GuiState.Previewing => "Previewing…",
        GuiState.ExportPreflight => "Preparing export…",
        GuiState.Exporting => "Exporting…",
        GuiState.Completed => "Export completed.",
        GuiState.Error => _errorMessage ?? "Error",
        _ => "",
    };

    public string ProjectTitle
    {
        get
        {
            string name = string.IsNullOrEmpty(_projectPath)
                ? "untitled"
                : Path.GetFileNameWithoutExtension(_projectPath);
            return name + (IsDirty ? " •" : "");
        }
    }

    public bool IsDirty => _isDirty;

    public bool HasFatalValidationIssues
    {
        get => _hasFatalValidationIssues;
        private set => SetProperty(ref _hasFatalValidationIssues, value);
    }

    public bool IsReducedMotion => _settings.Settings.ReducedMotion;

    public string ReducedMotionHint => "Reduced motion is enabled: automatic preview updates are paused. Press F5 to refresh the preview manually.";

    private static string RecoveryDirectory => Path.Combine(Path.GetTempPath(), "MDPlayer", "Visualizer", "recovery");

    // ---- Public API ----

    public async Task InitializeAsync()
    {
        try
        {
            if (_settings.Settings.ReducedMotion)
                Preview.SetReducedMotion(true);

            VisualizationProject? recovery = VisualizationProjectStore.TryLoadRecovery(RecoveryDirectory);
            if (recovery is not null)
            {
                bool restore = await (RecoveryPrompt?.Invoke(recovery) ?? Task.FromResult(false));
                if (restore)
                {
                    await OpenRequestAsync(recovery.Request, recovery.LastPreviewTimeSeconds, projectPath: null);
                    _isDirty = true;
                    OnPropertyChanged(nameof(IsDirty), nameof(ProjectTitle));
                    return;
                }
                VisualizationProjectStore.ClearRecovery(RecoveryDirectory);
            }

            if (!string.IsNullOrEmpty(_initialInputPath) && File.Exists(_initialInputPath))
                await OpenInputAsync(_initialInputPath);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during startup.
        }
        catch (Exception ex)
        {
            SetErrorState(ex.Message);
        }
    }

    /// <summary>Opens an input file: inspect → session → plan → layout preview.</summary>
    public async Task OpenInputAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path))
        {
            SetErrorState($"Input file not found: {path}");
            return;
        }

        string fullPath = Path.GetFullPath(path);
        SetState(GuiState.Inspecting);
        try
        {
            await DisposeSessionAsync();
            _session = await _previewFactory.OpenAsync(fullPath, _lifeCts.Token);
            AddRecentFile(fullPath);
            SetRequest(BuildInitialRequest(fullPath), markDirty: true);
            await UpdatePlanCoreAsync(_request!, _lifeCts.Token);
            Timeline.SetPosition(0, notify: false);
            SetState(GuiState.Ready);
            await RefreshPreviewAsync(InvalidationCategory.LayoutAffecting, force: true);
        }
        catch (OperationCanceledException)
        {
            SetState(GuiState.Ready);
        }
        catch (Exception ex)
        {
            SetErrorState("Failed to open input: " + ex.Message);
        }
    }

    /// <summary>Opens a saved project file (.mdpviz.json).</summary>
    public async Task OpenProjectAsync(string path)
    {
        try
        {
            VisualizationProject project = VisualizationProjectStore.Load(path);
            await OpenRequestAsync(project.Request, project.LastPreviewTimeSeconds, Path.GetFullPath(path));
            _isDirty = false;
            OnPropertyChanged(nameof(IsDirty), nameof(ProjectTitle));
            if (File.Exists(project.Request.InputPath))
                AddRecentFile(project.Request.InputPath);
            _autosaveTimer.Stop();
            VisualizationProjectStore.ClearRecovery(RecoveryDirectory);
        }
        catch (Exception ex)
        {
            SetErrorState("Failed to open project: " + ex.Message);
        }
    }

    /// <summary>Saves the project; opens a dialog when no path is given.</summary>
    public async Task SaveProjectAsync(string? path = null)
    {
        if (_request is null)
            return;
        try
        {
            path ??= _projectPath;
            if (path is null)
                path = await _dialogs.SaveFileAsync();
            if (path is null)
                return;

            var project = new VisualizationProject
            {
                Request = _request,
                FilePath = path,
                LastPreviewTimeSeconds = Timeline.PositionSeconds,
                LastPreviewFidelity = Preview.Fidelity,
            };
            VisualizationProjectStore.Save(project, path);
            _projectPath = path;
            _isDirty = false;
            _autosaveTimer.Stop();
            VisualizationProjectStore.ClearRecovery(RecoveryDirectory);
            OnPropertyChanged(nameof(IsDirty), nameof(ProjectTitle));
            Diagnostics.AddLine("Project saved: " + path);
            RefreshCommands();
        }
        catch (Exception ex)
        {
            SetErrorState("Failed to save project: " + ex.Message);
        }
    }

    /// <summary>Applies a request delta: new snapshot, revision bump, dirty flag,
    /// categorized preview refresh (debounced) and autosave scheduling.</summary>
    public void ApplySetting(string settingPath, Func<VisualizationRequest, VisualizationRequest> transform)
    {
        if (_request is null || _suppressSettingsSync)
            return;

        VisualizationRequest next = transform(_request);
        if (next == _request)
            return;

        InvalidationCategory category = InvalidationCategorizer.Categorize(settingPath);
        SetRequest(next, markDirty: true);
        ScheduleAutosave();
        RefreshPreviewDebounced(category);
    }

    /// <summary>Re-formats the canonical command for the current request.</summary>
    public async Task UpdateCommandAsync()
    {
        await Task.CompletedTask;
        if (_request is null)
        {
            Command.SetDisplay(null);
            Command.SetValidity(Array.Empty<ValidationIssue>());
            return;
        }
        CommandDisplayMode mode = Command.IsResolved ? CommandDisplayMode.FullyResolved : CommandDisplayMode.Compact;
        Command.SetDisplay(_formatter.Format(_request, mode));
        Command.SetValidity(_validationIssues);
    }

    /// <summary>Preflight-validates and launches the export pipeline.</summary>
    public async Task StartRenderAsync()
    {
        if (_request is null)
            return;

        IReadOnlyList<ValidationIssue> issues = VisualizationRequestValidator.Validate(_request);
        if (!VisualizationRequestValidator.IsValid(issues))
        {
            SetState(GuiState.Error, "Fix the validation errors before rendering.");
            return;
        }

        SetState(GuiState.ExportPreflight);
        Export.Reset();
        _exportCts = new CancellationTokenSource();
        SetState(GuiState.Exporting);

        try
        {
            var progress = new Progress<ExportProgressEvent>(Export.OnEvent);
            string workspace = await _exportProcess.StartAsync(_request, progress, _exportCts.Token);
            Export.SetWorkspace(workspace);
            bool failed = Export.HasFailed;
            SetState(failed ? GuiState.Error : GuiState.Completed);
            if (!failed)
                _notifications.Notify("Export completed.");
        }
        catch (OperationCanceledException)
        {
            Export.SetCancelled();
            SetState(GuiState.Ready);
        }
        catch (Exception ex)
        {
            Export.SetFailed(null, "Export failed: " + ex.Message);
            SetErrorState("Export failed: " + ex.Message);
        }
    }

    public void CancelExportAsync()
    {
        _exportCts.Cancel();
        StopMotion();
    }

    public void CancelPreview()
    {
        _previewCts.Cancel();
        StopMotion();
    }

    public void RestoreDetectedMetadata()
    {
        if (_session is null)
            return;
        ApplySetting(nameof(VisualizationRequest.Title), r => r with
        {
            Title = _session.Input.Title ?? Path.GetFileNameWithoutExtension(_session.Input.FullPath),
            Subtitle = _session.Input.Game ?? _session.Input.System,
            Credits = _session.Input.Composer,
        });
    }

    public async Task<string?> ChooseFontFileAsync()
    {
        // Reuse the storage provider via a dedicated font picker (TTF/OTF).
        return await _dialogs.ChooseFontFileAsync();
    }

    /// <summary>Reopens the session for the current request's input path.</summary>
    public async Task RelinkInputAsync(string path)
    {
        if (_request is null)
            return;
        await OpenRequestAsync(_request with { InputPath = path }, Timeline.PositionSeconds, _projectPath);
    }

    /// <summary>Disposes timers/session on window close.</summary>
    public void Shutdown()
    {
        _lifeCts.Cancel();
        _autosaveTimer.Stop();
        _previewDebounce.Stop();
        _previewCts.Cancel();
        _exportCts.Cancel();
        VisualizationProjectStore.ClearRecovery(RecoveryDirectory);
        _ = DisposeSessionAsync();
    }

    // ---- Setup ----

    private void WireCommands()
    {
        OpenInputCommand = new AsyncRelayCommand(OpenInputDialogAsync);
        OpenProjectCommand = new AsyncRelayCommand(OpenProjectDialogAsync);
        SaveProjectCommand = new AsyncRelayCommand(() => SaveProjectAsync(null));
        SaveProjectAsCommand = new AsyncRelayCommand(SaveProjectAsAsync);
        RenderCommand = new RelayCommand(() => _ = StartRenderAsync(),
            () => _state == GuiState.Ready && _request is not null && !HasFatalValidationIssues);
        CopyCommandCommand = new AsyncRelayCommand(() => _clipboard.SetTextAsync(Command.DisplayText));
        CopyRequestJsonCommand = new AsyncRelayCommand(CopyRequestJsonAsync);
        RecheckToolsCommand = new AsyncRelayCommand(() => Tools.RefreshAsync(_lifeCts.Token));
        RefreshPreviewCommand = new AsyncRelayCommand(() => RefreshPreviewAsync(InvalidationCategory.PresentationOnly, force: true));
        CancelExportCommand = new RelayCommand(CancelExportAsync);
        RenderStillCommand = new RelayCommand(() => _ = RenderPreviewFrameAsync(Timeline.PositionSeconds, PreviewFidelity.AccurateStill),
            () => _state == GuiState.Ready && _session is not null);
        CancelPreviewCommand = new RelayCommand(CancelPreview);
        FocusCommandPanelCommand = new RelayCommand(() => CommandPanelFocusRequested?.Invoke());
    }

    private void WireEvents()
    {
        Preview.RetryRequested += () => _ = RenderPreviewFrameAsync(Timeline.PositionSeconds, PreviewFidelity.AccurateStill);
        Preview.MotionFrameChanged += Timeline.SetPositionFromMotion;
        Timeline.ScrubRequested += HandleScrubRequested;
        Timeline.PlayPauseRequested += HandlePlayPauseRequested;
        Timeline.SeekRequested += HandleSeekRequested;
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
            SetErrorState(ex.Message);
        }
    }

    private async Task OpenProjectDialogAsync()
    {
        try
        {
            string? path = await _dialogs.OpenFileAsync();
            if (path is not null)
                await OpenProjectAsync(path);
        }
        catch (Exception ex)
        {
            SetErrorState(ex.Message);
        }
    }

    private async Task SaveProjectAsAsync()
    {
        string? path = await _dialogs.SaveFileAsync();
        if (path is not null)
            await SaveProjectAsync(path);
    }

    private async Task CopyRequestJsonAsync()
    {
        if (_request is null)
            return;
        await _clipboard.SetTextAsync(VisualizationRequestSerializer.Serialize(_request));
    }

    private void OnCommandModeChanged(bool value) { _ = UpdateCommandAsync(); }

    // ---- Request snapshot handling ----

    private void SetRequest(VisualizationRequest request, bool markDirty)
    {
        _suppressSettingsSync = true;
        try
        {
            _request = request;
            _requestRevision++;
            _isDirty = markDirty;
            Settings.Synchronize(request);
            ValidateCurrent();
            _ = UpdateCommandAsync();
            OnPropertyChanged(nameof(IsDirty), nameof(ProjectTitle));
        }
        finally
        {
            _suppressSettingsSync = false;
        }
        RefreshCommands();
    }

    private void ValidateCurrent()
    {
        _validationIssues = _request is null
            ? Array.Empty<ValidationIssue>()
            : VisualizationRequestValidator.Validate(_request);
        HasFatalValidationIssues = !VisualizationRequestValidator.IsValid(_validationIssues);
    }

    private async Task OpenRequestAsync(VisualizationRequest request, double startTime, string? projectPath)
    {
        SetState(GuiState.Inspecting);
        await DisposeSessionAsync();
        _session = await _previewFactory.OpenAsync(request.InputPath, _lifeCts.Token);
        SetState(GuiState.PreparingPreview);
        SetRequest(request, markDirty: false);
        await UpdatePlanCoreAsync(_request!, _lifeCts.Token);
        Timeline.SetPosition(startTime, notify: false);
        _projectPath = projectPath;
        OnPropertyChanged(nameof(ProjectTitle));
        SetState(GuiState.Ready);
        await RefreshPreviewAsync(InvalidationCategory.LayoutAffecting, force: true);
    }

    private async Task UpdatePlanCoreAsync(VisualizationRequest request, CancellationToken ct)
    {
        if (_session is null)
            return;
        VisualizationPlanResult plan = await _session.PlanAsync(request, ct);
        _plan = plan;
        Settings.SynchronizePlan(plan, _session.Input);
        Settings.SynchronizeAnalysisStatus(_session.Capabilities);
        Timeline.SynchronizePlan(plan);
        _ = Tools.RefreshAsync(ct);
    }

    // ---- Preview refresh ----

    private void RefreshPreviewDebounced(InvalidationCategory category)
    {
        if (category == InvalidationCategory.ExportOnly || _suppressPreviewRefresh)
        {
            _ = UpdateCommandAsync();
            return;
        }
        _pendingCategory = category;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private async void OnPreviewDebounceTick(object? sender, EventArgs e)
    {
        _previewDebounce.Stop();
        InvalidationCategory category = _pendingCategory;
        _pendingCategory = InvalidationCategory.ExportOnly;
        await RefreshPreviewAsync(category, force: false);
    }

    private async Task RefreshPreviewAsync(InvalidationCategory category, bool force)
    {
        if (_request is null || _session is null)
            return;
        if (_state is GuiState.Empty or GuiState.Inspecting or GuiState.PreparingPreview)
            return;

        if (IsReducedMotion)
        {
            // Reduced motion: never auto-render; still keep the command fresh.
            Preview.SetReducedMotion(true);
            await UpdateCommandAsync();
            return;
        }

        try
        {
            await _previewGate.WaitAsync(_lifeCts.Token);
            _previewCts.Cancel();
            _previewCts = new CancellationTokenSource();
            CancellationToken ct = _previewCts.Token;
            int revision = _requestRevision;
            VisualizationRequest request = _request;

            if (category is InvalidationCategory.LayoutAffecting or InvalidationCategory.AnalysisAffecting
                or InvalidationCategory.CaptureAffecting)
            {
                await UpdatePlanCoreAsync(request, ct);
                if (ct.IsCancellationRequested || revision != _requestRevision)
                    return;
            }

            if (Timeline.IsPlaying)
                return;

            double time = Timeline.PositionSeconds;
            PreviewFidelity fidelity = force ? PreviewFidelity.AccurateStill : PreviewFidelity.Layout;
            await RenderFrameCoreAsync(request, time, fidelity, revision, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception ex)
        {
            Preview.SetError(ex.Message);
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private async Task RenderPreviewFrameAsync(double time, PreviewFidelity fidelity)
    {
        if (_request is null || _session is null)
            return;
        if (IsReducedMotion)
        {
            Preview.SetReducedMotion(true);
            return;
        }
        try
        {
            await _previewGate.WaitAsync(_lifeCts.Token);
            _previewCts.Cancel();
            _previewCts = new CancellationTokenSource();
            CancellationToken ct = _previewCts.Token;
            int revision = _requestRevision;
            await RenderFrameCoreAsync(_request, time, fidelity, revision, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            Preview.SetError(ex.Message);
        }
        finally
        {
            _previewGate.Release();
        }
    }

    private async Task RenderFrameCoreAsync(
        VisualizationRequest request,
        double time,
        PreviewFidelity fidelity,
        int revision,
        CancellationToken ct)
    {
        Preview.SetLoading(true);
        try
        {
            var preview = new PreviewFrameRequest
            {
                TimeSeconds = time,
                Fidelity = fidelity,
                Width = _settings.Settings.PreviewMaxWidth,
                Height = _settings.Settings.PreviewMaxHeight,
            };
            PreviewFrameResult frame = await _session!.RenderFrameAsync(request, preview, ct);
            if (ct.IsCancellationRequested || revision != _requestRevision)
                return;
            if (Math.Abs(Timeline.PositionSeconds - time) > 0.05)
                return;
            Preview.ApplyFrame(frame);
        }
        finally
        {
            if (!Preview.IsPlaying)
                Preview.SetLoading(false);
        }
    }

    // ---- Timeline events ----

    private void HandleScrubRequested(double time, bool isFinal)
    {
        if (Timeline.IsPlaying)
            return;
        _ = RenderPreviewFrameAsync(time, isFinal ? PreviewFidelity.AccurateStill : PreviewFidelity.Layout);
    }

    private void HandleSeekRequested(double time)
    {
        if (Timeline.IsPlaying)
            return;
        _ = RenderPreviewFrameAsync(time, PreviewFidelity.AccurateStill);
    }

    private void HandlePlayPauseRequested()
    {
        if (Preview.IsPlaying)
        {
            StopMotion();
            return;
        }
        _ = StartMotionPreviewAsync();
    }

    private async Task StartMotionPreviewAsync()
    {
        if (_session is null || _request is null)
            return;
        if (IsReducedMotion)
        {
            Preview.SetReducedMotion(true);
            return;
        }
        try
        {
            SetState(GuiState.Previewing);
            double start = Math.Max(0, Timeline.PositionSeconds - 1.0);
            const double duration = 3.0;
            const int fps = 15;

            var request = new MotionPreviewRequest
            {
                StartSeconds = start,
                DurationSeconds = duration,
                Fps = fps,
                MaxWidth = _settings.Settings.PreviewMaxWidth,
                MaxHeight = _settings.Settings.PreviewMaxHeight,
            };
            var progress = new Progress<PreviewProgress>(p => Preview.SetLoading(p?.Fraction is > 0));

            MotionPreviewResult result = await _session.RenderMotionAsync(_request, request, progress, _lifeCts.Token);
            Preview.PlayMotion(result.FramePaths, result.Fps, request.StartSeconds, request.DurationSeconds);
            Timeline.SetPlaying(true);
            SetState(GuiState.Ready);
        }
        catch (OperationCanceledException)
        {
            StopMotion();
            SetState(GuiState.Ready);
        }
        catch (Exception ex)
        {
            StopMotion();
            Preview.SetError("Motion preview failed: " + ex.Message);
            SetState(GuiState.Ready);
        }
    }

    private void StopMotion()
    {
        Preview.StopMotion();
        Timeline.SetPlaying(false);
        if (_state == GuiState.Previewing)
            SetState(GuiState.Ready);
    }

    // ---- Autosave / recovery ----

    private void ScheduleAutosave()
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void Autosave()
    {
        _autosaveTimer.Stop();
        if (_request is null)
            return;
        try
        {
            var project = new VisualizationProject
            {
                Request = _request,
                FilePath = _projectPath,
                LastPreviewTimeSeconds = Timeline.PositionSeconds,
                LastPreviewFidelity = Preview.Fidelity,
            };
            VisualizationProjectStore.SaveRecovery(project, RecoveryDirectory);
            Diagnostics.AddLine("Recovery snapshot saved.");
        }
        catch (Exception ex)
        {
            Diagnostics.AddLine("Autosave failed: " + ex.Message);
        }
    }

    // ---- Helpers ----

    private void SetState(GuiState state, string? errorMessage = null)
    {
        _state = state;
        _errorMessage = errorMessage;
        OnPropertyChanged(nameof(StatusText));
        RefreshCommands();
    }

    private void SetErrorState(string message)
    {
        Diagnostics.AddLine("error: " + message);
        SetState(GuiState.Error, message);
    }

    private void RefreshCommands()
    {
        RenderCommand.RaiseCanExecuteChanged();
        RenderStillCommand.RaiseCanExecuteChanged();
        OpenInputCommand.RaiseCanExecuteChanged();
        OpenProjectCommand.RaiseCanExecuteChanged();
        SaveProjectCommand.RaiseCanExecuteChanged();
        SaveProjectAsCommand.RaiseCanExecuteChanged();
        RefreshPreviewCommand.RaiseCanExecuteChanged();
    }

    private async Task DisposeSessionAsync()
    {
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
        _plan = null;
        Timeline.SynchronizePlan(null);
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
    }
}
