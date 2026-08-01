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
    public static int Run(VisualizationRequest request, RenderRuntimeOptions runtime)
    {
        OutputSettings output = request.Output;
        TrackSettings tracks = request.Tracks;
        ViewSettings view = request.View;
        StyleSettings style = request.Style;
        PresentationSettings presentationSettings = request.Presentation;
        PlaybackSettings playback = request.Playback;
        var totalWatch = Stopwatch.StartNew();
        ProgressJsonlWriter progress = null;
        if (string.Equals(runtime.ProgressMode, "jsonl", StringComparison.Ordinal))
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
        try { track = TrackPreparation.Prepare(request.InputPath, runtime.FmpCom, runtime.AssetsDir, runtime.SearchPaths); }
        catch (TrackPreparationException ex)
        {
            return Fail(ex.Message, ex.ExitCode);
        }
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.PreparingInput), stageWatch.Elapsed.TotalSeconds);
        double preparationSeconds = stageWatch.Elapsed.TotalSeconds;

        CorrscopeRunner corrRunner = null;
        SinglePassComposer singlePass = null;
        VideoEncoder requestedEncoder = (VideoEncoder)output.Encoder;
        var encoderFallback = new VisualizationSupport.EncoderFallbackState();
        if (true)
        {
            corrRunner = new CorrscopeRunner(runtime.ToolTimeoutMinutes, runtime.CorrscopePath);
            singlePass = new SinglePassComposer(
                runtime.FfmpegPath,
                new SinglePassComposer.Options
                {
                    TimeoutMinutes = runtime.ToolTimeoutMinutes,
                    VideoPreset = "ultrafast",
                    VideoCrf = "20",
                    Encoder = requestedEncoder,
                });
            if (!singlePass.IsAvailable)
                return Fail("ffmpeg not found (install FFmpeg or pass --ffmpeg PATH)", 4);
            EncoderProbeResult nvencProbe = requestedEncoder == VideoEncoder.Nvenc
                ? new FfmpegVideoEncoderProbe(singlePass.FfmpegPath,
                TimeSpan.FromMinutes(runtime.ToolTimeoutMinutes)).Probe(VideoEncoder.Nvenc)
                : null;
            if (nvencProbe is { Supported: false })
                return Fail($"--encoder nvenc failed ({nvencProbe.FailureClassification}): {nvencProbe.Diagnostics}", 4);
            progress?.EncoderSelected(singlePass.EffectiveEncoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264");
            if (!runtime.Quiet)
                humanOut.WriteLine($"Video encoder: {(singlePass.EffectiveEncoder == VideoEncoder.Nvenc ? "h264_nvenc (GPU)" : "libx264 (CPU fallback)")}");
        }

        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request, track.Input);
        if (!output.Overwrite && workspace.HasConflict(true))
            return Fail(
                $"output exists: {workspace.FirstConflict(true)} (use --overwrite)",
                9);
        workspace.EnsureDirectories();

        var capturePipeline = new VisualizationPipeline(track.Assets, track.FileSystem, playback.SampleRate);
        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline));
        VisualizationPipeline.Result capture = capturePipeline.Capture(
            track.Data,
            track.Input.FullName,
            new VisualizationPipeline.Options
            {
                LoopCount = playback.LoopCount,
                FadeSeconds = playback.FadeSeconds,
                TailSeconds = playback.TailSeconds,
                MaxDurationSeconds = playback.MaximumDurationSeconds ?? 300,
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
            VisualizationLayoutModeMapper.FromComposition(request.Composition),
            new VisualizationLayoutSettings(
                output.Width, output.Height, view.PastSeconds, view.FutureSeconds,
                1.0, null, null, null,
                VisualizationScopePosition.Bottom,
                VisualizationChannelFilter.All,
                VisualizationGroupBy.Device,
                tracks.IncludedIds, tracks.ExcludedIds));
        OverlayLayout layout = resolvedLayout.Geometry;
        bool useScopes = layout.HasScopes;
        StemPass[] scopeStems = useScopes
            ? DefaultStems.All
            : [DefaultStems.All[0]];

        try { VisualizationJsonWriter.Write(workspace.TimelinePath, capture.Timeline); }
        catch (Exception ex)
        {
            return Fail($"writing timeline — {ex.Message}", 7);
        }

        AnalysisOutput analysisOutput = null;

        var scopeRenderer = new ScopeRenderer(
            track.Assets,
            track.FileSystem,
            playback.SampleRate,
            playback.LoopCount,
            playback.FadeSeconds,
            playback.TailSeconds,
            playback.MaximumDurationSeconds ?? 300,
            playback.SsgGainDb);
        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.RenderingChannelStems));
        ScopeRenderer.ScopeResult scopeResult = scopeRenderer.Render(
            track.Data,
            track.Input.FullName,
            workspace.ScopeDir,
            scopeStems,
            VisualizationSupport.CreateProgressReporter(runtime.Quiet),
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
        if (layout.HasScopes)
        {
            CorrscopeConfigWriter.Write(yamlPath, workspace.ScopeDir, scopeResult,
                audioDir: "../audio",
                overrides: new CorrscopeOverrides
                {
                    Fps = output.FpsNumerator,
                    RenderWidth = layout.CorrscopeGridWidth,
                    RenderHeight = layout.CorrscopeGridHeight,
                    LayoutNCols = layout.ColumnCount,
                    IncludeSilentChannels = tracks.Selection == TrackSelectionMode.All,
                    HideLabels = true,
                    ResDivisor = 1.0,
                    Antialiasing = true,
                });
        }

        VisualizationTimeline videoTimeline = VisualizationSupport.AlignTimelineToAudio(
            capture.Timeline, scopeResult.MasterSamples);
        AnalysisOverlayScene analysisOverlay = AnalysisOverlayScene.Empty;
        string masterAudioPath = scopeResult.Stems
            .FirstOrDefault(stem => stem.Name == "master" && stem.Success)?.WavPath;
        if (string.IsNullOrEmpty(masterAudioPath) || !File.Exists(masterAudioPath))
            return Fail("master WAV was not produced by the scope render", 7);

        stageWatch.Restart();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.AnalyzingEnergy));
        int totalFrames = (int)Math.Ceiling(scopeResult.MasterSamples *
            (double)output.FpsNumerator / scopeResult.SampleRate);
        ChannelEnergyEnvelope[] energy = ChannelEnergyAnalyzer.Analyze(
            scopeResult.Stems.Where(stem => stem.Success)
                .Select(stem => (stem.Name, stem.WavPath)).ToArray(),
            totalFrames,
            scopeResult.SampleRate,
            output.FpsNumerator,
            output.FpsDenominator);
        stageWatch.Stop();
        progress?.StageCompleted(ProgressJsonlWriter.StageName(ExportStage.AnalyzingEnergy), stageWatch.Elapsed.TotalSeconds);
        double energySeconds = stageWatch.Elapsed.TotalSeconds;

        VisualizationPresentation presentation = VisualizationSupport.ResolvePresentation(request, track.Input);

        var panelRenderer = new PanelOverlayRenderer(
            videoTimeline,
            resolvedLayout,
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = output.FpsNumerator,
                FpsDenominator = output.FpsDenominator,
                TimeGrid = (VisualizationTimeGrid) view.TimeGrid,
                Presentation = presentation,
                FontPath = presentationSettings.FontPath,
                PreferAntialiasedText = true,
                Effects = (EffectsMode) style.Effects,
                NoteColor = style.NoteColor switch
                {
                    Fmp.Application.Contracts.NoteColorMode.Channel => Fmp.Core.Visualization.Rendering.NoteColorMode.Channel,
                    Fmp.Application.Contracts.NoteColorMode.PitchClass => Fmp.Core.Visualization.Rendering.NoteColorMode.Pitch,
                    _ => Fmp.Core.Visualization.Rendering.NoteColorMode.Instrument,
                },
                Palette = VisualizationPalette.Default,
                MotionBlurSamples = 1,
                IntroSeconds = 0.75,
                OutroSeconds = Math.Min(0.45, playback.TailSeconds),
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
                    singlePass = new SinglePassComposer(runtime.FfmpegPath, new SinglePassComposer.Options
                    {
                        TimeoutMinutes = runtime.ToolTimeoutMinutes,
                        VideoPreset = "ultrafast",
                        VideoCrf = "20",
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
                request, runtime, workspace, capture, scopeResult,
                backend: "fmp", availability: "available", portable: true,
                encoder: singlePass.EffectiveEncoder.ToString(),
                encoderFallback: encoderFallback.Retried
                    ? new EncoderFallbackResult("composition", "nvenc runtime failure", encoderFallback.Diagnostics, Retried: true)
                    : null,
                preparationSeconds, captureSeconds, stemRenderSeconds, energySeconds,
                compositionSeconds, backendResolutionSeconds: preparationSeconds, totalWatch.Elapsed.TotalSeconds,
                composeMetrics: singlePass.LastMetrics, warnings: Array.Empty<string>()),
            humanReadable: true, json: runtime.Json, quiet: runtime.Quiet,
            output: resultOut);
        progress?.Completed(workspace.VideoPath, totalWatch.Elapsed.TotalSeconds);
        return 0;
    }

    private static string ResolveAnalysisOutputPath(
        VisualizationRequest request,
        VisualizationWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            return Path.Combine(workspace.OutputDir, "analysis", "analysis.json");
        string configured = Path.GetFullPath(request.OutputPath);
        return string.Equals(Path.GetExtension(configured), ".json", StringComparison.OrdinalIgnoreCase)
            ? configured
            : Path.Combine(configured, "analysis.json");
    }
}
