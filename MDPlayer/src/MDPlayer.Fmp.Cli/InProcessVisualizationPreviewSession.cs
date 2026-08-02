using Fmp.Application.Contracts;
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

    private CaptureKey? _captureKey;
    private PreparedCapture? _capture;
    private RenderKey? _renderKey;
    private PreparedVisualizationSource? _prepared;
    private VisualizationFrameRenderer? _interactiveRenderer;
    private VisualizationFrameRenderer? _productionRenderer;

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
        PreparedVisualizationSource source =
            await EnsurePreparedAsync(request, cancellationToken);
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

        PreparedFrameContext context =
            await EnsureFrameContextAsync(previewRequest, preview.Fidelity, cancellationToken);

        long frameIndex = FrameIndexAt(
            preview.TimeSeconds,
            previewRequest.Output,
            context.Renderer.TotalFrames);

        // Interactive still rendering is gated so a single renderer's shared
        // stream positions and scratch buffers are never touched concurrently.
        // Patch-1-style obsolescence already prevents stale frames from
        // reaching the UI; the gate protects the renderer internals themselves.
        byte[] rgba;
        switch (preview.Fidelity)
        {
            case PreviewFidelity.Layout:
                rgba = context.Renderer.RenderStaticFrame();
                break;

            case PreviewFidelity.InteractiveStill:
            case PreviewFidelity.AccurateStill:
                await _frameRenderGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rgba = context.Renderer.RenderFrame(frameIndex);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    _frameRenderGate.Release();
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(preview.Fidelity));
        }

        (bool approximatedScope, string[] approximationNotes) =
            DescribeApproximations(context, preview.Fidelity);

        return new PreviewFrameResult
        {
            Fidelity = preview.Fidelity,
            TimeSeconds = preview.TimeSeconds,
            Width = context.Renderer.Width,
            Height = context.Renderer.Height,
            PngBytes = PngFrameEncoder.Encode(
                context.Renderer.Width,
                context.Renderer.Height,
                rgba),
            HasApproximations = approximatedScope,
            ApproximationNotes = approximationNotes,
            Warning =
                context.Prepared.Plan.ValidationIssues
                    .FirstOrDefault(
                        issue =>
                            issue.Severity
                            == ValidationSeverity.Warning),
        };
    }

    /// <summary>
    /// Computes the approximation metadata for a delivered frame. Interactive
    /// stills with live scopes are explicitly flagged as approximate (the
    /// scope triggering is derived from random-access channel waveforms, not
    /// Corrscope's stateful correlation). Layout previews and production
    /// stills are never whole-frame approximate.
    /// </summary>
    private static (bool Approximated, string[] Notes) DescribeApproximations(
        PreparedFrameContext context,
        PreviewFidelity fidelity)
    {
        if (fidelity == PreviewFidelity.Layout)
        {
            return (true, ["Static layout preview; dynamic scopes and events are omitted."]);
        }

        if (fidelity == PreviewFidelity.InteractiveStill
            && context.Prepared.Scope.Enabled
            && context.Prepared.Layout.Geometry.HasScopes
            && context.Renderer.UsesApproximatedScopeSource)
        {
            var notes = new List<string>
            {
                "Interactive scope preview uses random-access channel waveforms "
                + "with local gain normalization. Final output uses Corrscope's "
                + "stateful correlation triggering.",
            };
            if (context.Renderer.InteractiveScopeUnavailableChannelCount > 0)
                notes.Add("Some channel scope WAVs were unavailable; affected scope cells are empty.");
            return (true, notes.ToArray());
        }

        if (context.Prepared.Scope.Enabled
            && context.Prepared.Layout.Geometry.HasScopes
            && !context.Renderer.HasScopeSource)
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
    /// Returns the renderer for a fidelity, constructing the interactive
    /// (random-access, fully in-process) or production (Corrscope-path) renderer
    /// lazily and keeping both cached across frames. Layout and interactive
    /// stills share the in-process renderer (never launching Corrscope for a
    /// static or quick scrub); motion preview and accurate stills use the
    /// production renderer so they stay byte-identical to final video.
    /// </summary>
    private VisualizationFrameRenderer EnsureRenderer(
        PreparedVisualizationSource prepared,
        PreviewFidelity fidelity)
    {
        switch (fidelity)
        {
            case PreviewFidelity.Layout:
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

    private void DisposeAllRenderers()
    {
        _interactiveRenderer?.Dispose();
        _interactiveRenderer = null;

        _productionRenderer?.Dispose();
        _productionRenderer = null;
    }

    private async Task<PreparedVisualizationSource> EnsurePreparedAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CaptureKey captureKey = CaptureKey.From(request, _runtime);
        RenderKey renderKey = RenderKey.From(request);

        // Capture is reused until a capture-affecting setting changes.
        if (_capture is null || _captureKey != captureKey)
        {
            DisposeAllRenderers();
            _prepared = null;
            _renderKey = null;

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                VisualizationBackendResolution resolution =
                    VisualizationBackendResolver.Resolve(request, _runtime);

                PreparedTrack? track = null;
                if (string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal))
                {
                    track = TrackPreparation.Prepare(
                        request.InputPath, resolution.FmpComPath,
                        _runtime.AssetsDir, resolution.SearchPaths);
                }

                _capture = VisualizationPrepareCoordinator.Capture(
                    request,
                    _runtime,
                    _workspace,
                    resolution,
                    track,
                    seedTimelinePath: _seedTimelinePath,
                    timelineOutPath: _timelineOutPath);
                Capabilities = Capabilities with { HasCapturedTimeline = true };
            }, cancellationToken);

            _captureKey = captureKey;
        }

        // Layout/plan/energy/presentation depend on the full render request.
        // Rebuild the projection for each distinct render key, reusing capture.
        if (_prepared is null || _renderKey != renderKey)
        {
            _prepared = VisualizationPrepareCoordinator.BuildSource(
                _capture!, request, _workspace);
            _renderKey = renderKey;
            DisposeAllRenderers();
        }

        return _prepared!;
    }

    // ---- keys --------------------------------------------------------------

    private sealed record PreparedFrameContext(
        PreparedVisualizationSource Prepared,
        VisualizationFrameRenderer Renderer);
}