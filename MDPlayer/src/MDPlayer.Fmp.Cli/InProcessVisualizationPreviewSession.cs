using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Inspection;
using Fmp.Application.Preview;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Creates in-process preview sessions for the GUI, and for the CLI's
/// <c>preview</c> and <c>review</c> commands. The default constructor is the
/// GUI entry point (no CLI-specific configuration); the <see cref="RenderRuntimeOptions"/>
/// constructor is CLI-internal so tool paths (FMP.COM, Corrscope, FFmpeg) are
/// preserved where a standalone render executable exists.
/// </summary>
public sealed class InProcessVisualizationPreviewSessionFactory : IVisualizationPreviewSessionFactory
{
    private readonly RenderRuntimeOptions _runtime;

    public InProcessVisualizationPreviewSessionFactory()
        : this(new RenderRuntimeOptions())
    {
    }

    internal InProcessVisualizationPreviewSessionFactory(RenderRuntimeOptions runtime)
    {
        _runtime = runtime ?? new RenderRuntimeOptions();
    }

    public Task<IVisualizationPreviewSession> OpenAsync(
        string inputPath,
        CancellationToken cancellationToken)
        => OpenWithTimelineAsync(inputPath, null, cancellationToken);

    /// <summary>
    /// Opens a session optionally seeded from an existing captured timeline
    /// (<c>--timeline</c>). The captured timeline is written to the session
    /// workspace each capture. When <paramref name="timelineOutPath"/> is
    /// supplied it receives an extra durable copy (surviving the temporary
    /// session directory).
    /// </summary>
    internal async Task<IVisualizationPreviewSession> OpenWithTimelineAsync(
        string inputPath,
        string? seedTimelinePath,
        CancellationToken cancellationToken,
        string? timelineOutPath = null)
    {
        VisualizationInputInfo input = await VisualizationInputInspector.InspectAsync(
            inputPath, cancellationToken);
        return new InProcessVisualizationPreviewSession(input, _runtime, seedTimelinePath, timelineOutPath);
    }
}

/// <summary>
/// Reusable in-process preview session bound to one input. It owns a single
/// temporary workspace and caches the last capture, last prepared source and
/// last frame renderer. Capture invalidation is driven only by capture-affecting
/// settings; style/dimension changes rebuild layout or renderer without
/// recapturing.
/// </summary>
internal sealed class InProcessVisualizationPreviewSession : IVisualizationPreviewSession
{
    private readonly RenderRuntimeOptions _runtime;
    private readonly string _sessionRoot;
    private readonly VisualizationWorkspace _workspace;
    private readonly string? _seedTimelinePath;
    private readonly string? _timelineOutPath;

    private TimelineCaptureKey? _timelineCaptureKey;
    private CaptureContext? _timelineCapture;

    private PlanKey? _planKey;
    private PreparedPlanContext? _planCache;
    private FrameStyleKey? _timelineFrameStyleKey;
    private PreparedTimelineSource? _timelineSource;
    private VisualizationFrameRenderer? _timelineRenderer;

    private Task<PreparedCapture>? _renderAssetsTask;
    private PreparedCapture? _capture;

    private FrameStyleKey? _frameStyleKey;
    private PreparedVisualizationSource? _prepared;
    private VisualizationFrameRenderer? _interactiveRenderer;
    private VisualizationFrameRenderer? _productionRenderer;

    // ---- Instrumentation counters (internal, test-only seams) ----
    private int _timelineCaptureCount;
    private int _scopeAssetPreparationCount;
    private int _planBuildCount;
    private int _rendererConstructionCount;
    private int _frameRenderCount;

    private readonly CancellationTokenSource _sessionLifetimeCts = new();

    // Serializes capture-affecting work (semantic capture and scope/stem
    // generation) so an obsolete pass can never write into the workspace at the
    // same time as a new capture for a changed playback setting.
    private readonly SemaphoreSlim _captureWorkGate = new(1, 1);

    // The seekable WAV readers hold mutable stream positions and scratch
    // buffers, so a single interactive renderer must not render two frames
    // concurrently. Because interactive stills are cheap, it is preferable to
    // let the previous frame finish rather than duplicate every open stem
    // stream.
    private readonly SemaphoreSlim _frameRenderGate = new(1, 1);

    public InProcessVisualizationPreviewSession(
        VisualizationInputInfo input,
        RenderRuntimeOptions runtime,
        string? seedTimelinePath = null,
        string? timelineOutPath = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        _runtime = runtime ?? new RenderRuntimeOptions();
        _seedTimelinePath = seedTimelinePath;
        _timelineOutPath = timelineOutPath;

        _sessionRoot = Path.Combine(
            Path.GetTempPath(), "MDPlayer", "Preview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionRoot);

        string timelinePath = Path.Combine(_sessionRoot, "timeline.json");
        string audioDir = Path.Combine(_sessionRoot, "audio");
        string masterAudioPath = Path.Combine(audioDir, "master.wav");
        string scopeDir = Path.Combine(_sessionRoot, "scope");
        string scopeMetadataPath = Path.Combine(scopeDir, "metadata.json");
        string corrscopeConfigPath = Path.Combine(scopeDir, "corrscope-grid.yaml");
        string videoPath = Path.Combine(_sessionRoot, "preview.mp4");
        Directory.CreateDirectory(audioDir);
        Directory.CreateDirectory(scopeDir);

        _workspace = new VisualizationWorkspace(
            _sessionRoot,
            timelinePath,
            audioDir,
            masterAudioPath,
            scopeDir,
            scopeMetadataPath,
            corrscopeConfigPath,
            videoPath);

        Capabilities = new VisualizationSessionCapabilities
        {
            SemanticCapture = input.SupportsSemanticCapture,
            ScopeCapture = input.SupportsScopeCapture,
            AnalysisAvailable = input.SupportsAnalysis,
            Issues = input.Issues,
        };
    }

    public VisualizationInputInfo Input { get; }
    public VisualizationSessionCapabilities Capabilities { get; private set; }

    public async Task<VisualizationPlanResult> PlanAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateInput(request);
        PreparedTimelineSource source =
            await EnsureTimelineSourceAsync(request, cancellationToken);
        return source.Plan;
    }

    public async Task<PreviewFrameResult> RenderFrameAsync(
        VisualizationRequest request,
        PreviewFrameRequest preview,
        CancellationToken cancellationToken)
    {
        ValidateInput(request);

        VisualizationRequest previewRequest = WithPreviewDimensions(
            request, preview.Width, preview.Height);

        return preview.Fidelity switch
        {
            PreviewFidelity.Layout
                or PreviewFidelity.TimelineStill =>
                await RenderTimelineFrameAsync(
                    previewRequest,
                    preview,
                    cancellationToken),

            PreviewFidelity.InteractiveStill
                or PreviewFidelity.AccurateStill =>
                await RenderPreparedFrameAsync(
                    previewRequest,
                    preview,
                    cancellationToken),

            _ => throw new ArgumentOutOfRangeException(
                nameof(preview.Fidelity)),
        };
    }

    /// <summary>
    /// Timeline-path frame: built only from the captured timeline (plus the
    /// master waveform when the initial capture produced one). Never performs
    /// stem generation, energy analysis, Corrscope startup or full renderer
    /// construction, so it is the first frame the GUI can display.
    /// </summary>
    private async Task<PreviewFrameResult> RenderTimelineFrameAsync(
        VisualizationRequest request,
        PreviewFrameRequest preview,
        CancellationToken cancellationToken)
    {
        PreparedTimelineSource source =
            await EnsureTimelineSourceAsync(
                request,
                cancellationToken);

        _timelineRenderer ??=
            VisualizationFrameRendererFactory
                .CreateTimelinePreview(
                    source,
                    introOutro: true);

        long frameIndex = FrameIndexAt(
            preview.TimeSeconds,
            request.Output,
            _timelineRenderer.TotalFrames);

        byte[] rgba = preview.Fidelity == PreviewFidelity.Layout
            ? _timelineRenderer.RenderStaticFrame()
            : _timelineRenderer.RenderFrame(frameIndex);

        return BuildTimelineFrameResult(
            source,
            _timelineRenderer,
            preview,
            rgba);
    }

    /// <summary>
    /// Full-path frame: waits for the prepared scope/stem assets, then renders
    /// through the interactive (random-access) or production (Corrscope-path)
    /// renderer depending on fidelity.
    /// </summary>
    private async Task<PreviewFrameResult> RenderPreparedFrameAsync(
        VisualizationRequest request,
        PreviewFrameRequest preview,
        CancellationToken cancellationToken)
    {
        PreparedVisualizationSource prepared =
            await EnsurePreparedAsync(request, cancellationToken);

        VisualizationFrameRenderer renderer =
            EnsureRenderer(prepared, preview.Fidelity);

        long frameIndex = FrameIndexAt(
            preview.TimeSeconds,
            request.Output,
            renderer.TotalFrames);

        await _frameRenderGate.WaitAsync(cancellationToken);
        byte[] rgba;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            rgba = renderer.RenderFrame(frameIndex);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _frameRenderGate.Release();
        }

        return BuildPreparedFrameResult(
            prepared,
            renderer,
            preview,
            rgba);
    }

    /// <summary>
    /// Builds the frame result for the full prepared-path renderer. Applies the
    /// same approximation metadata as before (<see cref="DescribeApproximations"/>),
    /// plus surfaces a plan validation warning when present.
    /// </summary>
    private static PreviewFrameResult BuildPreparedFrameResult(
        PreparedVisualizationSource prepared,
        VisualizationFrameRenderer renderer,
        PreviewFrameRequest preview,
        byte[] rgba)
    {
        (bool approximatedScope, string[] approximationNotes) =
            DescribeApproximations(prepared, renderer, preview.Fidelity);

        return new PreviewFrameResult
        {
            Fidelity = preview.Fidelity,
            TimeSeconds = preview.TimeSeconds,
            Width = renderer.Width,
            Height = renderer.Height,
            PngBytes = PngFrameEncoder.Encode(
                renderer.Width,
                renderer.Height,
                rgba),
            ScopeKind = preview.Fidelity switch
            {
                PreviewFidelity.InteractiveStill => PreviewScopeKind.PerChannel,
                PreviewFidelity.AccurateStill => PreviewScopeKind.Corrscope,
                _ => PreviewScopeKind.None,
            },
            HasApproximations = approximatedScope,
            ApproximationNotes = approximationNotes,
            Warning =
                prepared.Plan.ValidationIssues
                    .FirstOrDefault(
                        issue =>
                            issue.Severity
                            == ValidationSeverity.Warning),
        };
    }

    /// <summary>
    /// Builds the frame result for a timeline-only frame. The approximation
    /// metadata reflects the incomplete-but-not-failed nature of a quick
    /// preview: channel scopes are still being prepared and channel-energy
    /// effects have not run yet. Semantic lanes are never flagged approximate.
    /// </summary>
    private static PreviewFrameResult BuildTimelineFrameResult(
        PreparedTimelineSource source,
        VisualizationFrameRenderer renderer,
        PreviewFrameRequest preview,
        byte[] rgba)
    {
        IReadOnlyList<string> notes =
            TimelineApproximationNotes(source);

        return new PreviewFrameResult
        {
            Fidelity = preview.Fidelity,
            TimeSeconds = preview.TimeSeconds,
            Width = renderer.Width,
            Height = renderer.Height,
            PngBytes = PngFrameEncoder.Encode(
                renderer.Width,
                renderer.Height,
                rgba),
            ScopeKind = source.MasterAudioProduced
                ? PreviewScopeKind.MasterFallback
                : PreviewScopeKind.None,
            HasApproximations = notes.Count > 0,
            ApproximationNotes = notes,
        };
    }

    /// <summary>
    /// Approximation notes for a timeline-only frame. The timeline, layout,
    /// event positions, colors, titles and track selection use the same
    /// production overlay implementation, so only scope and energy layers are
    /// described as approximate/pending.
    /// </summary>
    private static IReadOnlyList<string> TimelineApproximationNotes(
        PreparedTimelineSource source)
    {
        var notes = new List<string>();

        if (source.Layout.Geometry.HasScopes)
        {
            notes.Add(
                source.MasterAudioProduced
                    ? "Channel scopes are still being prepared; "
                      + "scope panels temporarily show the master waveform."
                    : "Channel scopes are still being prepared; "
                      + "scope panels are temporarily empty.");
        }

        if (source.Request.Style.Effects
            != VisualEffects.Off)
        {
            notes.Add(
                "Channel-energy effects will appear "
                + "when preview preparation completes.");
        }

        return notes;
    }

    /// <summary>
    /// Computes the approximation metadata for a delivered full-path frame.
    /// Interactive stills with live scopes are explicitly flagged as
    /// approximate (the scope triggering is derived from random-access channel
    /// waveforms, not Corrscope's stateful correlation). Production stills are
    /// never whole-frame approximate.
    /// </summary>
    private static (bool Approximated, string[] Notes) DescribeApproximations(
        PreparedVisualizationSource prepared,
        VisualizationFrameRenderer renderer,
        PreviewFidelity fidelity)
    {
        if (fidelity == PreviewFidelity.InteractiveStill
            && prepared.Scope.Enabled
            && prepared.Layout.Geometry.HasScopes
            && renderer.UsesApproximatedScopeSource)
        {
            var notes = new List<string>
            {
                "Interactive scope preview uses random-access channel waveforms "
                + "with local gain normalization. Final output uses Corrscope's "
                + "stateful correlation triggering.",
            };
            if (renderer.InteractiveScopeUnavailableChannelCount > 0)
                notes.Add("Some channel scope WAVs were unavailable; affected scope cells are empty.");
            return (true, notes.ToArray());
        }

        if (prepared.Scope.Enabled
            && prepared.Layout.Geometry.HasScopes
            && !renderer.HasScopeSource)
        {
            return (true, ["Scope source unavailable; scope regions render transparent."]);
        }

        return (false, Array.Empty<string>());
    }

    public async Task<MotionPreviewResult> RenderMotionAsync(
        VisualizationRequest request,
        MotionPreviewRequest preview,
        IProgress<PreviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateInput(request);

        (int width, int height) =
            FitInside(
                request.Output.Width,
                request.Output.Height,
                preview.MaxWidth,
                preview.MaxHeight);

        VisualizationRequest previewRequest = request with
        {
            Output = request.Output with
            {
                Width = width,
                Height = height,
            },
        };

        PreparedFrameContext context =
            await EnsureFrameContextAsync(previewRequest, PreviewFidelity.Motion, cancellationToken);

        int frameCount =
            checked((int)Math.Ceiling(
                preview.DurationSeconds
                * preview.Fps));

        if (frameCount <= 0)
            throw new InvalidOperationException("motion preview produced no frames");

        var pngFrames = new List<byte[]>(frameCount);
        var paths = new List<string>(frameCount);
        Directory.CreateDirectory(_sessionRoot);

        // Motion shares _productionRenderer with AccurateStill. Its sources
        // (Corrscope bridge / master waveform) are not safe to touch from two
        // threads, so serialize the whole render loop under the gate.
        await _frameRenderGate.WaitAsync(cancellationToken);
        try
        {
            for (int i = 0; i < frameCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double time =
                    preview.StartSeconds
                    + i / (double)preview.Fps;

                long sourceFrame =
                    FrameIndexAt(
                        time,
                        previewRequest.Output,
                        context.Renderer.TotalFrames);

                byte[] rgba =
                    context.Renderer.RenderFrame(sourceFrame);

                byte[] png =
                    PngFrameEncoder.Encode(
                        width,
                        height,
                        rgba);

                string fileName = $"frame-{i:D4}.png";
                string fullPath = Path.Combine(_sessionRoot, fileName);
                await File.WriteAllBytesAsync(fullPath, png, cancellationToken);
                paths.Add(fullPath);
                pngFrames.Add(png);

                progress?.Report(
                    new PreviewProgress(
                        "renderingFrames",
                        (i + 1) / (double)frameCount,
                        $"Rendered frame {i + 1}/{frameCount}"));
            }
        }
        finally
        {
            _frameRenderGate.Release();
        }

        return new MotionPreviewResult
        {
            FrameCount = frameCount,
            Fps = preview.Fps,
            Width = width,
            Height = height,
            FramesDirectory = _sessionRoot,
            FramePaths = paths,
        };
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the background scope/stem asset task so it cannot keep writing
        // into the workspace while we tear it down.
        _sessionLifetimeCts.Cancel();

        // Let any in-flight still/motion render finish before tearing down the
        // renderer streams and the gate itself; otherwise a render already
        // inside the gate could touch a disposed reader.
        try
        {
            await _frameRenderGate.WaitAsync();
            _frameRenderGate.Release();
        }
        catch
        {
            // Gate was already disposed / cancelled: proceed best-effort.
        }

        try
        {
            _renderAssetsTask?.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // Best effort; the lifetime token already cancelled any running pass.
        }
        finally
        {
            _renderAssetsTask?.ContinueWith(
                t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
        }

        try
        {
            DisposeAllRenderers();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _frameRenderGate.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _captureWorkGate.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _sessionLifetimeCts.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (Directory.Exists(_sessionRoot))
                Directory.Delete(_sessionRoot, recursive: true);
        }
        catch
        {
            // Temporary preview workspace is diagnostic-only.
        }
    }

    // ---- capture/prepare caching ------------------------------------------

    private void ValidateInput(VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(
                Path.GetFullPath(request.InputPath),
                Path.GetFullPath(Input.FullPath),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "preview session is bound to a different input.");
        }
    }

    private static VisualizationRequest WithPreviewDimensions(
        VisualizationRequest request,
        int? width,
        int? height)
    {
        if (width is null && height is null)
            return request;

        return request with
        {
            Output = request.Output with
            {
                Width = width ?? request.Output.Width,
                Height = height ?? request.Output.Height,
            },
        };
    }

    private static (int Width, int Height) FitInside(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            throw new ArgumentException(
                "--max-width and --max-height must be positive.");
        }
        double scale = Math.Min(
            maxWidth / (double)Math.Max(1, sourceWidth),
            maxHeight / (double)Math.Max(1, sourceHeight));
        scale = Math.Max(1.0 / 16.0, Math.Min(1.0, scale));
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }

    private static long FrameIndexAt(
        double timeSeconds,
        OutputSettings output,
        long totalFrames)
    {
        long frameIndex = (long)Math.Round(
            timeSeconds * output.FpsNumerator /
            (double)output.FpsDenominator);
        return Math.Clamp(frameIndex, 0, Math.Max(0, totalFrames - 1));
    }

    private async Task<PreparedFrameContext> EnsureFrameContextAsync(
        VisualizationRequest request,
        PreviewFidelity fidelity,
        CancellationToken cancellationToken)
    {
        PreparedVisualizationSource prepared =
            await EnsurePreparedAsync(request, cancellationToken);

        VisualizationFrameRenderer renderer = EnsureRenderer(prepared, fidelity);

        return new PreparedFrameContext(prepared, renderer);
    }

    /// <summary>
    /// Returns the renderer for a prepared fidelity, constructing the
    /// interactive (random-access, fully in-process) or production
    /// (Corrscope-path) renderer lazily and keeping both cached across frames.
    /// Motion preview and accurate stills use the production renderer so they
    /// stay byte-identical to final video.
    /// </summary>
    private VisualizationFrameRenderer EnsureRenderer(
        PreparedVisualizationSource prepared,
        PreviewFidelity fidelity)
    {
        switch (fidelity)
        {
            case PreviewFidelity.InteractiveStill:
                return _interactiveRenderer ??=
                    VisualizationFrameRendererFactory.Create(
                        prepared,
                        _workspace,
                        _runtime,
                        introOutro: true,
                        ScopeFrameSourcePolicy.Interactive);

            case PreviewFidelity.AccurateStill:
            case PreviewFidelity.Motion:
                return _productionRenderer ??=
                    VisualizationFrameRendererFactory.Create(
                        prepared,
                        _workspace,
                        _runtime,
                        introOutro: true,
                        ScopeFrameSourcePolicy.Production);

            default:
                throw new ArgumentOutOfRangeException(nameof(fidelity));
        }
    }

    private void DisposeFullRenderers()
    {
        _interactiveRenderer?.Dispose();
        _interactiveRenderer = null;

        _productionRenderer?.Dispose();
        _productionRenderer = null;

        _timelineRenderer?.Dispose();
        _timelineRenderer = null;
    }

    private void DisposeAllRenderers() => DisposeFullRenderers();

    /// <summary>
    /// Returns the timeline capture context for the request's capture key,
    /// performing backend resolution, FMP track preparation and semantic
    /// capture once per capture key and retaining the backend resolution and
    /// prepared FMP track so the later scope/stem stage can reuse them.
    /// </summary>
    private async Task<CaptureContext> EnsureTimelineCaptureAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        TimelineCaptureKey key = KeyFor(request);

        if (_timelineCapture is not null && _timelineCaptureKey == key)
        {
            return _timelineCapture;
        }

        InvalidateAllPreviewState();

        CaptureContext context = await Task.Run(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _captureWorkGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Interlocked.Increment(ref _timelineCaptureCount);

                    VisualizationBackendResolution resolution =
                        VisualizationBackendResolver.Resolve(request, _runtime);

                    PreparedTrack? track = null;

                    if (string.Equals(
                            resolution.Backend.Id,
                            "fmp",
                            StringComparison.Ordinal))
                    {
                        track = TrackPreparation.Prepare(
                            request.InputPath,
                            resolution.FmpComPath,
                            _runtime.AssetsDir,
                            resolution.SearchPaths);
                    }

                    PreparedTimeline timeline =
                        VisualizationPrepareCoordinator.CaptureTimeline(
                            request,
                            _runtime,
                            _workspace,
                            resolution,
                            track,
                            _seedTimelinePath,
                            _timelineOutPath);

                    return new CaptureContext(
                        timeline,
                        resolution,
                        track);
                }
                finally
                {
                    _captureWorkGate.Release();
                }
            },
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        _timelineCaptureKey = key;
        _timelineCapture = context;

        Capabilities = Capabilities with
        {
            HasCapturedTimeline = true,
        };

        return context;
    }

    /// <summary>Builds the staged timeline-capture key from a live input file.</summary>
    private TimelineCaptureKey KeyFor(VisualizationRequest request)
    {
        string fullPath = Path.GetFullPath(request.InputPath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new VisualizationRequestException(
                $"Capture input no longer exists: {request.InputPath}");
        }
        return TimelineCaptureKey.From(request, file, _runtime);
    }

    /// <summary>
    /// Returns a lightweight timeline-only source for the request's frame-style
    /// key, rebuilding it (without recapturing the file) whenever the frame
    /// style changes. When only style/presentation changed the previously
    /// computed plan is reused, so a title/style edit does not re-plan.
    /// </summary>
    private async Task<PreparedTimelineSource> EnsureTimelineSourceAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        CaptureContext context =
            await EnsureTimelineCaptureAsync(
                request,
                cancellationToken);

        FrameStyleKey frameKey =
            FrameStyleKey.From(
                request,
                PlanKey.From(request, ScopeAssetKey.From(request, _timelineCaptureKey!.Value)));

        if (_timelineSource is null
            || _timelineFrameStyleKey != frameKey)
        {
            _timelineRenderer?.Dispose();
            _timelineRenderer = null;

            PreparedPlanContext plan =
                await EnsurePlanContextAsync(
                    context,
                    request,
                    cancellationToken);

            _timelineSource =
                VisualizationPrepareCoordinator
                    .BuildTimelineSourceFrom(
                        context.Timeline,
                        request,
                        _workspace,
                        plan);

            _timelineFrameStyleKey = frameKey;
        }

        return _timelineSource;
    }

    /// <summary>
    /// Returns (and caches by <c>PlanKey</c>) the resolved layout and prepared
    /// plan for a request. A pure frame-style change (title, palette, effects,
    /// note color) reuses this cached plan, so it never re-runs the expensive
    /// <see cref="VisualizationPlanBuilder"/>.
    /// </summary>
    private async Task<PreparedPlanContext> EnsurePlanContextAsync(
        CaptureContext context,
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PlanKey planKey = PlanKey.From(
            request,
            ScopeAssetKey.From(request, _timelineCaptureKey!.Value));

        if (_planCache is not null && _planKey == planKey)
            return _planCache;

        _planKey = planKey;

        Interlocked.Increment(ref _planBuildCount);
        var planContext =
            VisualizationPrepareCoordinator.BuildPlanContext(
                context.Timeline.Timeline,
                request,
                _workspace);

        _planCache = planContext;
        return planContext;
    }

    /// <summary>
    /// Starts at most one heavy scope/stem generation task per capture key and
    /// returns it. Caller cancellation (via <c>WaitAsync</c>) must cancel only
    /// that caller's wait, never the underlying stem task, so repeated seeks or
    /// render-only changes cannot abort preparation; the underlying task is
    /// cancelled only when the capture key changes or the session is disposed.
    /// </summary>
    private Task<PreparedCapture> EnsureRenderAssetsTask(
        VisualizationRequest request,
        CaptureContext context)
    {
        if (_renderAssetsTask is not null)
            return _renderAssetsTask;

        Interlocked.Increment(ref _scopeAssetPreparationCount);

        Task<PreparedCapture> task = Task.Run(
            async () =>
            {
                await _captureWorkGate.WaitAsync(_sessionLifetimeCts.Token);
                try
                {
                    _sessionLifetimeCts.Token.ThrowIfCancellationRequested();

                    PreparedCapture result =
                        VisualizationPrepareCoordinator.CompleteCapture(
                            context.Timeline,
                            request,
                            _runtime,
                            _workspace,
                            context.Resolution,
                            context.PreparedTrack);

                    return result;
                }
                finally
                {
                    _captureWorkGate.Release();
                }
            },
            _sessionLifetimeCts.Token);

        _renderAssetsTask = task;
        return task;
    }

    /// <summary>
    /// Disposes all renderers and clears both lightweight and full source
    /// caches because the capture (and therefore every derived source) changed.
    /// </summary>
    private void InvalidateAllPreviewState()
    {
        _timelineRenderer?.Dispose();
        _timelineRenderer = null;
        _timelineSource = null;
        _timelineFrameStyleKey = null;

        DisposeFullRenderers();
        _prepared = null;
        _frameStyleKey = null;

        _planKey = null;
        _planCache = null;

        _capture = null;
        _renderAssetsTask = null;
    }

    /// <summary>
    /// Returns the full prepared source, waiting (but never cancelling) the
    /// deduplicated scope/stem asset task, then projecting the finished capture
    /// into a request-specific prepared source when the frame-style key changes.
    /// </summary>
    private async Task<PreparedVisualizationSource> EnsurePreparedAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        CaptureContext timelineContext =
            await EnsureTimelineCaptureAsync(
                request,
                cancellationToken);

        Task<PreparedCapture> task =
            EnsureRenderAssetsTask(
                request,
                timelineContext);

        PreparedCapture capture =
            await task.WaitAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (_timelineCaptureKey != KeyFor(request))
        {
            throw new OperationCanceledException(
                "Preview capture was superseded.");
        }

        _capture = capture;

        FrameStyleKey frameKey =
            FrameStyleKey.From(
                request,
                PlanKey.From(request, ScopeAssetKey.From(request, _timelineCaptureKey!.Value)));

        if (_prepared is null
            || _frameStyleKey != frameKey)
        {
            DisposeFullRenderers();

            PreparedPlanContext plan =
                await EnsurePlanContextAsync(
                    timelineContext,
                    request,
                    cancellationToken);

            _prepared =
                VisualizationPrepareCoordinator.BuildSourceFrom(
                    capture,
                    request,
                    _workspace,
                    plan);

            _frameStyleKey = frameKey;
        }

        return _prepared!;
    }

    // ---- keys --------------------------------------------------------------

    private sealed record PreparedFrameContext(
        PreparedVisualizationSource Prepared,
        VisualizationFrameRenderer Renderer);

    private sealed record CaptureContext(
        PreparedTimeline Timeline,
        VisualizationBackendResolution Resolution,
        PreparedTrack? PreparedTrack);
}