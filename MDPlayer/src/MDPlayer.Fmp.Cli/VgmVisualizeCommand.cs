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
    internal static int Handle(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationBackendResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(resolution);

        OutputSettings output = request.Output;
        TrackSettings tracks = request.Tracks;
        ViewSettings view = request.View;
        StyleSettings style = request.Style;
        PresentationSettings presentation = request.Presentation;
        PlaybackSettings playback = request.Playback;

        FileInfo input = resolution.Input;
        IPlaybackBackend backend = resolution.Backend;
        PlaybackProbeResult probe = resolution.Probe;

        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request, input);
        if (!output.Overwrite && workspace.HasConflict(true))
        {
            Console.Error.WriteLine(
                $"error: output exists: {workspace.FirstConflict(true)} (use --overwrite)");
            return 9;
        }
        workspace.EnsureDirectories();
        string timelinePath = workspace.TimelinePath;
        string audioPath = workspace.MasterAudioPath;
        string videoPath = workspace.VideoPath;
        string scopeDir = workspace.ScopeDir;

        int timelineSampleRate = probe.NativeSampleRate > 0
            ? probe.NativeSampleRate
            : playback.SampleRate;
        var eventSink = new TimelineDecoderEventSink(timelineSampleRate);
        var totalWatch = Stopwatch.StartNew();
        double captureSeconds = 0;
        double stemRenderSeconds = 0;
        double compositionSeconds = 0;
        SinglePassComposer composer = null;
        VideoEncoder requestedEncoder = (VideoEncoder)output.Encoder;
        VideoEncoder effectiveEncoder = requestedEncoder;
        var encoderFallback = new VisualizationSupport.EncoderFallbackState();
        ProgressJsonlWriter? progress = ProgressJsonlWriter.CreateIfRequested(
            runtime);
        try
        {
            progress?.Started();
            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline));

            Stopwatch captureWatch = Stopwatch.StartNew();
            using IPlaybackCaptureSession session = backend.Open(
                input,
                new PlaybackOptions(
                    playback.LoopCount,
                    playback.FadeSeconds,
                    playback.TailSeconds,
                    playback.MaximumDurationSeconds ?? 300,
                    audioPath,
                    playback.SampleRate,
                    true,
                    MapSpcPitch(playback.SpcPitch),
                    playback.SsgGainDb),
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

            ResolvedVisualizationLayout resolvedLayout = VisualizationLayoutBuilder.Build(
                timeline,
                VisualizationLayoutModeMapper.FromComposition(request.Composition),
                request.ToLayoutSettings());
            OverlayLayout layout = resolvedLayout.Geometry;
            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.RenderingChannelStems));
            Stopwatch stemWatch = Stopwatch.StartNew();
            GenericScopeArtifacts scopeArtifacts;
            try
            {
                scopeArtifacts = VisualizationScopeCoordinator.Render(
                    backend.Id,
                    input,
                    workspace,
                    request,
                    runtime,
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
            bool useCorrscope = scopeArtifacts.Enabled && layout.HasScopes;
            if (!hasRealStems && scopeArtifacts.Enabled)
                scopeResult = ExpandMasterToPanels(workspace, scopeResult, layout.PanelCount);
            if (useCorrscope)
            {
                CorrscopeConfigWriter.Write(yamlPath, scopeDir, scopeResult,
                    audioDir: "../audio",
                    overrides: new CorrscopeOverrides
                    {
                        Fps = output.FpsNumerator,
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
                        IncludeSilentChannels = tracks.Selection == TrackSelectionMode.All,
                        HideLabels = true,
                        ResDivisor = 1.0,
                        Antialiasing = output.Quality != RenderQuality.Draft,
                    });
            }

            CorrscopeRunner corrRunner = useCorrscope
                ? new CorrscopeRunner(
                    runtime.ToolTimeoutMinutes,
                    runtime.CorrscopePath)
                : null;

            {
                var panelRenderer = new PanelOverlayRenderer(
                    timeline,
                    resolvedLayout,
                    new PanelOverlayRenderer.Options
                    {
                        FpsNumerator = output.FpsNumerator,
                        FpsDenominator = output.FpsDenominator,
                        TimeGrid = (VisualizationTimeGrid)view.TimeGrid,
                        Presentation = new VisualizationPresentation(
                            string.IsNullOrWhiteSpace(presentation.Title)
                                ? Path.GetFileNameWithoutExtension(input.Name)
                                : presentation.Title.Trim(),
                            presentation.Subtitle ?? "",
                            presentation.Credits ?? ""),
                        FontPath = presentation.FontPath,
                        PreferAntialiasedText = output.Quality != RenderQuality.Draft,
                        Effects = (EffectsMode)style.Effects,
                        NoteColor = style.NoteColor switch
                        {
                            Fmp.Application.Contracts.NoteColorMode.Channel =>
                                Fmp.Core.Visualization.Rendering.NoteColorMode.Channel,
                            Fmp.Application.Contracts.NoteColorMode.PitchClass =>
                                Fmp.Core.Visualization.Rendering.NoteColorMode.Pitch,
                            _ => Fmp.Core.Visualization.Rendering.NoteColorMode.Instrument,
                        },
                        Palette = VisualizationPalette.Default,
                        MotionBlurSamples = 1,
                        AnalysisOverlay = AnalysisOverlayScene.Empty,
                    });
                composer = new SinglePassComposer(
                    runtime.FfmpegPath,
                    new SinglePassComposer.Options
                    {
                        TimeoutMinutes = runtime.ToolTimeoutMinutes,
                        VideoPreset = output.Quality == RenderQuality.Final ? "veryfast" : "ultrafast",
                        VideoCrf = output.Quality == RenderQuality.Final ? "18" : "20",
                        Encoder = requestedEncoder,
                    });
                if (!composer.IsAvailable)
                {
                    progress?.Failed("ffmpeg not found", ValidationCodes.FfmpegNotFound, 4);
                    Console.Error.WriteLine("error: ffmpeg not found (install FFmpeg or pass --ffmpeg PATH)");
                    return 4;
                }
                EncoderProbeResult nvencProbe = requestedEncoder == VideoEncoder.Nvenc
                    ? new FfmpegVideoEncoderProbe(composer.FfmpegPath,
                        TimeSpan.FromMinutes(runtime.ToolTimeoutMinutes)).Probe(VideoEncoder.Nvenc)
                    : null;
                if (nvencProbe is { Supported: false })
                {
                    progress?.Failed(
                        $"--encoder nvenc failed: {nvencProbe.Diagnostics}",
                        ValidationCodes.EncoderUnavailable, 4);
                    Console.Error.WriteLine($"error: --encoder nvenc failed ({nvencProbe.FailureClassification}): {nvencProbe.Diagnostics}");
                    return 4;
                }
                effectiveEncoder = composer.EffectiveEncoder;
                progress?.EncoderSelected(effectiveEncoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264");

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
                        if (scopePlan.Support == ScopeSupport.Channel)
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
                    effectiveEncoder = VideoEncoder.LibX264;
                    composer = new SinglePassComposer(runtime.FfmpegPath, new SinglePassComposer.Options
                    {
                        TimeoutMinutes = runtime.ToolTimeoutMinutes,
                        VideoPreset = output.Quality == RenderQuality.Final ? "veryfast" : "ultrafast",
                        VideoCrf = output.Quality == RenderQuality.Final ? "18" : "20",
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
                    request,
                    runtime,
                    workspace, timeline, scopeResult,
                    backend: backend.Id,
                    availability: probe.Availability.ToString().ToLowerInvariant(),
                    portable: probe.Portable,
                    encoder: effectiveEncoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264",
                    encoderFallback: encoderFallback.Retried
                        ? new EncoderFallbackResult("composition", encoderFallback.Reason, encoderFallback.Diagnostics, Retried: true)
                        : null,
                    captureSeconds: captureSeconds, stemExportSeconds: stemRenderSeconds,
                    compositionSeconds: compositionSeconds,
                    backendResolutionSeconds: captureSeconds, overallSeconds: totalWatch.Elapsed.TotalSeconds,
                    composeMetrics: composer?.LastMetrics, warnings: timeline.Warnings),
                humanReadable: true, json: runtime.Json, quiet: runtime.Quiet,
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
        VisualizationWorkspace workspace,
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

    private static SpcPitchMode MapSpcPitch(SpcPitchInterpretation pitch) => pitch switch
    {
        SpcPitchInterpretation.Estimate => SpcPitchMode.Estimate,
        SpcPitchInterpretation.Relative => SpcPitchMode.Relative,
        _ => throw new ArgumentOutOfRangeException(nameof(pitch), pitch, null),
    };
}
