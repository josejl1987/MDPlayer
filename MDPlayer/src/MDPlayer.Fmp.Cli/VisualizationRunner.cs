using System.Diagnostics;
using Fmp.Core.Audio;
using Fmp.Core.Analysis;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

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
        var stageWatch = Stopwatch.StartNew();
        PreparedTrack track;
        try { track = TrackPreparation.Prepare(options.Input, options); }
        catch (TrackPreparationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        stageWatch.Stop();
        double preparationSeconds = stageWatch.Elapsed.TotalSeconds;

        CorrscopeRunner corrRunner = null;
        SinglePassComposer singlePass = null;
        if (!options.StemsOnly)
        {
            corrRunner = new CorrscopeRunner(options.ExternalToolTimeoutMinutes, options.CorrscopePath);
            if (!corrRunner.IsAvailable)
            {
                Console.Error.WriteLine("error: corr not found (install Corrscope or pass --corrscope PATH)");
                return 4;
            }
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
            {
                Console.Error.WriteLine("error: ffmpeg not found (install FFmpeg or pass --ffmpeg PATH)");
                return 4;
            }
            if (options.Encoder == VideoEncoder.Nvenc && !singlePass.SupportsEncoder(VideoEncoder.Nvenc))
            {
                Console.Error.WriteLine("error: --encoder nvenc requested but FFmpeg does not expose h264_nvenc");
                return 4;
            }
            options.Encoder = singlePass.EffectiveEncoder;
            if (!options.Quiet)
                Console.WriteLine($"Video encoder: {(singlePass.EffectiveEncoder == VideoEncoder.Nvenc ? "h264_nvenc (GPU)" : "libx264 (CPU fallback)")}");
        }

        VisualizationWorkspace workspace = VisualizationWorkspace.Create(options, track.Input);
        if (!options.Overwrite && workspace.HasConflict(!options.StemsOnly))
        {
            string existing = !options.StemsOnly && File.Exists(workspace.VideoPath)
                ? workspace.VideoPath : workspace.TimelinePath;
            Console.Error.WriteLine($"error: output exists: {existing} (use --overwrite)");
            return 9;
        }
        workspace.EnsureDirectories();

        var capturePipeline = new VisualizationPipeline(track.Assets, track.FileSystem, options.SampleRate);
        stageWatch.Restart();
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
        double captureSeconds = stageWatch.Elapsed.TotalSeconds;
        if (!capture.Success || capture.Timeline == null)
        {
            Console.Error.WriteLine($"error: visualization capture failed: {capture.LastError}");
            return 7;
        }

        VisualizationLayoutMode layoutMode = options.LayoutMode;
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(capture.Timeline, layoutMode);
        if (layoutMode == VisualizationLayoutMode.Focus
            && topology.Panels.Any(panel => !IsKnownPanelId(panel.Id)))
        {
            // The current FMP scope backend has a fixed stem vocabulary. Keep
            // the generic diagnostic topology if a future descriptor cannot be
            // represented by that backend yet.
            layoutMode = VisualizationLayoutMode.Diagnostic;
            topology = VisualizationTopologyBuilder.Build(capture.Timeline, layoutMode);
        }
        OverlayLayout layout = new(options.Width, options.Height, 0.75, 2.25, topology.Panels.Count);
        StemPass[] scopeStems = SelectScopeStems(topology, layoutMode);

        try { VisualizationJsonWriter.Write(workspace.TimelinePath, capture.Timeline); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: writing timeline — {ex.Message}");
            return 7;
        }

        AnalysisOutput analysisOutput = null;
        if (options.Analysis)
        {
            int analysisExit = AnalysisRunner.Run(new AnalyzeOptions
            {
                Timeline = workspace.TimelinePath,
                AnalysisPython = options.AnalysisPython,
                AnalysisOutput = options.AnalysisOutput,
                AnalysisCache = options.AnalysisCache,
                Detail = options.AnalysisDetail,
                Force = options.AnalysisForce,
                TimeoutMinutes = options.AnalysisTimeoutMinutes,
            });
            if (analysisExit != 0)
                return analysisExit;
            string analysisPath = ResolveAnalysisOutputPath(options, workspace);
            analysisOutput = AnalysisResultValidator.ReadAndValidate(
                analysisPath,
                FmpSymbolicNormalizer.Normalize(capture.Timeline));
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
        ScopeRenderer.ScopeResult scopeResult = scopeRenderer.Render(
            track.Data,
            track.Input.FullName,
            workspace.ScopeDir,
            scopeStems,
            VisualizationSupport.CreateProgressReporter(options.Quiet),
            skipSilentStems: false);
        stageWatch.Stop();
        double stemRenderSeconds = stageWatch.Elapsed.TotalSeconds;
        if (!scopeResult.Success)
        {
            Console.Error.WriteLine($"error: scope render failed: {scopeResult.LastError}");
            return 7;
        }
        if (scopeResult.SampleRate != capture.Timeline.SampleRate)
        {
            Console.Error.WriteLine($"error: scope/timeline sample-rate mismatch: {scopeResult.SampleRate} vs {capture.Timeline.SampleRate}");
            return 7;
        }

        string[] missingStems = scopeStems
            .Where(pass => !scopeResult.Stems.Any(stem => stem.Name == pass.Name
                && stem.Success && stem.RenderedSamples == scopeResult.MasterSamples
                && File.Exists(stem.WavPath)))
            .Select(pass => pass.Name)
            .ToArray();
        if (scopeResult.MasterSamples <= 0 || missingStems.Length > 0)
        {
            Console.Error.WriteLine($"error: Corrscope layout requires synchronized stems: {string.Join(", ", missingStems)}");
            return 7;
        }

        string yamlPath = Path.Combine(workspace.ScopeDir, "corrscope-grid.yaml");
        if (options.StemsOnly)
        {
            CorrscopeConfigWriter.Write(yamlPath, workspace.ScopeDir, scopeResult,
                overrides: new CorrscopeOverrides
                {
                    Fps = options.Fps,
                    RenderWidth = options.Width,
                    RenderHeight = options.Height,
                    LayoutNCols = layout.ColumnCount,
                    IncludeSilentChannels = true,
                    HideLabels = true,
                    FfmpegVideoTemplate = options.CorrscopeVideoTemplate,
                    ResDivisor = options.FinalQuality ? 1.0 : 2.0,
                    Antialiasing = options.FinalQuality,
                });
            VisualizationSupport.WriteSummary(options, workspace.TimelinePath, null, capture, scopeResult);
            if (!options.Quiet) Console.Error.WriteLine($"stems: {workspace.ScopeDir}");
            return 0;
        }

        CorrscopeConfigWriter.Write(yamlPath, workspace.ScopeDir, scopeResult,
            overrides: new CorrscopeOverrides
            {
                Fps = options.Fps,
                RenderWidth = options.Width,
                RenderHeight = layout.CorrscopeGridHeight,
                LayoutNCols = layout.ColumnCount,
                IncludeSilentChannels = true,
                HideLabels = true,
                ResDivisor = 1.0,
                Antialiasing = options.FinalQuality,
            });

        VisualizationTimeline videoTimeline = VisualizationSupport.AlignTimelineToAudio(
            capture.Timeline, scopeResult.MasterSamples);
        AnalysisOverlayScene analysisOverlay = analysisOutput is null
            || options.AnalysisOverlay == "none"
            ? AnalysisOverlayScene.Empty
            : AnalysisOverlaySceneBuilder.Build(
                analysisOutput,
                videoTimeline,
                allowTentative: options.AnalysisOverlay == "standard");
        string masterAudioPath = scopeResult.Stems
            .FirstOrDefault(stem => stem.Name == "master" && stem.Success)?.WavPath;
        if (string.IsNullOrEmpty(masterAudioPath) || !File.Exists(masterAudioPath))
        {
            Console.Error.WriteLine("error: master WAV was not produced by the scope render");
            return 7;
        }

        stageWatch.Restart();
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
        double energySeconds = stageWatch.Elapsed.TotalSeconds;

        VisualizationPresentation presentation = VisualizationSupport.ResolvePresentation(options, track.Input);

        var panelRenderer = new PanelOverlayRenderer(
            videoTimeline,
            new PanelOverlayRenderer.Options
            {
                Width = options.Width,
                Height = options.Height,
                FpsNumerator = options.Fps,
                FpsDenominator = options.FpsDenominator,
                PastSeconds = 0.75,
                FutureSeconds = 2.25,
                Presentation = presentation,
                FontPath = options.FontPath,
                Effects = options.Effects,
                NoteColor = options.NoteColor,
                LayoutMode = layoutMode,
                IntroSeconds = 0.75,
                OutroSeconds = Math.Min(0.45, options.Tail),
                AnalysisOverlay = analysisOverlay,
                Energy = energy,
            });

        stageWatch.Restart();
        try
        {
            string bridgePath = Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py");
            if (!File.Exists(bridgePath))
            {
                Console.Error.WriteLine($"error: bridge script not found: {bridgePath}");
                return 7;
            }
            string pythonPath = CorrscopeRunner.ResolvePythonPath(corrRunner.CorrPath);
            Process corrProcess = corrRunner.StartRawFrames(pythonPath, bridgePath, yamlPath);
            singlePass.Compose(corrProcess, masterAudioPath, workspace.VideoPath, panelRenderer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: single-pass video composition failed: {ex.Message}");
            return 7;
        }
        stageWatch.Stop();
        double compositionSeconds = stageWatch.Elapsed.TotalSeconds;
        totalWatch.Stop();
        VisualizationSupport.WriteSummary(options, workspace.TimelinePath, workspace.VideoPath,
            capture, scopeResult, captureSeconds, stemRenderSeconds, energySeconds,
            preparationSeconds, compositionSeconds, totalWatch.Elapsed.TotalSeconds,
            singlePass.LastMetrics);
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

    private static StemPass[] SelectScopeStems(
        VisualizationTopology topology,
        VisualizationLayoutMode layoutMode)
    {
        if (layoutMode == VisualizationLayoutMode.Diagnostic)
            return DefaultStems.All;

        var activePanelIds = topology.Panels
            .Select(panel => panel.Id)
            .ToHashSet(StringComparer.Ordinal);
        return DefaultStems.All
            .Where(stem => stem.Name == "master"
                || activePanelIds.Contains(StemNameToPanelId(stem.Name)))
            .ToArray();
    }

    private static bool IsKnownPanelId(string panelId)
        => DefaultStems.All.Any(stem => StemNameToPanelId(stem.Name) == panelId);

    private static string StemNameToPanelId(string stemName) => stemName switch
    {
        "ym2608-fm1" => "ym2608.0.fm.1",
        "ym2608-fm2" => "ym2608.0.fm.2",
        "ym2608-fm3" => "ym2608.0.fm.3",
        "ym2608-fm4" => "ym2608.0.fm.4",
        "ym2608-fm5" => "ym2608.0.fm.5",
        "ym2608-fm6" => "ym2608.0.fm.6",
        "ym2608-ssg1" => "ym2608.0.ssg.1",
        "ym2608-ssg2" => "ym2608.0.ssg.2",
        "ym2608-ssg3" => "ym2608.0.ssg.3",
        "ym2608-rhythm" => "ym2608.0.rhythm",
        "ym2608-adpcm" => "ym2608.0.adpcm-b",
        "ppz8-01" => "ppz8.0",
        _ => null,
    };
}
