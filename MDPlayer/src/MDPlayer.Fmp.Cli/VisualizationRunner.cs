using System.Diagnostics;
using Fmp.Application.Contracts;
using Fmp.Core.Audio;
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
/// Concrete linear orchestration for the FMP visualizer. Each stage owns its
/// inputs and returns data for the next stage; rendering remains single-pass.
/// </summary>
internal static class VisualizationRunner
{
    public static int Run(VisualizeOptions options)
    {
        var totalWatch = Stopwatch.StartNew();
        ProgressJsonlWriter progress = null;
        if (string.Equals(options.ProgressMode, "jsonl", StringComparison.Ordinal))
        {
            progress = new ProgressJsonlWriter(Console.Out);
            progress.Started();
        }
        // Structured progress owns stdout; human lines move to stderr.
        TextWriter humanOut = progress == null ? Console.Out : Console.Error;
        TextWriter resultOut = humanOut;

        int Fail(string message, int exitCode)
        {
            progress?.Failed(message, ProgressJsonlWriter.MapFailureCode(message, exitCode), exitCode);
            Console.Error.WriteLine($"error: {message}");
            return exitCode;
        }

        var stageWatch = Stopwatch.StartNew();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.PreparingInput));
        PreparedTrack track;
        try { track = TrackPreparation.Prepare(options.Input, options); }
        catch (TrackPreparationException ex)
        {
            return Fail(ex.Message, ex.ExitCode);
        }
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.PreparingInput), stageWatch.Elapsed.TotalSeconds);
        double preparationSeconds = stageWatch.Elapsed.TotalSeconds;

        CorrscopeRunner corrRunner = null;
        SinglePassComposer singlePass = null;
        VideoEncoder requestedEncoder = options.Encoder;
        var encoderFallback = new VisualizationSupport.EncoderFallbackState();
        if (!options.StemsOnly)
        {
            corrRunner = new CorrscopeRunner(options.ExternalToolTimeoutMinutes, options.CorrscopePath);
            singlePass = new SinglePassComposer(
                options.FfmpegPath,
                new SinglePassComposer.Options
                {
                    TimeoutMinutes = options.ExternalToolTimeoutMinutes,
                    VideoPreset = options.FinalQuality ? "veryfast" : "ultrafast",
                    VideoCrf = options.FinalQuality ? "18" : "20",
                    Encoder = options.Encoder,
                });
            if (!singlePass.IsAvailable)
                return Fail("ffmpeg not found (install FFmpeg or pass --ffmpeg PATH)", 4);
            EncoderProbeResult nvencProbe = options.Encoder == VideoEncoder.Nvenc
                ? new FfmpegVideoEncoderProbe(singlePass.FfmpegPath,
                TimeSpan.FromMinutes(options.ExternalToolTimeoutMinutes)).Probe(VideoEncoder.Nvenc)
                : null;
            if (nvencProbe is { Supported: false })
                return Fail($"--encoder nvenc failed ({nvencProbe.FailureClassification}): {nvencProbe.Diagnostics}", 4);
            options.Encoder = singlePass.EffectiveEncoder;
            progress?.EncoderSelected(options.Encoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264");
            if (!options.Quiet)
                humanOut.WriteLine($"Video encoder: {(singlePass.EffectiveEncoder == VideoEncoder.Nvenc ? "h264_nvenc (GPU)" : "libx264 (CPU fallback)")}");
        }

        VisualizationWorkspace workspace = VisualizationWorkspace.Create(options, track.Input);
        if (!options.Overwrite && workspace.HasConflict(!options.StemsOnly))
            return Fail(
                $"output exists: {workspace.FirstConflict(!options.StemsOnly)} (use --overwrite)",
                9);
        workspace.EnsureDirectories();

        var capturePipeline = new VisualizationPipeline(track.Assets, track.FileSystem, options.SampleRate);
        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline));
        VisualizationPipeline.Result capture = capturePipeline.Capture(
            track.Data,
            track.Input.FullName,
            new VisualizationPipeline.Options
            {
                LoopCount = options.Loops,
                FadeSeconds = options.Fade,
                TailSeconds = options.Tail,
                MaxDurationSeconds = options.MaxDuration,
                TimeoutSeconds = options.Timeout,
            });
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline), stageWatch.Elapsed.TotalSeconds);
        double captureSeconds = stageWatch.Elapsed.TotalSeconds;
        if (!capture.Success || capture.Timeline == null)
            return Fail($"visualization capture failed: {capture.LastError}", 7);
        if (!VisualizationContentAvailability.HasRenderableContent(capture.Timeline))
            return Fail(
                "visualization capture contains neither semantic events nor waveform activity",
                10);

        ResolvedVisualizationLayout resolvedLayout = VisualizationLayoutBuilder.Build(
            capture.Timeline,
            options.LayoutMode,
            options.ToLayoutSettings());
        OverlayLayout layout = resolvedLayout.Geometry;
        if (!string.IsNullOrWhiteSpace(options.PreviewHtmlPath))
        {
            VisualizationPreviewWriter.Write(
                options.PreviewHtmlPath,
                capture.Timeline,
                VisualizationLayoutPlan.Create(
                    capture.Timeline,
                    resolvedLayout,
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
            VisualizationDiagnosticPagesWriter.Write(
                options.DiagnosticPagesPath,
                capture.Timeline,
                VisualizationLayoutPlan.Create(
                    capture.Timeline,
                    resolvedLayout,
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
        VisualizationPlanOutput.Emit(
            capture.Timeline,
            resolvedLayout,
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
        bool useScopes = options.StemsOnly || layout.HasScopes;
        StemPass[] scopeStems = useScopes
            ? DefaultStems.All
            : [DefaultStems.All[0]];

        try { VisualizationJsonWriter.Write(workspace.TimelinePath, capture.Timeline); }
        catch (Exception ex)
        {
            return Fail($"writing timeline — {ex.Message}", 7);
        }

        AnalysisOutput analysisOutput = null;
        if (options.Analysis)
        {
            stageWatch.Restart();
            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.RunningAnalysis));
            AnalysisExecutionResult analysis = AnalysisRunner.Execute(new AnalyzeOptions
            {
                CapturedTimeline = capture.Timeline,
                AnalysisPython = options.AnalysisPython,
                AnalysisOutput = options.AnalysisOutput ?? Path.Combine(workspace.OutputDir, "analysis"),
                AnalysisCache = options.AnalysisCache,
                Detail = options.AnalysisDetail,
                Force = options.AnalysisForce,
                TimeoutMinutes = options.AnalysisTimeoutMinutes,
                Output = humanOut,
            });
            stageWatch.Stop();
            progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.RunningAnalysis), stageWatch.Elapsed.TotalSeconds);
            if (analysis.ExitCode != 0)
                return Fail($"analysis failed (exit {analysis.ExitCode})", analysis.ExitCode);
            analysisOutput = analysis.Output;
        }

        var scopeRenderer = new ScopeRenderer(
            track.Assets,
            track.FileSystem,
            options.SampleRate,
            options.Loops,
            options.Fade,
            options.Tail,
            options.MaxDuration,
            options.SsgGainDb);
        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.RenderingChannelStems));
        ScopeRenderer.ScopeResult scopeResult = scopeRenderer.Render(
            track.Data,
            track.Input.FullName,
            workspace.ScopeDir,
            scopeStems,
            VisualizationSupport.CreateProgressReporter(options.Quiet),
            skipSilentStems: false,
            audioDir: workspace.AudioDir,
            metadataPath: workspace.ScopeMetadataPath);
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.RenderingChannelStems), stageWatch.Elapsed.TotalSeconds);
        double stemRenderSeconds = stageWatch.Elapsed.TotalSeconds;
        if (!scopeResult.Success)
            return Fail($"scope render failed: {scopeResult.LastError}", 7);
        if (scopeResult.SampleRate != capture.Timeline.SampleRate)
            return Fail($"scope/timeline sample-rate mismatch: {scopeResult.SampleRate} vs {capture.Timeline.SampleRate}", 7);

        string[] missingStems = scopeStems
            .Where(pass => !scopeResult.Stems.Any(stem => stem.Name == pass.Name
                && stem.Success && stem.RenderedSamples == scopeResult.MasterSamples
                && File.Exists(stem.WavPath)))
            .Select(pass => pass.Name)
            .ToArray();
        if (scopeResult.MasterSamples <= 0 || missingStems.Length > 0)
            return Fail($"Corrscope layout requires synchronized stems: {string.Join(", ", missingStems)}", 7);

        string yamlPath = workspace.CorrscopeConfigPath;
        if (options.StemsOnly)
        {
            CorrscopeConfigWriter.Write(yamlPath, workspace.ScopeDir, scopeResult,
                audioDir: "../audio",
                overrides: new CorrscopeOverrides
                {
                    Fps = options.Fps,
                    RenderWidth = layout.CorrscopeGridWidth,
                    RenderHeight = options.Height,
                    LayoutNCols = layout.ColumnCount,
                    IncludeSilentChannels = options.Channels == VisualizationChannelFilter.All,
                    HideLabels = true,
                    FfmpegVideoTemplate = options.CorrscopeVideoTemplate,
                    ResDivisor = options.FinalQuality ? 1.0 : 2.0,
                    Antialiasing = options.FinalQuality,
                });
            VisualizationResultWriter.Write(
                VisualizationResultBuilder.Build(
                    options, workspace, capture, scopeResult,
                    backend: null, availability: null, portable: true,
                    encoder: null, encoderFallback: null,
                    preparationSeconds, captureSeconds, stemRenderSeconds, energySeconds: 0,
                    compositionSeconds: 0, backendResolutionSeconds: 0, totalWatch.Elapsed.TotalSeconds,
                    composeMetrics: null, warnings: new[] { $"stems: {workspace.ScopeDir}" }),
                humanReadable: true, json: options.Json, quiet: options.Quiet,
                output: resultOut);
            progress?.Completed(workspace.ScopeDir, totalWatch.Elapsed.TotalSeconds);
            return 0;
        }

        if (layout.HasScopes)
        {
            CorrscopeConfigWriter.Write(yamlPath, workspace.ScopeDir, scopeResult,
                audioDir: "../audio",
                overrides: new CorrscopeOverrides
                {
                    Fps = options.Fps,
                    RenderWidth = layout.CorrscopeGridWidth,
                    RenderHeight = layout.CorrscopeGridHeight,
                    LayoutNCols = layout.ColumnCount,
                    IncludeSilentChannels = options.Channels == VisualizationChannelFilter.All,
                    HideLabels = true,
                    ResDivisor = 1.0,
                    Antialiasing = options.FinalQuality,
                });
        }

        VisualizationTimeline videoTimeline = VisualizationSupport.AlignTimelineToAudio(
            capture.Timeline, scopeResult.MasterSamples);
        AnalysisOverlayScene analysisOverlay = analysisOutput is null
            || options.AnalysisOverlay == "none"
            ? AnalysisOverlayScene.Empty
            : AnalysisOverlaySceneBuilder.Build(
                analysisOutput,
                videoTimeline,
                allowTentative: options.AnalysisOverlay is "standard" or "full");
        string masterAudioPath = scopeResult.Stems
            .FirstOrDefault(stem => stem.Name == "master" && stem.Success)?.WavPath;
        if (string.IsNullOrEmpty(masterAudioPath) || !File.Exists(masterAudioPath))
            return Fail("master WAV was not produced by the scope render", 7);

        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.AnalyzingEnergy));
        int totalFrames = (int)Math.Ceiling(scopeResult.MasterSamples *
            (double)options.Fps / scopeResult.SampleRate);
        ChannelEnergyEnvelope[] energy = ChannelEnergyAnalyzer.Analyze(
            scopeResult.Stems.Where(stem => stem.Success)
                .Select(stem => (stem.Name, stem.WavPath)).ToArray(),
            totalFrames,
            scopeResult.SampleRate,
            options.Fps,
            options.FpsDenominator);
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.AnalyzingEnergy), stageWatch.Elapsed.TotalSeconds);
        double energySeconds = stageWatch.Elapsed.TotalSeconds;

        VisualizationPresentation presentation = VisualizationSupport.ResolvePresentation(options, track.Input);

        var panelRenderer = new PanelOverlayRenderer(
            videoTimeline,
            resolvedLayout,
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = options.Fps,
                FpsDenominator = options.FpsDenominator,
                TimeGrid = options.TimeGrid,
                Presentation = presentation,
                FontPath = options.FontPath,
                PreferAntialiasedText = options.Preset != VisualizationPreset.Diagnostic,
                Effects = options.Effects,
                NoteColor = options.NoteColor,
                Palette = options.Palette,
                MotionBlurSamples = options.MotionBlurSamples,
                IntroSeconds = 0.75,
                OutroSeconds = Math.Min(0.45, options.Tail),
                AnalysisOverlay = analysisOverlay,
                Energy = energy,
            });

        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.ComposingFrames));
        try
        {
            if (!layout.HasScopes)
            {
                singlePass.ComposeMasterOnly(
                    masterAudioPath,
                    workspace.VideoPath,
                    panelRenderer,
                    includeWaveform: false);
            }
            else
            {
                if (corrRunner is null || !corrRunner.IsAvailable)
                    return Fail("Corrscope is required for this layout (install Corrscope or pass --corrscope PATH)", 4);
                string bridgePath = Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py");
                if (!File.Exists(bridgePath))
                    return Fail($"bridge script not found: {bridgePath}", 7);
                string pythonPath = CorrscopeRunner.ResolvePythonPath(corrRunner.CorrPath);
                Process corrProcess = corrRunner.StartRawFrames(pythonPath, bridgePath, yamlPath);
                try
                {
                    singlePass.Compose(corrProcess, masterAudioPath, workspace.VideoPath, panelRenderer);
                }
                catch (Exception ex) when (encoderFallback.ShouldRetry(requestedEncoder, ex))
                {
                    Console.Error.WriteLine("warning: NVENC runtime failure; restarting Corrscope and retrying with libx264");
                    progress?.Warning("NVENC runtime failure; restarting Corrscope and retrying with libx264");
                    options.Encoder = VideoEncoder.LibX264;
                    singlePass = new SinglePassComposer(options.FfmpegPath, new SinglePassComposer.Options
                    {
                        TimeoutMinutes = options.ExternalToolTimeoutMinutes,
                        VideoPreset = options.FinalQuality ? "veryfast" : "ultrafast",
                        VideoCrf = options.FinalQuality ? "18" : "20",
                        Encoder = VideoEncoder.LibX264,
                    });
                    Process retryProcess = corrRunner.StartRawFrames(pythonPath, bridgePath, yamlPath);
                    singlePass.Compose(retryProcess, masterAudioPath, workspace.VideoPath, panelRenderer);
                }
            }
        }
        catch (Exception ex)
        {
            return Fail($"single-pass video composition failed: {ex.Message}", 7);
        }
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.ComposingFrames), stageWatch.Elapsed.TotalSeconds);
        double compositionSeconds = stageWatch.Elapsed.TotalSeconds;
        progress?.OutputCreated(workspace.VideoPath);
        totalWatch.Stop();
        VisualizationResultWriter.Write(
            VisualizationResultBuilder.Build(
                options, workspace, capture, scopeResult,
                backend: "fmp", availability: "available", portable: true,
                encoder: singlePass.EffectiveEncoder.ToString(),
                encoderFallback: encoderFallback.Retried
                    ? new EncoderFallbackResult("composition", "nvenc runtime failure", encoderFallback.Diagnostics, Retried: true)
                    : null,
                preparationSeconds, captureSeconds, stemRenderSeconds, energySeconds,
                compositionSeconds, backendResolutionSeconds: preparationSeconds, totalWatch.Elapsed.TotalSeconds,
                composeMetrics: singlePass.LastMetrics, warnings: Array.Empty<string>()),
            humanReadable: true, json: options.Json, quiet: options.Quiet,
            output: resultOut);
        progress?.Completed(workspace.VideoPath, totalWatch.Elapsed.TotalSeconds);
        return 0;
    }

    private static string ResolveAnalysisOutputPath(
        VisualizeOptions options,
        VisualizationWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(options.AnalysisOutput))
            return Path.Combine(workspace.OutputDir, "analysis", "analysis.json");
        string configured = Path.GetFullPath(options.AnalysisOutput);
        return string.Equals(Path.GetExtension(configured), ".json", StringComparison.OrdinalIgnoreCase)
            ? configured
            : Path.Combine(configured, "analysis.json");
    }
}
