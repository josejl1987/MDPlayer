using System.Diagnostics;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Core.Analysis;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
// The CLI uses the Core enum types for the render pipeline; the Application
// contracts import colliding names. Pin the Core types explicitly.
using VisualizationPreset = Fmp.Core.Visualization.Rendering.VisualizationPreset;
using VideoEncoder = Fmp.Core.Visualization.Rendering.VideoEncoder;

namespace Fmp.Cli;

/// <summary>
/// Generic register-log visualization entry point. Register-log backends use a
/// topology-driven overlay and the deterministic master-scope fallback when
/// isolated channel stems are not available.
/// </summary>
internal static class VgmVisualizeCommand
{
    // Source-compatible parser seam retained for callers and tests that use
    // the generic command directly. The shared parser is now the single CLI
    // option implementation used by both backend paths.
    internal static VisualizeOptions Parse(string[] args)
        => VisualizeOptionsParser.ParseStrict(args);

    internal static int Handle(
        VisualizeOptions options,
        VisualizationBackendResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolution);

        FileInfo input = resolution.Input;
        IPlaybackBackend backend = resolution.Backend;
        PlaybackProbeResult probe = resolution.Probe;

        VisualizationWorkspace workspace = VisualizationWorkspace.Create(options, input);
        if (!options.Overwrite && workspace.HasConflict(!options.StemsOnly))
        {
            Console.Error.WriteLine(
                $"error: output exists: {workspace.FirstConflict(!options.StemsOnly)} (use --overwrite)");
            return 9;
        }
        workspace.EnsureDirectories();
        string timelinePath = workspace.TimelinePath;
        string audioPath = workspace.MasterAudioPath;
        string videoPath = workspace.VideoPath;
        string scopeDir = workspace.ScopeDir;

        int timelineSampleRate = probe.NativeSampleRate > 0
            ? probe.NativeSampleRate
            : options.SampleRate;
        var eventSink = new TimelineDecoderEventSink(timelineSampleRate);
        var totalWatch = Stopwatch.StartNew();
        double captureSeconds = 0;
        double stemRenderSeconds = 0;
        double compositionSeconds = 0;
        SinglePassComposer composer = null;
        VideoEncoder requestedEncoder = options.Encoder;
        var encoderFallback = new VisualizationSupport.EncoderFallbackState();
        ProgressJsonlWriter? progress = ProgressJsonlWriter.CreateIfRequested(options);
        try
        {
            progress?.Started();
            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline));

            Stopwatch captureWatch = Stopwatch.StartNew();
            using IPlaybackCaptureSession session = backend.Open(
                input,
                new PlaybackOptions(
                    options.Loops,
                    options.Fade,
                    options.Tail,
                    options.MaxDuration,
                    audioPath,
                    options.SampleRate,
                    options.SpcStems,
                    options.SpcPitchMode,
                    options.SsgGainDb),
                eventSink);
            session.Run();
            captureWatch.Stop();
            captureSeconds = captureWatch.Elapsed.TotalSeconds;
            progress?.StageCompleted(
                ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline), captureSeconds);

            VisualizationTimeline timeline = eventSink.Complete(
                session.SamplePosition,
                "completed",
                            new TrackMetadata(
                                input.Extension.TrimStart('.').ToLowerInvariant(),
                                Path.GetFileNameWithoutExtension(input.Name),
                                backend.Id,
                                input.Name));
            if (!VisualizationContentAvailability.HasRenderableContent(timeline))
            {
                Console.Error.WriteLine(
                    "error: visualization capture contains neither semantic events nor waveform activity");
                return 10;
            }
            VisualizationJsonWriter.Write(timelinePath, timeline);

            AnalysisOutput analysisOutput = null;
            if (options.Analysis)
            {
                progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.RunningAnalysis));
                AnalysisExecutionResult analysis = AnalysisRunner.Execute(new AnalyzeOptions
                {
                    CapturedTimeline = timeline,
                    AnalysisPython = options.AnalysisPython,
                    AnalysisOutput = options.AnalysisOutput ?? Path.Combine(workspace.OutputDir, "analysis"),
                    AnalysisCache = options.AnalysisCache,
                    Detail = options.AnalysisDetail,
                    Force = options.AnalysisForce,
                    TimeoutMinutes = options.AnalysisTimeoutMinutes,
                });
                if (analysis.ExitCode != 0)
                {
                    progress?.Failed(
                        $"analysis failed (exit {analysis.ExitCode})",
                        ValidationCodes.AnalysisFailed, analysis.ExitCode);
                    return analysis.ExitCode;
                }
                analysisOutput = analysis.Output;
                progress?.StageCompleted(
                    ProgressJsonlWriter.StageName(ExportStage.RunningAnalysis), null);
            }

            VisualizationLayoutMode layoutMode = VisualizationLayoutModeResolver.Resolve(
                timeline, options.LayoutMode);
            VisualizationTopology topology = VisualizationTopologyBuilder.Build(
                timeline, layoutMode, options.Channels, options.GroupBy);
            OverlayLayout layout = new(
                options.Width,
                options.Height,
                options.PastSeconds,
                options.FutureSeconds,
                topology.Panels.Count,
                layoutMode,
                options.ScopeHeight,
                options.TimelineHeight,
                options.RollZoom,
                options.ScopeRatio,
                options.ScopePosition);
            VisualizationLayoutValidator.Validate(layout, topology);
            if (!string.IsNullOrWhiteSpace(options.PreviewHtmlPath))
            {
                VisualizationPreviewWriter.Write(
                    options.PreviewHtmlPath,
                    timeline,
                    VisualizationLayoutPlan.Create(
                        timeline,
                        topology,
                        layout,
                        options.LayoutMode,
                        options.Channels,
                        options.GroupBy,
                        options.TimeGrid,
                        options.ScopePosition,
                        options.ScopeRatio,
                        options.Fps,
                        options.FpsDenominator,
                        options.Encoder.ToString(),
                        options.Analysis ? "requested" : "none"));
            }
            if (!string.IsNullOrWhiteSpace(options.DiagnosticPagesPath))
            {
                VisualizationTopology diagnosticTopology = VisualizationTopologyBuilder.Build(
                    timeline,
                    VisualizationLayoutMode.Diagnostic,
                    VisualizationChannelFilter.All,
                    options.GroupBy);
                OverlayLayout diagnosticLayout = new(
                    options.Width,
                    options.Height,
                    options.PastSeconds,
                    options.FutureSeconds,
                    diagnosticTopology.Panels.Count,
                    VisualizationLayoutMode.Diagnostic,
                    options.ScopeHeight,
                    options.TimelineHeight,
                    options.RollZoom,
                    options.ScopeRatio,
                    options.ScopePosition);
                VisualizationLayoutValidator.Validate(diagnosticLayout, diagnosticTopology);
                VisualizationDiagnosticPagesWriter.Write(
                    options.DiagnosticPagesPath,
                    timeline,
                    VisualizationLayoutPlan.Create(
                        timeline,
                        diagnosticTopology,
                        diagnosticLayout,
                        VisualizationLayoutMode.Diagnostic,
                        VisualizationChannelFilter.All,
                        options.GroupBy,
                        options.TimeGrid,
                        options.ScopePosition,
                        options.ScopeRatio,
                        options.Fps,
                        options.FpsDenominator,
                        options.Encoder.ToString(),
                        options.Analysis ? "requested" : "none"));
            }
            VisualizationPlanOutput.Emit(
                timeline,
                topology,
                layout,
                options.LayoutMode,
                options.Channels,
                options.GroupBy,
                options.TimeGrid,
                options.ScopePosition,
                options.ScopeRatio,
                options.Fps,
                options.FpsDenominator,
                options.Encoder.ToString(),
                options.Analysis ? "requested" : "none",
                options.PrintLayout,
                options.LayoutJson);
            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.RenderingChannelStems));
            Stopwatch stemWatch = Stopwatch.StartNew();
            GenericScopeArtifacts scopeArtifacts;
            try
            {
                scopeArtifacts = VisualizationScopeCoordinator.Render(
                    backend.Id,
                    input,
                    workspace,
                    options,
                    timeline.Devices,
                    timeline.Voices,
                    session.SamplePosition);
            }
            catch (VisualizationScopeException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ex.ExitCode;
            }
            stemWatch.Stop();
            stemRenderSeconds = stemWatch.Elapsed.TotalSeconds;
            progress?.StageCompleted(
                ProgressJsonlWriter.StageName(ExportStage.RenderingChannelStems), stemRenderSeconds);

            StemPlan scopePlan = scopeArtifacts.Plan;
            ScopeRenderer.ScopeResult scopeResult = scopeArtifacts.Result;
            bool hasRealStems = scopeArtifacts.HasIsolatedStems;
            string yamlPath = workspace.CorrscopeConfigPath;
            bool useCorrscope = scopeArtifacts.Enabled && (options.StemsOnly || layout.HasScopes);
            if (!hasRealStems && scopeArtifacts.Enabled)
                scopeResult = ExpandMasterToPanels(input, workspace, options, scopeResult, layout.PanelCount);
            if (useCorrscope)
            {
                CorrscopeConfigWriter.Write(yamlPath, scopeDir, scopeResult,
                    audioDir: "../audio",
                    overrides: new CorrscopeOverrides
                    {
                        Fps = options.Fps,
                        TriggerMs = 20,
                        RenderMs = 12,
                        EdgeStrength = 0.35,
                        BufferStrength = 1.0,
                        Responsiveness = 0.25,
                        BufferFalloff = 0.35,
                        ResetBelow = 0.2,
                        RenderWidth = layout.CorrscopeGridWidth,
                        RenderHeight = Math.Max(1, layout.CorrscopeGridHeight),
                        LayoutNCols = layout.ColumnCount,
                        IncludeMasterAsChannel = !hasRealStems,
                        IncludeSilentChannels = options.Channels == VisualizationChannelFilter.All,
                        HideLabels = true,
                        ResDivisor = 1.0,
                        Antialiasing = options.FinalQuality,
                    });
            }

            if (options.StemsOnly)
            {
                VisualizationPlanOutput.Emit(
                    timeline,
                    topology,
                    layout,
                    options.LayoutMode,
                    options.Channels,
                    options.GroupBy,
                    options.TimeGrid,
                    options.ScopePosition,
                    options.ScopeRatio,
                    options.Fps,
                    options.FpsDenominator,
                    "not-rendered",
                    options.Analysis ? "requested" : "none",
                    options.PrintLayout,
                    options.LayoutJson);
                if (!options.Quiet)
                    Console.Error.WriteLine($"stems: {scopeDir}");
                progress?.OutputCreated(scopeDir);
                progress?.Completed(scopeDir, totalWatch.Elapsed.TotalSeconds);
                return 0;
            }

            CorrscopeRunner corrRunner = useCorrscope
                ? new CorrscopeRunner(
                    options.ExternalToolTimeoutMinutes,
                    options.CorrscopePath)
                : null;

            if (!options.StemsOnly)
            {
                var panelRenderer = new PanelOverlayRenderer(
                    timeline,
                    new PanelOverlayRenderer.Options
                    {
                        Width = options.Width,
                        Height = options.Height,
                        FpsNumerator = options.Fps,
                        FpsDenominator = options.FpsDenominator,
                        PastSeconds = options.PastSeconds,
                        FutureSeconds = options.FutureSeconds,
                        RollZoom = options.RollZoom,
                        ScopeHeight = options.ScopeHeight,
                        TimelineHeight = options.TimelineHeight,
                        ScopeRatio = options.ScopeRatio,
                        ScopePosition = options.ScopePosition,
                        Channels = options.Channels,
                        GroupBy = options.GroupBy,
                        TimeGrid = options.TimeGrid,
                        Presentation = new VisualizationPresentation(
                            string.IsNullOrWhiteSpace(options.Title)
                                ? Path.GetFileNameWithoutExtension(input.Name)
                                : options.Title.Trim(),
                            options.Subtitle ?? "",
                            options.Credits ?? ""),
                        FontPath = options.FontPath,
                        PreferAntialiasedText = options.Preset != VisualizationPreset.Diagnostic,
                        Effects = options.Effects,
                        NoteColor = options.NoteColor,
                        Palette = options.Palette,
                        MotionBlurSamples = options.MotionBlurSamples,
                        LayoutMode = layoutMode,
                        AnalysisOverlay = analysisOutput is null
                            || options.AnalysisOverlay == "none"
                            ? AnalysisOverlayScene.Empty
                            : AnalysisOverlaySceneBuilder.Build(
                                analysisOutput,
                                timeline,
                                allowTentative: options.AnalysisOverlay is "standard" or "full"),
                    });
                composer = new SinglePassComposer(
                    options.FfmpegPath,
                    new SinglePassComposer.Options
                    {
                        TimeoutMinutes = options.ExternalToolTimeoutMinutes,
                        VideoPreset = options.FinalQuality ? "veryfast" : "ultrafast",
                        VideoCrf = options.FinalQuality ? "18" : "20",
                        Encoder = options.Encoder,
                    });
                if (!composer.IsAvailable)
                {
                    progress?.Failed("ffmpeg not found", ValidationCodes.FfmpegNotFound, 4);
                    Console.Error.WriteLine("error: ffmpeg not found (install FFmpeg or pass --ffmpeg PATH)");
                    return 4;
                }
                EncoderProbeResult nvencProbe = options.Encoder == VideoEncoder.Nvenc
                    ? new FfmpegVideoEncoderProbe(composer.FfmpegPath,
                        TimeSpan.FromMinutes(options.ExternalToolTimeoutMinutes)).Probe(VideoEncoder.Nvenc)
                    : null;
                if (nvencProbe is { Supported: false })
                {
                    progress?.Failed(
                        $"--encoder nvenc failed: {nvencProbe.Diagnostics}",
                        ValidationCodes.EncoderUnavailable, 4);
                    Console.Error.WriteLine($"error: --encoder nvenc failed ({nvencProbe.FailureClassification}): {nvencProbe.Diagnostics}");
                    return 4;
                }
                options.Encoder = composer.EffectiveEncoder;
                progress?.EncoderSelected(options.Encoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264");

                // Emit the plan only after the effective encoder is known.
                // Generic VGM paths resolve the encoder later than capture and
                // scope planning; reporting the requested auto value would
                // make --print-layout lie about the actual output.
                VisualizationPlanOutput.Emit(
                    timeline,
                    topology,
                    layout,
                    options.LayoutMode,
                    options.Channels,
                    options.GroupBy,
                    options.TimeGrid,
                    options.ScopePosition,
                    options.ScopeRatio,
                    options.Fps,
                    options.FpsDenominator,
                    options.Encoder.ToString(),
                    options.Analysis ? "requested" : "none",
                    options.PrintLayout,
                    options.LayoutJson);

                progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.ComposingFrames));
                Stopwatch compositionWatch = Stopwatch.StartNew();
                try
                {
                    if (!useCorrscope)
                    {
                        composer.ComposeMasterOnly(
                            audioPath,
                            videoPath,
                            panelRenderer,
                            includeWaveform: false);
                    }
                    else if (!corrRunner.IsAvailable)
                    {
                        if (scopePlan.Support == ScopeSupport.Channel && options.ScopeModeExplicit)
                        {
                            progress?.Failed(
                                "channel scopes require Corrscope",
                                ValidationCodes.CorrscopeNotFound, 4);
                            Console.Error.WriteLine(
                                "error: channel scopes require Corrscope " +
                                "(install Corrscope or pass --corrscope PATH)");
                            return 4;
                        }
                        Console.Error.WriteLine(
                            "warning: corr not found; using the internal master waveform " +
                            "(install Corrscope or pass --corrscope PATH)");
                        composer.ComposeMasterOnly(audioPath, videoPath, panelRenderer, includeWaveform: true);
                    }
                    else
                    {
                        string bridgePath = Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py");
                        if (!File.Exists(bridgePath))
                        {
                            progress?.Failed(
                                $"bridge script not found: {bridgePath}",
                                ValidationCodes.CaptureFailed, 7);
                            Console.Error.WriteLine($"error: bridge script not found: {bridgePath}");
                            return 7;
                        }
                        string pythonPath = CorrscopeRunner.ResolvePythonPath(corrRunner.CorrPath);
                        Process corrProcess = corrRunner.StartRawFrames(pythonPath, bridgePath, yamlPath);
                        composer.Compose(corrProcess, audioPath, videoPath, panelRenderer);
                    }
                }
                catch (Exception ex) when (encoderFallback.ShouldRetry(requestedEncoder, ex))
                {
                    progress?.Warning("NVENC runtime failure; retrying with libx264");
                    Console.Error.WriteLine("warning: NVENC runtime failure; retrying with libx264");
                    options.Encoder = VideoEncoder.LibX264;
                    composer = new SinglePassComposer(options.FfmpegPath, new SinglePassComposer.Options
                    {
                        TimeoutMinutes = options.ExternalToolTimeoutMinutes,
                        VideoPreset = options.FinalQuality ? "veryfast" : "ultrafast",
                        VideoCrf = options.FinalQuality ? "18" : "20",
                        Encoder = VideoEncoder.LibX264,
                    });
                    if (!useCorrscope)
                        composer.ComposeMasterOnly(audioPath, videoPath, panelRenderer, includeWaveform: false);
                    else if (!corrRunner.IsAvailable)
                        composer.ComposeMasterOnly(audioPath, videoPath, panelRenderer, includeWaveform: true);
                    else
                    {
                        string pythonPath = CorrscopeRunner.ResolvePythonPath(corrRunner.CorrPath);
                        Process retryProcess = corrRunner.StartRawFrames(
                            pythonPath, Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py"), yamlPath);
                        composer.Compose(retryProcess, audioPath, videoPath, panelRenderer);
                    }
                }
                compositionWatch.Stop();
                compositionSeconds = compositionWatch.Elapsed.TotalSeconds;
                progress?.StageCompleted(
                    ProgressJsonlWriter.StageName(ExportStage.ComposingFrames), compositionSeconds);
            }

            totalWatch.Stop();
            progress?.OutputCreated(videoPath);
            VisualizationResultWriter.Write(
                VisualizationResultBuilder.BuildGeneric(
                    options, workspace, timeline, scopeResult,
                    backend: backend.Id,
                    availability: probe.Availability.ToString().ToLowerInvariant(),
                    portable: probe.Portable,
                    encoder: options.Encoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264",
                    encoderFallback: encoderFallback.Retried
                        ? new EncoderFallbackResult("composition", encoderFallback.Reason, encoderFallback.Diagnostics, Retried: true)
                        : null,
                    captureSeconds, stemRenderSeconds, compositionSeconds,
                    backendResolutionSeconds: captureSeconds, totalWatch.Elapsed.TotalSeconds,
                    composeMetrics: composer?.LastMetrics, warnings: timeline.Warnings),
                humanReadable: true, json: options.Json, quiet: options.Quiet,
                output: ProgressJsonlWriter.HumanOutput(progress));
            progress?.Completed(videoPath, totalWatch.Elapsed.TotalSeconds);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: visualization cancelled");
            return 7;
        }
        catch (Exception ex)
        {
            progress?.Failed(ex.Message, ValidationCodes.CaptureFailed, 7);
            Console.Error.WriteLine($"error: visualization capture failed: {ex.Message}");
            return 7;
        }
    }

    /// <summary>
    /// Master-fallback grid filling: when no isolated stems exist, publish one
    /// master channel per panel so every Corrscope grid cell carries the master
    /// waveform and the overlay scope cells stay aligned.
    /// </summary>
    private static ScopeRenderer.ScopeResult ExpandMasterToPanels(
        FileInfo input,
        VisualizationWorkspace workspace,
        VisualizeOptions options,
        ScopeRenderer.ScopeResult scopeResult,
        int panelCount)
    {
        var result = new ScopeRenderer.ScopeResult
        {
            Success = true,
            InputPath = scopeResult.InputPath,
            OutputDir = workspace.ScopeDir,
            MasterSamples = scopeResult.MasterSamples,
            SampleRate = scopeResult.SampleRate,
            CompletionReason = scopeResult.CompletionReason,
        };
        for (int panel = 0; panel < panelCount; panel++)
        {
            result.Stems.Add(new ScopeRenderer.StemResult
            {
                Name = "master",
                Label = "Master",
                WavPath = workspace.MasterAudioPath,
                RenderedSamples = scopeResult.MasterSamples,
                Channels = 2,
                Success = true,
            });
        }
        return result;
    }
}
