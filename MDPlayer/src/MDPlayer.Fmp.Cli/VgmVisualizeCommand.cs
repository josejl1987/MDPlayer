using System.Diagnostics;
using System.Text.Json;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Generic register-log visualization entry point. Register-log backends use a
/// topology-driven overlay and the deterministic master-scope fallback when
/// isolated channel stems are not available.
/// </summary>
internal static class VgmVisualizeCommand
{
    public static int Handle(string[] args)
    {
        Options options;
        try { options = Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(options.Input))
        {
            Console.Error.WriteLine("error: no input file specified");
            return 2;
        }

        var input = new FileInfo(options.Input);
        if (!input.Exists)
        {
            Console.Error.WriteLine($"error: input not found: {input.FullName}");
            return 3;
        }

        var environment = new PlaybackEnvironment(
            [input.DirectoryName ?? "."],
            true,
            options.SampleRate);
        var registry = PlaybackBackendRegistry.CreateDefault(environment);
        if (!registry.TrySelect(
                input,
                environment,
                options.Backend,
                out IPlaybackBackend backend,
                out PlaybackProbeResult probe))
        {
            Console.Error.WriteLine(
                $"error: no playback backend accepted {input.Name}: {string.Join("; ", probe.Warnings)}");
            return 3;
        }
        if (!probe.Visualizable)
        {
            Console.Error.WriteLine(
                "error: MDPlayer can play this track, but none of its active devices expose supported note data");
            return 10;
        }

        string outputDir = options.OutputDir;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            string baseName = Path.GetFileNameWithoutExtension(input.Name);
            outputDir = Path.Combine(input.DirectoryName ?? ".", baseName + ".visualization");
        }
        Directory.CreateDirectory(outputDir);
        string timelinePath = Path.Combine(outputDir, "timeline.json");
        string audioPath = Path.Combine(outputDir, "audio", "master.wav");
        string videoPath = options.VideoPath
            ?? Path.Combine(outputDir, "visualization.mp4");
        string scopeDir = Path.Combine(outputDir, "scope");
        if (!options.Overwrite
            && (File.Exists(timelinePath)
                || File.Exists(audioPath)
                || (!options.StemsOnly && File.Exists(videoPath))))
        {
            Console.Error.WriteLine("error: output exists (use --overwrite)");
            return 9;
        }
        Directory.CreateDirectory(scopeDir);

        int timelineSampleRate = probe.NativeSampleRate > 0
            ? probe.NativeSampleRate
            : options.SampleRate;
        var eventSink = new TimelineDecoderEventSink(timelineSampleRate);
        var totalWatch = Stopwatch.StartNew();
        double captureSeconds = 0;
        double stemRenderSeconds = 0;
        double compositionSeconds = 0;
        SinglePassComposer composer = null;
        try
        {
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
                    options.SpcPitchMode),
                eventSink);
            session.Run();
            captureWatch.Stop();
            captureSeconds = captureWatch.Elapsed.TotalSeconds;

            VisualizationTimeline timeline = eventSink.Complete(
                session.SamplePosition,
                "completed",
                            new TrackMetadata(
                                input.Extension.TrimStart('.').ToLowerInvariant(),
                                Path.GetFileNameWithoutExtension(input.Name),
                                backend.Id,
                                input.Name));
            VisualizationJsonWriter.Write(timelinePath, timeline);
            StemPlan scopePlan = ScopePlanner.Plan(
                timeline.Devices,
                timeline.Voices,
                options.ScopeMode);
            if (!options.StemsOnly && !scopePlan.Supported)
            {
                Console.Error.WriteLine(
                    $"error: requested scope mode '{options.ScopeMode}' is unavailable: {scopePlan.Reason}");
                return 4;
            }

            VisualizationTopology topology = VisualizationTopologyBuilder.Build(timeline);
            OverlayLayout layout = new(
                options.Width,
                options.Height,
                0.75,
                2.25,
                topology.Panels.Count);
            Stopwatch stemWatch = Stopwatch.StartNew();
            ScopeRenderer.ScopeResult scopeResult = backend.Id == "vgm"
                ? VgmScopeRenderer.Render(
                    input.FullName,
                    scopeDir,
                    audioPath,
                    options.SampleRate,
                    options.Loops,
                    options.Fade,
                    options.Tail,
                    options.MaxDuration)
                : null;
            stemWatch.Stop();
            stemRenderSeconds = stemWatch.Elapsed.TotalSeconds;
            bool hasRealStems = scopeResult?.Success == true
                && scopeResult.Stems.Any(stem => stem.Name != "master" && stem.Success);
            if (!hasRealStems)
            {
                scopeResult = new ScopeRenderer.ScopeResult
                {
                    Success = true,
                    InputPath = input.FullName,
                    OutputDir = scopeDir,
                    MasterSamples = session.SamplePosition,
                    SampleRate = options.SampleRate,
                    CompletionReason = "master_fallback",
                };
                for (int panel = 0; panel < layout.PanelCount; panel++)
                {
                    scopeResult.Stems.Add(new ScopeRenderer.StemResult
                    {
                        Name = "master",
                        Label = "Master",
                        WavPath = audioPath,
                        RenderedSamples = session.SamplePosition,
                        Channels = 2,
                        Success = true,
                    });
                }
            }

            if (scopeResult.Stems.Count == 0)
            {
                Console.Error.WriteLine("error: no Corrscope stems were produced");
                return 7;
            }

            string yamlPath = Path.Combine(scopeDir, "corrscope-grid.yaml");
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
                    RenderWidth = options.Width,
                    RenderHeight = layout.CorrscopeGridHeight,
                    LayoutNCols = layout.ColumnCount,
                    IncludeMasterAsChannel = !hasRealStems,
                    IncludeSilentChannels = true,
                    HideLabels = true,
                    ResDivisor = 1.0,
                    Antialiasing = options.FinalQuality,
                });

            if (options.StemsOnly)
            {
                if (!options.Quiet)
                    Console.Error.WriteLine($"stems: {scopeDir}");
                return 0;
            }

            var corrRunner = new CorrscopeRunner(
                options.ExternalToolTimeoutMinutes,
                options.CorrscopePath);

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
                        PastSeconds = 0.75,
                        FutureSeconds = 2.25,
                        Presentation = new VisualizationPresentation(
                            string.IsNullOrWhiteSpace(options.Title)
                                ? Path.GetFileNameWithoutExtension(input.Name)
                                : options.Title.Trim(),
                            options.Subtitle ?? "",
                            options.Credits ?? ""),
                        FontPath = options.FontPath,
                        Effects = options.Effects,
                        NoteColor = options.NoteColor,
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
                    Console.Error.WriteLine("error: ffmpeg not found (install FFmpeg or pass --ffmpeg PATH)");
                    return 4;
                }
                if (options.Encoder == VideoEncoder.Nvenc && !composer.SupportsEncoder(VideoEncoder.Nvenc))
                {
                    Console.Error.WriteLine("error: --encoder nvenc requested but FFmpeg does not expose h264_nvenc");
                    return 4;
                }
                options.Encoder = composer.EffectiveEncoder;

                Stopwatch compositionWatch = Stopwatch.StartNew();
                if (!corrRunner.IsAvailable)
                {
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
                        Console.Error.WriteLine($"error: bridge script not found: {bridgePath}");
                        return 7;
                    }
                        string pythonPath = CorrscopeRunner.ResolvePythonPath(corrRunner.CorrPath);
                    Process corrProcess = corrRunner.StartRawFrames(pythonPath, bridgePath, yamlPath);
                    composer.Compose(corrProcess, audioPath, videoPath, panelRenderer);
                }
                compositionWatch.Stop();
                compositionSeconds = compositionWatch.Elapsed.TotalSeconds;
            }

            if (!options.Quiet)
            {
                Console.WriteLine($"Timeline written to: {timelinePath}");
                Console.WriteLine($"Master WAV written to: {audioPath}");
                if (!options.StemsOnly)
                    Console.WriteLine(
                        $"Visualization video written to: {videoPath} " +
                        $"(scope: corrscope {(hasRealStems ? "channels" : "master")}, strategy: {scopePlan.Strategy})");
                Console.WriteLine($"Devices: {timeline.Devices.Count}  Notes: {timeline.Notes.Count}");
                foreach (string warning in timeline.Warnings)
                    Console.Error.WriteLine($"warning: {warning}");
            }

            if (options.Json)
            {
                totalWatch.Stop();
                double trackDurationSeconds = scopeResult.SampleRate > 0
                    ? scopeResult.MasterSamples / (double)scopeResult.SampleRate : 0;
                double effectiveFps = compositionSeconds > 0
                    ? Math.Ceiling(scopeResult.MasterSamples * options.Fps /
                        (double)scopeResult.SampleRate) / compositionSeconds : 0;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    backend = backend.Id,
                    availability = probe.Availability.ToString().ToLowerInvariant(),
                    portable = probe.Portable,
                    timeline = timelinePath,
                    audio = audioPath,
                    video = options.StemsOnly ? null : videoPath,
                    sampleRate = timeline.SampleRate,
                    samples = timeline.EndSample,
                    devices = timeline.Devices.Count,
                    notes = timeline.Notes.Count,
                    encoder = options.Encoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264",
                    outputSizeBytes = !options.StemsOnly && File.Exists(videoPath)
                        ? new FileInfo(videoPath).Length : 0,
                    trackDurationSeconds,
                    effectiveOutputFps = effectiveFps,
                    realTimeFactor = trackDurationSeconds > 0 && compositionSeconds > 0
                        ? trackDurationSeconds / compositionSeconds : 0,
                    stages = new
                    {
                        timelineCaptureSeconds = captureSeconds,
                        stemExportSeconds = stemRenderSeconds,
                        energyAnalysisSeconds = 0,
                        scopeOverlayEncodeSeconds = compositionSeconds,
                        overallSeconds = totalWatch.Elapsed.TotalSeconds,
                        corrscopeWaitSeconds = composer?.LastMetrics.CorrscopeWaitSeconds ?? 0,
                        overlayCpuSeconds = composer?.LastMetrics.OverlayCpuSeconds ?? 0,
                        ffmpegWriteWaitSeconds = composer?.LastMetrics.FfmpegWriteWaitSeconds ?? 0,
                    },
                    scope = scopePlan,
                    warnings = timeline.Warnings,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: visualization cancelled");
            return 7;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: visualization capture failed: {ex.Message}");
            return 7;
        }
    }

    internal static Options Parse(string[] args)
    {
        var options = new Options();
        var reader = new ArgumentReader(args);
        while (reader.HasMore)
        {
            if (reader.TryReadOption(out string name, out string value))
            {
                if (RenderOptionsParser.TryParse(ref reader, name, options))
                    continue;
                switch (name)
                {
                    case "-o":
                    case "--output": options.OutputDir = reader.RequireValue(name); break;
                    case "--overwrite": options.Overwrite = true; break;
                    case "--quiet": options.Quiet = true; break;
                    case "--json": options.Json = true; break;
                    case "--stems-only": options.StemsOnly = true; break;
                    case "--spc-stems": options.SpcStems = true; break;
                    case "--video": options.VideoPath = reader.RequireValue(name); break;
                    case "--corrscope": options.CorrscopePath = reader.RequireValue(name); break;
                    case "--ffmpeg": options.FfmpegPath = reader.RequireValue(name); break;
                    case "--width": options.Width = reader.ReadInt(name); options.WidthExplicit = true; break;
                    case "--height": options.Height = reader.ReadInt(name); options.HeightExplicit = true; break;
                    case "--fps": options.Fps = reader.ReadInt(name); options.FpsExplicit = true; break;
                    case "--fps-denominator": options.FpsDenominator = reader.ReadInt(name); break;
                    case "--tool-timeout-minutes": options.ExternalToolTimeoutMinutes = reader.ReadInt(name); break;
                    case "--title": options.Title = reader.RequireValue(name); break;
                    case "--subtitle": options.Subtitle = reader.RequireValue(name); break;
                    case "--credits": options.Credits = reader.RequireValue(name); break;
                    case "--font": options.FontPath = reader.RequireValue(name); break;
                    case "--duration": options.MaxDuration = reader.ReadDouble(name); break;
                    case "--spc-pitch":
                        string pitch = reader.RequireValue(name).Trim().ToLowerInvariant();
                        options.SpcPitchMode = pitch switch
                        {
                            "estimate" => SpcPitchMode.Estimate,
                            "relative" => SpcPitchMode.Relative,
                            _ => throw new ArgumentException(
                                $"unknown spc pitch mode '{pitch}' (expected estimate or relative)"),
                        };
                        break;
                    case "--final-quality":
                        options.FinalQuality = true;
                        if (!options.WidthExplicit) options.Width = 1920;
                        if (!options.HeightExplicit) options.Height = 1080;
                        if (!options.FpsExplicit) options.Fps = 60;
                        break;
                    case "--encoder":
                        options.Encoder = ParseEncoder(reader.RequireValue(name));
                        break;
                    case "--effects":
                        options.Effects = ParseEffects(reader.RequireValue(name));
                        break;
                    case "--note-color":
                        options.NoteColor = ParseNoteColor(reader.RequireValue(name));
                        break;
                    case "--backend":
                        string backend = reader.RequireValue(name).ToLowerInvariant();
                        if (backend is not ("auto" or "fmp" or "mdplayer"))
                            throw new ArgumentException($"unknown backend '{backend}'");
                        options.Backend = backend;
                        break;
                    case "--scopes":
                        string scopes = reader.RequireValue(name).ToLowerInvariant();
                        if (scopes is not ("auto" or "master" or "device" or "channel" or "off"))
                            throw new ArgumentException($"unknown scope mode '{scopes}'");
                        options.ScopeMode = scopes;
                        break;
                    default: throw new ArgumentException($"unknown option {name}");
                }
            }
            else
            {
                string positional = reader.Next();
                if (positional == "--") continue;
                if (options.Input != null)
                    throw new ArgumentException($"unexpected argument {positional}");
                options.Input = positional;
            }
        }
        if (options.SampleRate <= 0 || options.Loops <= 0
            || options.Fade < 0 || options.Tail < 0 || options.MaxDuration <= 0
            || options.Width < 480 || options.Height < 270
            || options.Fps <= 0 || options.FpsDenominator <= 0
            || options.ExternalToolTimeoutMinutes <= 0)
        {
            throw new ArgumentException("invalid visualization numeric option");
        }
        return options;
    }

    private static VideoEncoder ParseEncoder(string raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "auto" => VideoEncoder.Auto,
            "libx264" or "x264" or "software" or "cpu" => VideoEncoder.LibX264,
            "nvenc" or "h264_nvenc" or "gpu" or "hardware" => VideoEncoder.Nvenc,
            _ => throw new ArgumentException($"unknown encoder '{raw}' (expected libx264 or nvenc)"),
        };

    private static EffectsMode ParseEffects(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "all" => EffectsMode.All,
        "none" => EffectsMode.None,
        _ => throw new ArgumentException($"unknown effects mode '{raw}' (expected all or none)"),
    };

    private static NoteColorMode ParseNoteColor(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "instrument" => NoteColorMode.Instrument,
        "pitch" => NoteColorMode.Pitch,
        "channel" => NoteColorMode.Channel,
        _ => throw new ArgumentException($"unknown note-color mode '{raw}' (expected instrument, pitch, or channel)"),
    };

    internal sealed class Options : RenderSettings
    {
        public string Input { get; set; }
        public string OutputDir { get; set; }
        public bool Overwrite { get; set; }
        public bool Quiet { get; set; }
        public bool Json { get; set; }
        public bool StemsOnly { get; set; }

        /// <summary>
        /// PR 6: also export SPC per-voice mono stems (voice-01.wav .. voice-08.wav)
        /// and the stereo echo stem (echo.wav) next to master.wav. Only applies
        /// when the selected backend is the SPC backend.
        /// </summary>
        public bool SpcStems { get; set; }
        public string VideoPath { get; set; }
        public string CorrscopePath { get; set; }
        public string FfmpegPath { get; set; }
        public int Width { get; set; } = 1440;
        public int Height { get; set; } = 720;
        public int Fps { get; set; } = 30;
        public int FpsDenominator { get; set; } = 1;
        public int ExternalToolTimeoutMinutes { get; set; } = 60;
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Credits { get; set; }
        public string FontPath { get; set; }
        public bool FinalQuality { get; set; }
        public VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;
        public EffectsMode Effects { get; set; } = EffectsMode.All;
        public NoteColorMode NoteColor { get; set; } = NoteColorMode.Instrument;
        public string ScopeMode { get; set; } = "auto";
        public string Backend { get; set; } = "auto";
        /// <summary>
        /// SPC diagnostic option (§25.3): Estimate (default) runs the BRR root
        /// estimator; Relative skips it so instruments keep only relative S-DSP
        /// pitch. Ignored by non-SPC backends; flows through PlaybackOptions.
        /// </summary>
        public SpcPitchMode SpcPitchMode { get; set; } = SpcPitchMode.Estimate;
        public bool WidthExplicit { get; set; }
        public bool HeightExplicit { get; set; }
        public bool FpsExplicit { get; set; }
    }
}
