using System.Collections.ObjectModel;
using Avalonia.Threading;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Preview;
using Fmp.Application.Validation;
using Fmp.Application.PlaybackAssets;
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
    private readonly IVisualizationPreviewSessionFactory _previewFactory;
    private readonly string? _initialInputPath;
    private readonly CancellationTokenSource _lifeCts = new();
    private readonly DispatcherTimer _previewDebounce;

    private VisualizationRequest? _request;
    private VisualizationInputInfo? _input;
    private IVisualizationPreviewSession? _session;
    private VisualizationPlanResult? _plan;
    private int _requestRevision;
    private int _previewGeneration;
    private int _refreshSeq;
    private PreviewRefreshKind? _pendingPreviewRefresh;
    private Task _activePreviewRefresh = Task.CompletedTask;
    private GuiState _state = GuiState.Empty;
    private GuiError? _error;
    private IReadOnlyList<ValidationIssue> _validationIssues = Array.Empty<ValidationIssue>();
    private bool _hasFatalValidationIssues;
    private double _previewTimeSeconds;
    private double _durationSeconds;
    private (double Width, double Height) _previewViewportSize;

    // Patch-3: progressive first paint splits the single refresh into an
    // immediate timeline-only frame and an independent refinement that
    // replaces it with interactive output once stems are prepared.
    private bool _interactivePreviewReady;
    private bool _accuratePreviewRunning;
    private CancellationTokenSource _refinementCts = new();
    private Task _refinementTask = Task.CompletedTask;
    private int _refinementGeneration;

    private CancellationTokenSource _previewCts = new();
    private CancellationTokenSource _exportCts = new();
    private Task? _shutdownTask;

    private const int RecentFilesMax = 5;

    /// <summary>Debounce for visual/replan refreshes (e.g. style changes).</summary>
    private static readonly TimeSpan VisualRefreshDelay =
        TimeSpan.FromMilliseconds(250);

    /// <summary>Debounce for preview-viewport resize replans (150-200ms window).</summary>
    private static readonly TimeSpan ResizeRefreshDelay =
        TimeSpan.FromMilliseconds(180);

    /// <summary>Debounce for timeline seeks (frame-only refresh).</summary>
    private static readonly TimeSpan SeekRefreshDelay =
        TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// What a queued preview refresh must recompute. Ordering matters: a
    /// stronger (plan) refresh must never be downgraded to a weaker (frame)
    /// one when a seek arrives while a plan refresh is already pending.
    /// </summary>
    private enum PreviewRefreshKind
    {
        FrameOnly = 0,
        PlanAndFrame = 1,
    }

    public MainWindowViewModel(
        GuiSettingsStore settings,
        FileDialogService dialogs,
        ClipboardService clipboard,
        ExportProcessService exportProcess,
        IVisualizationPreviewSessionFactory previewFactory,
        string? initialInputPath)
    {
        _settings = settings;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _exportProcess = exportProcess;
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
    public AsyncRelayCommand RenderAccuratePreviewCommand { get; private set; } = null!;
    public RelayCommand PreviousPointCommand { get; private set; } = null!;
    public RelayCommand NextPointCommand { get; private set; } = null!;
    public AsyncRelayCommand ChooseOutputPathCommand { get; private set; } = null!;
    public RelayCommand OpenOutputFolderCommand { get; private set; } = null!;
    public RelayCommand OpenOutputCommand { get; private set; } = null!;
    public AsyncRelayCommand CopyErrorCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportFurnaceAssetsCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportMidiCommand { get; private set; } = null!;

    // ---- Observable state ----

    public string StatusText => _state switch
    {
        GuiState.Empty => "Open a music file to begin.",
        GuiState.LoadingInput => "Loading input…",
        GuiState.Ready => "Ready to render",
        GuiState.Rendering => "Rendering…",
        _ => "",
    };

    public bool HasInput => _request is not null;
    public bool IsBusy => _state is GuiState.LoadingInput or GuiState.Rendering;
    public bool CanEdit => HasInput && _state == GuiState.Ready;
    public bool IsAccuratePreviewRunning => _accuratePreviewRunning;
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

    private string _furnaceExportNotice = "";
    private bool _furnaceExportNoticeIsError;

    /// <summary>User-facing result of the last Furnace asset export, or empty.</summary>
    public string FurnaceExportNotice
    {
        get => _furnaceExportNotice;
        private set
        {
            if (SetProperty(ref _furnaceExportNotice, value))
            {
                OnPropertyChanged(nameof(HasFurnaceExportNotice));
                OnPropertyChanged(nameof(HasExportNotice));
            }
        }
    }

    /// <summary>True when a Furnace export result notice is visible.</summary>
    public bool HasFurnaceExportNotice => !string.IsNullOrEmpty(FurnaceExportNotice);

    /// <summary>True when the Furnace export notice represents a failure.</summary>
    public bool FurnaceExportNoticeIsError
    {
        get => _furnaceExportNoticeIsError;
        private set => SetProperty(ref _furnaceExportNoticeIsError, value);
    }

    private string _midiExportNotice = "";

    public string MidiExportNotice
    {
        get => _midiExportNotice;
        private set
        {
            if (SetProperty(ref _midiExportNotice, value))
            {
                OnPropertyChanged(nameof(HasMidiExportNotice));
                OnPropertyChanged(nameof(HasExportNotice));
            }
        }
    }

    /// <summary>True when a MIDI export result notice is visible.</summary>
    public bool HasMidiExportNotice => !string.IsNullOrEmpty(MidiExportNotice);

    /// <summary>True when any export result notice (Furnace or MIDI) is visible.</summary>
    public bool HasExportNotice => HasFurnaceExportNotice || HasMidiExportNotice;

    private bool _midiExportNoticeIsError;

    /// <summary>True when the MIDI export notice represents a failure.</summary>
    public bool MidiExportNoticeIsError
    {
        get => _midiExportNoticeIsError;
        private set => SetProperty(ref _midiExportNoticeIsError, value);
    }

    /* ---- MIDI export options ---- */

    private int _midiPpq = 960;

    /// <summary>MIDI pulses-per-quarter-note resolution for raw transcription.</summary>
    public int MidiPpq { get => _midiPpq; set => SetProperty(ref _midiPpq, Math.Clamp(value, 96, 9600)); }

    public IReadOnlyList<int> MidiPpqOptions { get; } =
        new[] { 240, 480, 960, 1920, 3840 };

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
            // Prefer the planned track count: the inspector's initial track
            // collection is empty for several formats.
            int trackCount = _plan?.Tracks.Count ?? _input.Tracks.Count;
            return $"{_input.Format} · {duration} · {trackCount} tracks";
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

    /// <summary>Right-hand status readout: resolution · fps · encoder.</summary>
    public string RenderSpecText
    {
        get
        {
            OutputSettings output = _request?.Output ?? new OutputSettings();
            double fps = output.FpsDenominator > 0
                ? output.FpsNumerator / (double)output.FpsDenominator
                : 0;
            string fpsText = fps > 0 && fps == Math.Floor(fps)
                ? $"{(int)fps} FPS"
                : $"{fps:0.##} FPS";
            string encoder = output.Encoder switch
            {
                VideoEncoder.LibX264 => "H.264",
                VideoEncoder.Nvenc => "NVENC",
                _ => "H.264",
            };
            return $"{output.Width}×{output.Height} · {fpsText} · {encoder}";
        }
    }

    public bool HasFatalValidationIssues
    {
        get => _hasFatalValidationIssues;
        private set => SetProperty(ref _hasFatalValidationIssues, value);
    }

    /// <summary>Validation issues for the current request, surfaced to the UI.</summary>
    public IReadOnlyList<ValidationIssue> ValidationIssues => _validationIssues;
    public bool HasValidationIssues => _validationIssues.Count > 0;

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

    /// <summary>
    /// Scrub value bound to the timeline slider. Updates the displayed time
    /// while dragging but never schedules a preview; the preview fires once on
    /// drag completion via <see cref="CommitScrub"/>. This keeps slow dragging
    /// from launching a preview render every debounce tick.
    /// </summary>
    public double PreviewScrubTime
    {
        get => _previewTimeSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, DurationSeconds));
            if (SetProperty(ref _previewTimeSeconds, clamped))
                OnPropertyChanged(nameof(TimeText));
        }
    }

    /// <summary>Called when a slider drag (or discrete key jump) completes.</summary>
    public void CommitScrub()
    {
        if (HasInput)
            ScheduleSeekPreview();
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
            // Transactional open: create the new session BEFORE disposing the
            // old one, so a failed open leaves the previous input, plan and
            // session fully intact. Disposing first could strand the UI with a
            // visible request but no active session.
            IVisualizationPreviewSession newSession =
                await _previewFactory.OpenAsync(fullPath, _lifeCts.Token);

            await DisposeSessionAsync();
            _session = newSession;
            _input = newSession.Input;
            ClearError();
            OnPropertyChanged(nameof(HasInput), nameof(InputTitle), nameof(InputSummary));
            AddRecentFile(fullPath);
            SetRequest(BuildInitialRequest(fullPath));

            // The first usable frame is the plan + a timeline-only still. The
            // UI becomes usable (move to Ready) as soon as that first real
            // frame exists; refinement to interactive scope output continues in
            // the background and replaces it when ready.
            await LoadInitialPreviewAsync(
                _request!,
                newSession,
                _lifeCts.Token);

            SetState(GuiState.Ready);
            StartPreviewRefinement();
        }
        catch (OperationCanceledException)
        {
            SetState(HasInput ? GuiState.Ready : GuiState.Empty);
        }
        catch (Exception ex)
        {
            SetError("Failed to open input: " + ex.Message);
            SetState(HasInput ? GuiState.Ready : GuiState.Empty);
        }
    }

    /// <summary>
    /// Produces and displays the first usable frame: a plan followed by a
    /// timeline-only still (no stems/energy yet). After this returns the UI can
    /// transition to <see cref="GuiState.Ready"/> and refinement may start.
    /// </summary>
    private async Task LoadInitialPreviewAsync(
        VisualizationRequest request,
        IVisualizationPreviewSession session,
        CancellationToken cancellationToken)
    {
        Preview.SetLoading(
            true,
            "Reading timeline…");

        VisualizationPlanResult plan =
            await session.PlanAsync(
                request,
                cancellationToken);

        ApplyPlan(plan);

        (int width, int height) =
            GetPreviewDimensions(request.Output);

        PreviewFrameRequest frameRequest =
            BuildFrameRequest(
                PreviewFidelity.TimelineStill,
                PreviewTimeSeconds,
                width,
                height);

        PreviewFrameResult firstFrame =
            await session.RenderFrameAsync(
                request,
                frameRequest,
                cancellationToken);

        Preview.ApplyFrame(firstFrame);

        _interactivePreviewReady = false;
    }

    private static PreviewFrameRequest BuildFrameRequest(
        PreviewFidelity fidelity,
        double timeSeconds,
        int width,
        int height)
    {
        return new PreviewFrameRequest
        {
            TimeSeconds = timeSeconds,
            Width = width,
            Height = height,
            Fidelity = fidelity,
        };
    }

    /// <summary>
    /// Fidelity used for immediate scrubbing/refresh frame requests: interactive
    /// once refinement has succeeded, timeline-only until then.
    /// </summary>
    private PreviewFidelity CurrentInteractiveFidelity =>
        _interactivePreviewReady
            ? PreviewFidelity.InteractiveStill
            : PreviewFidelity.TimelineStill;

    /// <summary>Applies a request delta and schedules a debounced preview refresh.</summary>
    public void ApplyVisualSetting(Func<VisualizationRequest, VisualizationRequest> transform)
    {
        if (_request is null)
            return;
        VisualizationRequest previous = _request;
        if (SetNewRequest(transform))
        {
            _previewGeneration++;

            PreviewRequestImpact impact =
                _session!.ClassifyChange(previous, _request);

            switch (impact)
            {
                case PreviewRequestImpact.None:
                case PreviewRequestImpact.ExportOnly:
                    // No preview work required.
                    return;

                case PreviewRequestImpact.Frame:
                    // Style/presentation change: keep the session-owned raw
                    // asset task alive; rebuild only the timeline source and
                    // render a fresh frame.
                    CancelRefinement();
                    QueuePreviewRefresh(PreviewRefreshKind.FrameOnly, VisualRefreshDelay);
                    break;

                case PreviewRequestImpact.Plan:
                    // Track/projection/layout change: re-plan and re-render a
                    // frame without invalidating the interactive preview.
                    CancelRefinement();
                    QueuePreviewRefresh(PreviewRefreshKind.PlanAndFrame, VisualRefreshDelay);
                    break;

                case PreviewRequestImpact.TimelineCapture:
                    // Capture-affecting change: mark the interactive preview
                    // stale, cancel refinement, then re-plan and re-render.
                    _interactivePreviewReady = false;
                    CancelRefinement();
                    QueuePreviewRefresh(PreviewRefreshKind.PlanAndFrame, VisualRefreshDelay);
                    break;
            }
        }
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

        try
        {
            var progress = new Progress<ExportProgressEvent>(Export.OnEvent);
            ExportResult result = await RunWithCapturedLeaseAsync(request, progress);
            SetState(GuiState.Ready);
            if (result.Succeeded)
            {
                ClearError();
            }
            else if (result.Cancelled)
            {
                // The failure summary (with the retained workspace/log path)
                // was already set via the cancelled event.
                SetError("Render cancelled.");
            }
            else
            {
                SetError("Render failed.");
            }
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

    private async Task<ExportResult> RunWithCapturedLeaseAsync(
        VisualizationRequest request,
        Progress<ExportProgressEvent> progress)
    {
        // Acquire a reusable capture lease and hold it for the duration of
        // StartAsync so the session keeps the published bundle alive during
        // export. The lease is kept in an await-using scope (never stored in a
        // field). If acquisition fails, surface the error and do not start the
        // CLI.
        if (_session is null)
        {
            return await _exportProcess.StartAsync(
                request,
                progress,
                Export.SetWorkspace,
                _exportCts.Token);
        }

        VisualizationRequest pending = _request ?? request;
        await using (ReusableCaptureLease lease =
               await _session.AcquireReusableCaptureAsync(pending, _exportCts.Token))
        {
            return await _exportProcess.StartAsync(
                request,
                progress,
                Export.SetWorkspace,
                _exportCts.Token,
                lease.DirectoryPath,
                lease.CaptureKey);
        }
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

    /// <summary>
    /// Waits for any pending debounced refresh (or the active one) to complete.
    /// When no refresh is pending or active this is a no-op rather than starting
    /// an extra refresh.
    /// </summary>
    public async Task WaitForPreviewRefreshAsync()
    {
        _previewDebounce.Stop();

        if (_pendingPreviewRefresh is { } kind)
        {
            _pendingPreviewRefresh = null;
            _activePreviewRefresh = RefreshPreviewAsync(kind);
        }

        await _activePreviewRefresh;
        // Also let any in-flight background refinement settle so tests (and
        // callers) observe a stable preview image and fidelity.
        await _refinementTask;
    }

    /// <summary>
    /// Queues a frame-only refresh debounced for timeline movement. Seeking
    /// never changes the plan, so planning is skipped to reuse the cached
    /// prepared source and frame renderer.
    /// </summary>
    private void ScheduleSeekPreview()
    {
        QueuePreviewRefresh(PreviewRefreshKind.FrameOnly, SeekRefreshDelay);
    }

    private void QueuePreviewRefresh(PreviewRefreshKind kind, TimeSpan delay)
    {
        // Prevent an in-flight older frame from being applied while the new
        // refresh waits for its debounce interval.
        CancelPreview();

        if (_pendingPreviewRefresh is null
            || kind > _pendingPreviewRefresh.Value)
        {
            _pendingPreviewRefresh = kind;
        }

        _previewDebounce.Stop();
        _previewDebounce.Interval = delay;
        _previewDebounce.Start();
    }

    private void OnPreviewDebounceTick(object? sender, EventArgs e)
    {
        _previewDebounce.Stop();

        if (_pendingPreviewRefresh is not { } kind)
            return;

        _pendingPreviewRefresh = null;
        // Fire-and-observe: RefreshPreviewAsync catches its own operational
        // exceptions, so the timer boundary is controlled.
        _activePreviewRefresh = RefreshPreviewAsync(kind);
    }

    /// <summary>
    /// Performs an immediate preview refresh. <see cref="PreviewRefreshKind.PlanAndFrame"/>
    /// rebuilds the plan (reusing the session's cached capture/prepared source)
    /// before rendering a frame; <see cref="PreviewRefreshKind.FrameOnly"/> skips
    /// planning and renders a frame at the current time, reusing the cached plan
    /// dimensions.
    /// </summary>
    private Task RefreshPreviewAsync(PreviewRefreshKind kind)
        => RefreshPreviewAsync(kind, fidelityOverride: null);

    private async Task RefreshPreviewAsync(
        PreviewRefreshKind kind,
        PreviewFidelity? fidelityOverride)
    {
        if (_request is null || _session is null)
            return;

        int generation = _previewGeneration;
        int refreshSeq = ++_refreshSeq;

        VisualizationRequest request = _request;
        IVisualizationPreviewSession session = _session;
        double requestedTime = PreviewTimeSeconds;

        PreviewFidelity fidelity =
            fidelityOverride ?? CurrentInteractiveFidelity;

        CancelPreview();
        _previewCts.Dispose();
        _previewCts = new CancellationTokenSource();

        CancellationToken ct = _previewCts.Token;

        Preview.SetLoading(true);
        try
        {
            if (kind == PreviewRefreshKind.PlanAndFrame)
            {
                VisualizationPlanResult plan =
                    await session.PlanAsync(request, ct);

                if (IsObsolete(generation, refreshSeq, session, ct))
                    return;

                _plan = plan;
                ApplyPlan(plan);

                // Applying the plan may reduce or initialize duration.
                requestedTime = Math.Clamp(
                    requestedTime,
                    0,
                    Math.Max(0, DurationSeconds));
            }

            (int width, int height) =
                GetPreviewDimensions(request.Output);

            PreviewFrameRequest frameRequest = BuildFrameRequest(
                fidelity,
                requestedTime,
                width,
                height);

            PreviewFrameResult frame = await session.RenderFrameAsync(
                request,
                frameRequest,
                ct);

            if (IsObsolete(generation, refreshSeq, session, ct))
                return;

            Preview.ApplyFrame(frame);
            ClearError();

            // When refinement is still running the applied frame is a
            // timeline-only approximation; restart the interactive waiter at
            // the latest timeline position so a fresh request/time is used.
            if (!_interactivePreviewReady
                && fidelity != PreviewFidelity.AccurateStill)
            {
                StartPreviewRefinement();
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer request or a newer refresh. The session-owned
            // asset task continues; only this waiter is cancelled.
        }
        catch (Exception ex)
        {
            if (!IsObsolete(generation, refreshSeq, session, ct))
                Preview.SetError(ex.Message);
        }
        finally
        {
            // Only the most recently started refresh clears the loading flag, so a
            // cancelled superseded refresh can never turn it off early.
            if (_refreshSeq == refreshSeq)
                Preview.SetLoading(false);
        }
    }

    /// <summary>
    /// Manual refresh always rebuilds the plan, bypassing any debounce. Used by
    /// the Refresh command and Retry controls.
    /// </summary>
    public Task RefreshPreviewManuallyAsync()
    {
        _previewDebounce.Stop();
        _pendingPreviewRefresh = null;

        return RefreshPreviewAsync(PreviewRefreshKind.PlanAndFrame);
    }

    /// <summary>
    /// Renders a single accurate still for the current request/time. Applies to
    /// one frame request only and never becomes persistent refresh state: after
    /// it completes, subsequent seeks and edits resume the interactive fidelity.
    /// </summary>
    public async Task RenderAccuratePreviewAsync()
    {
        if (_request is null
            || _session is null
            || _accuratePreviewRunning)
        {
            return;
        }

        _accuratePreviewRunning = true;
        OnPropertyChanged(nameof(IsAccuratePreviewRunning));
        RenderAccuratePreviewCommand.RaiseCanExecuteChanged();

        _previewDebounce.Stop();
        _pendingPreviewRefresh = null;
        CancelRefinement();

        try
        {
            await RefreshPreviewAsync(
                PreviewRefreshKind.FrameOnly,
                PreviewFidelity.AccurateStill);
        }
        finally
        {
            _accuratePreviewRunning = false;
            OnPropertyChanged(nameof(IsAccuratePreviewRunning));
            RenderAccuratePreviewCommand.RaiseCanExecuteChanged();
        }
    }

    // ---- Progressive refinement ----

    /// <summary>
    /// Cancels only the current refinement waiter. Never cancels the
    /// session-owned raw scope/stem task, so repeated edits/seeks cannot abort
    /// preparation.
    /// </summary>
    private void CancelRefinement()
    {
        _refinementCts.Cancel();
        _refinementCts.Dispose();
        _refinementCts = CancellationTokenSource.CreateLinkedTokenSource(_lifeCts.Token);
    }

    /// <summary>
    /// Starts a new interactive-refinement waiter for the current request and
    /// timeline position. The heavy session-owned asset task runs independently
    /// and continues across superseded waiters.
    /// </summary>
    private void StartPreviewRefinement()
    {
        if (_request is null || _session is null)
            return;

        CancelRefinement();

        int generation = ++_refinementGeneration;

        VisualizationRequest request = _request;
        double time = PreviewTimeSeconds;
        CancellationToken ct = _refinementCts.Token;

        Preview.SetLoading(
            true,
            "Preparing channel scopes…");

        _refinementTask = RefinePreviewAsync(generation, request, time, ct);
    }

    private async Task RefinePreviewAsync(
        int generation,
        VisualizationRequest request,
        double timeSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            (int width, int height) =
                GetPreviewDimensions(request.Output);

            PreviewFrameResult frame =
                await _session!.RenderFrameAsync(
                    request,
                    BuildFrameRequest(
                        PreviewFidelity.InteractiveStill,
                        timeSeconds,
                        width,
                        height),
                    cancellationToken);

            if (cancellationToken.IsCancellationRequested
                || generation != _refinementGeneration
                || request != _request)
            {
                return;
            }

            Preview.ApplyFrame(frame);
            _interactivePreviewReady = true;
            ClearError();
        }
        catch (OperationCanceledException)
        {
            // The session-owned asset task continues when only this waiter was
            // superseded by a newer seek/request.
        }
        catch (Exception ex)
        {
            if (generation == _refinementGeneration)
            {
                Preview.SetRefinementWarning(
                    "Channel preview preparation failed: " + ex.Message);
            }
        }
        finally
        {
            if (generation == _refinementGeneration)
                Preview.SetLoading(false);
        }
    }

    private bool IsObsolete(
        int generation,
        int refreshSeq,
        IVisualizationPreviewSession session,
        CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            || generation != _previewGeneration
            || refreshSeq != _refreshSeq
            || !ReferenceEquals(session, _session);

    /// <summary>
    /// Fits the request output dimensions inside the preview dimension caps,
    /// preserving aspect ratio (the caps are maximums, not exact dimensions;
    /// clamping each axis independently would distort non-16:9 requests).
    /// </summary>
    /// <summary>
    /// Called (debounced) by the view when <c>PreviewViewport.Bounds</c> changes.
    /// The preview bitmap is requested at the viewport's logical size rather
    /// than only the configured maximum, so the layout resolver picks full or
    /// overview grammar for the dimensions that actually display, and a large
    /// bitmap is not rendered only to be scaled back down by Avalonia.
    /// </summary>
    public void SetPreviewViewportSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return;
        _previewViewportSize = (width, height);
    }

    /// <summary>
    /// Debounced viewport-resize refresh: updates the preview target size and
    /// schedules a plan+frame rebuild so the layout resolver re-evaluates the
    /// full/overview/device fallback for the new viewport dimensions. Debounced
    /// to avoid a rebuild on every pixel of an active window resize.
    /// </summary>
    public void SchedulePreviewResize(double width, double height)
    {
        if (width <= 0 || height <= 0)
            return;
        SetPreviewViewportSize(width, height);
        QueuePreviewRefresh(PreviewRefreshKind.PlanAndFrame, ResizeRefreshDelay);
    }

    private (int Width, int Height) GetPreviewDimensions(OutputSettings output)
    {
        int maxWidth = Math.Max(1, _settings.Settings.PreviewMaxWidth);
        int maxHeight = Math.Max(1, _settings.Settings.PreviewMaxHeight);

        // Prefer the actual viewport logical bounds (if available) so the
        // preview geometry matches what is on screen; otherwise fall back to
        // the configured maximum.
        double targetWidth = _previewViewportSize.Width > 0
            ? _previewViewportSize.Width
            : maxWidth;
        double targetHeight = _previewViewportSize.Height > 0
            ? _previewViewportSize.Height
            : maxHeight;

        return FitInside(
            Math.Max(1, output.Width),
            Math.Max(1, output.Height),
            (int)Math.Clamp(targetWidth, 1, maxWidth),
            (int)Math.Clamp(targetHeight, 1, maxHeight));
    }

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
        OnPropertyChanged(nameof(InputSummary));
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
        RefreshPreviewCommand = new AsyncRelayCommand(RefreshPreviewManuallyAsync, () => CanEdit);
        RenderAccuratePreviewCommand =
            new AsyncRelayCommand(
                RenderAccuratePreviewAsync,
                () => CanEdit && !_accuratePreviewRunning);
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
        ExportFurnaceAssetsCommand = new AsyncRelayCommand(ExportFurnaceAssetsAsync, () => HasInput);
        ExportMidiCommand = new AsyncRelayCommand(ExportMidiAsync, () => HasInput);
    }

    private void WireEvents()
    {
        Preview.RetryRequested += () => _ = RefreshPreviewManuallyAsync();
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

    private async Task ExportFurnaceAssetsAsync()
    {
        FurnaceExportNotice = "";
        if (_input is null)
        {
            FurnaceExportNotice = "Open a music file before exporting Furnace assets.";
            FurnaceExportNoticeIsError = true;
            return;
        }

        try
        {
            string? folder = await _dialogs.PickFolderAsync();
            if (folder is null)
                return; // user cancelled

            FurnaceExportResult result = await Task.Run(() =>
                FurnaceExportService.Dump(_input.FullPath, folder, sampleRate: 44100));

            if (!string.IsNullOrEmpty(result.RenderError))
            {
                FurnaceExportNotice = "Furnace export failed: " + result.RenderError;
                FurnaceExportNoticeIsError = true;
                return;
            }

            string warning = string.IsNullOrEmpty(result.ExportWarning)
                ? ""
                : " (" + result.ExportWarning + ")";

            if (result.ExportedCount == 0)
            {
                // A completed render with no captured FM instruments is usually
                // a signal, not a silent success: the file may be SSG/rhy-only,
                // unplayable, or may end before any FM key-on. Surface it.
                FurnaceExportNotice =
                    "Furnace export produced no FM instruments. If this song uses FM "
                    + "operator channels, it may not be an FMP/OVI track, or playback ended "
                    + "before any FM note. manifest.json was still written." + warning;
                FurnaceExportNoticeIsError = true;
                return;
            }

            FurnaceExportNotice =
                $"Furnace export complete: {result.ExportedCount} instrument(s) + manifest.json → {folder}{warning}";
            FurnaceExportNoticeIsError = false;
        }
        catch (Exception ex)
        {
            FurnaceExportNotice = "Furnace export failed: " + ex.Message;
            FurnaceExportNoticeIsError = true;
        }
    }

    private async Task ExportMidiAsync()
    {
        MidiExportNotice = "";
        if (_request is null /* HasInput gate */ || _input is null)
        {
            MidiExportNotice = "Open a music file before exporting MIDI.";
            MidiExportNoticeIsError = true;
            return;
        }

        string? outputPath;
        try
        {
            outputPath = await _dialogs.SaveMidiFileAsync(_input.DisplayName ?? _input.FullPath);
        }
        catch (Exception ex)
        {
            MidiExportNotice = "MIDI export failed: " + ex.Message;
            MidiExportNoticeIsError = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(outputPath))
            return; // user cancelled the save dialog

        try
        {
            // Raw MIDI is a direct transcription of the captured timeline. No
            // tempo/grid or per-voice projection is part of this command.
            var request = new MidiExportRequest { Ppq = MidiPpq };
            MidiExportResult result;
            if (_session is null)
            {
                result = new MidiExportService().ExportFromTimelinePath(_input.FullPath, request);
            }
            else
            {
                await using (ReusableCaptureLease lease =
                    await _session.AcquireReusableCaptureAsync(_request!, _exportCts.Token))
                {
                    string timelinePath = Path.Combine(lease.DirectoryPath, "timeline.json");
                    result = new MidiExportService().ExportFromTimelinePath(timelinePath, request);
                }
            }

            if (!result.Succeeded || result.Bytes is null)
            {
                MidiExportNotice = "MIDI export failed: " + (result.Error ?? "unknown error");
                MidiExportNoticeIsError = true;
                return;
            }

            File.WriteAllBytes(outputPath, result.Bytes);
            MidiExportNotice =
                $"MIDI export complete: {result.Bytes.Length:N0} bytes → {outputPath}";
            MidiExportNoticeIsError = false;
        }
        catch (Exception ex)
        {
            MidiExportNotice = "MIDI export failed: " + ex.Message;
            MidiExportNoticeIsError = true;
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
            nameof(OutputStatusText), nameof(RenderSpecText), nameof(CanOpenOutput));
        RefreshCommands();
        return true;
    }

    private void SetRequest(VisualizationRequest request)
    {
        _request = request;
        _requestRevision++;
        _previewGeneration++;
        Settings.Synchronize(request);
        ValidateCurrent();
        OnPropertyChanged(nameof(HasInput), nameof(OutputPath), nameof(HasOutputConflict),
            nameof(OutputStatusText), nameof(RenderSpecText), nameof(CanOpenOutput));
        RefreshCommands();
    }

    private void ValidateCurrent()
    {
        _validationIssues = _request is null
            ? Array.Empty<ValidationIssue>()
            : VisualizationRequestValidator.Validate(_request);
        HasFatalValidationIssues = !VisualizationRequestValidator.IsValid(_validationIssues);
        OnPropertyChanged(nameof(ValidationIssues), nameof(HasValidationIssues));
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

    private void ClearError()
    {
        if (Error is null)
            return;
        Error = null;
        OnPropertyChanged(nameof(StatusText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit), nameof(CanRender), nameof(IsBusy), nameof(HasInput),
            nameof(OutputStatusText), nameof(HasOutputConflict), nameof(CanOpenOutput));
        RenderCommand.RaiseCanExecuteChanged();
        RefreshPreviewCommand.RaiseCanExecuteChanged();
        RenderAccuratePreviewCommand.RaiseCanExecuteChanged();
        ChooseOutputPathCommand.RaiseCanExecuteChanged();
        OpenOutputFolderCommand.RaiseCanExecuteChanged();
        OpenOutputCommand.RaiseCanExecuteChanged();
        CopyErrorCommand.RaiseCanExecuteChanged();
        SeekRelativeCommand.RaiseCanExecuteChanged();
        SeekStartCommand.RaiseCanExecuteChanged();
        SeekEndCommand.RaiseCanExecuteChanged();
        PreviousPointCommand.RaiseCanExecuteChanged();
        NextPointCommand.RaiseCanExecuteChanged();
        ExportFurnaceAssetsCommand.RaiseCanExecuteChanged();
        ExportMidiCommand.RaiseCanExecuteChanged();
    }

    private async Task DisposeSessionAsync()
    {
        CancelPreview();
        CancelRefinement();
        _interactivePreviewReady = false;
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
        CancelRefinement();
        await DisposeSessionAsync();
        Preview.Dispose();
    }

    private VisualizationRequest BuildInitialRequest(string inputPath)
    {
        string? directory = Path.GetDirectoryName(inputPath);
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string outputDir = Path.Combine(directory ?? ".", name + ".visualization");
        return new VisualizationRequest
        {
            InputPath = inputPath,
            OutputPath = Path.Combine(outputDir, "visualization.mp4"),
            // New GUI projects explicitly start with Performance. Persisted
            // schema-v1 requests remain Diagnostic when composition is absent.
            Composition = CompositionKind.Performance,
            Playback = new PlaybackSettings
            {
                OpnaBackend = ResolvePersistedOpnaBackend(),
            },
        };
    }

    /// <summary>
    /// Resolves the persisted YM2608 backend, honoring the invalid-setting
    /// policy: "mdsound" and "native-audio" are accepted; any missing, unknown
    /// or retired value (notably "native-lle") stays MDSound and is never
    /// silently promoted to native audio.
    /// </summary>
    private FmpOpnaBackend ResolvePersistedOpnaBackend()
        => string.Equals(_settings.Settings.OpnaBackend, "native-audio", StringComparison.OrdinalIgnoreCase)
            ? FmpOpnaBackend.NativeAudio
            : FmpOpnaBackend.Mdsound;

    /// <summary>
    /// Persists the selected YM2608 audio backend through the existing
    /// settings store. The stored value is the serialized "mdsound"/"native-audio"
    /// string; the caller ensures it is one of those. No native probing happens here.
    /// </summary>
    public void PersistOpnaBackend(FmpOpnaBackend backend)
    {
        string serialized = backend == FmpOpnaBackend.NativeAudio ? "native-audio" : "mdsound";
        _settings.Update(settings => settings.OpnaBackend = serialized);
    }

    private void AddRecentFile(string path)
    {
        if (!IsPersistableRecent(path))
            return;
        _settings.Update(settings =>
        {
            settings.RecentFiles.Remove(path);
            settings.RecentFiles.Insert(0, path);
            if (settings.RecentFiles.Count > RecentFilesMax)
                settings.RecentFiles.RemoveAt(RecentFilesMax);
        });
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (string path in _settings.Settings.RecentFiles.Where(IsPersistableRecent).Take(RecentFilesMax))
            RecentFiles.Add(new RecentFileItemViewModel(path, OpenInputAsync));
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>
    /// A path is worth keeping as a recent if it still exists and is not a
    /// test/temporary artifact (the GUI test suite and preview workspace both
    /// live under the temp directory).
    /// </summary>
    private static bool IsPersistableRecent(string path)
        => !string.IsNullOrWhiteSpace(path)
           && File.Exists(path)
           && !path.StartsWith(Path.GetTempPath(), StringComparison.Ordinal);

    private static (int Width, int Height) FitInside(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight)
    {
        double scale = Math.Min(
            maxWidth / (double)Math.Max(1, sourceWidth),
            maxHeight / (double)Math.Max(1, sourceHeight));
        if (scale >= 1)
            return (sourceWidth, sourceHeight);
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
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