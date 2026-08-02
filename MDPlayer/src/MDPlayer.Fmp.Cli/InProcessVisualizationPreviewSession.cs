using Fmp.Application.Contracts;
using Fmp.Application.Inspection;
using Fmp.Application.Preview;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Creates in-process preview sessions for the CLI's <c>preview</c> and
/// <c>review</c> commands. Binds the session to a CLI <see cref="RenderRuntimeOptions"/>
/// so tool paths (FMP.COM, Corrscope, FFmpeg) are preserved.
/// </summary>
internal sealed class InProcessVisualizationPreviewSessionFactory : IVisualizationPreviewSessionFactory
{
    private readonly RenderRuntimeOptions _runtime;

    public InProcessVisualizationPreviewSessionFactory(RenderRuntimeOptions runtime)
    {
        _runtime = runtime ?? new RenderRuntimeOptions();
    }

    public async Task<IVisualizationPreviewSession> OpenAsync(
        string inputPath,
        CancellationToken cancellationToken)
        => await OpenWithTimelineAsync(inputPath, null, cancellationToken);

    /// <summary>
    /// Opens a session optionally seeded from an existing captured timeline
    /// (<c>--timeline</c>). The captured timeline is written to the session
    /// workspace each capture. When <paramref name="timelineOutPath"/> is
    /// supplied it receives an extra durable copy (surviving the temporary
    /// session directory).
    /// </summary>
    public async Task<IVisualizationPreviewSession> OpenWithTimelineAsync(
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
    private VisualizationFrameRenderer? _renderer;

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
            await EnsureFrameContextAsync(previewRequest, cancellationToken);

        long frameIndex = FrameIndexAt(
            preview.TimeSeconds,
            previewRequest.Output,
            context.Renderer.TotalFrames);

        byte[] rgba = preview.Fidelity switch
        {
            PreviewFidelity.Layout =>
                context.Renderer.RenderStaticFrame(),

            PreviewFidelity.AccurateStill =>
                context.Renderer.RenderFrame(frameIndex),

            _ => throw new ArgumentOutOfRangeException(nameof(preview.Fidelity)),
        };

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
            HasApproximations =
                preview.Fidelity == PreviewFidelity.Layout
                || (context.Prepared.Scope.Enabled
                    && context.Prepared.Layout.Geometry.HasScopes
                    && !context.Renderer.HasScopeSource),
            ApproximationNotes =
                preview.Fidelity == PreviewFidelity.Layout
                    ? ["Static layout preview; dynamic scopes and events are omitted."]
                    : (context.Prepared.Scope.Enabled
                        && context.Prepared.Layout.Geometry.HasScopes
                        && !context.Renderer.HasScopeSource)
                        ? ["Scope source unavailable; scope regions render transparent."]
                        : Array.Empty<string>(),
            Warning =
                context.Prepared.Plan.ValidationIssues
                    .FirstOrDefault(
                        issue =>
                            issue.Severity
                            == ValidationSeverity.Warning),
        };
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
            await EnsureFrameContextAsync(previewRequest, cancellationToken);

        int frameCount =
            checked((int)Math.Ceiling(
                preview.DurationSeconds
                * preview.Fps));

        if (frameCount <= 0)
            throw new InvalidOperationException("motion preview produced no frames");

        var pngFrames = new List<byte[]>(frameCount);
        var paths = new List<string>(frameCount);
        Directory.CreateDirectory(_sessionRoot);

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

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_renderer is not null)
            {
                _renderer.Dispose();
                _renderer = null;
            }
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
        return ValueTask.CompletedTask;
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
        CancellationToken cancellationToken)
    {
        PreparedVisualizationSource prepared =
            await EnsurePreparedAsync(request, cancellationToken);

        if (_renderer is null)
        {
            _renderer = VisualizationFrameRendererFactory.Create(
                prepared,
                _workspace,
                _runtime,
                introOutro: true);
        }

        return new PreparedFrameContext(prepared, _renderer!);
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
            if (_renderer is not null)
            {
                _renderer.Dispose();
                _renderer = null;
            }
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
            if (_renderer is not null)
            {
                _renderer.Dispose();
                _renderer = null;
            }
        }

        return _prepared!;
    }

    // ---- keys --------------------------------------------------------------

    private sealed record PreparedFrameContext(
        PreparedVisualizationSource Prepared,
        VisualizationFrameRenderer Renderer);
}