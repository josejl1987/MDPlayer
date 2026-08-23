using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Single command-result model shared by every visualization backend path.
/// Both the FMP runner and the generic runner build this same shape so that
/// human output and JSON output are structurally identical across backends.
/// Fields that do not apply to a given job remain null/zero/empty rather than
/// being omitted, keeping the emitted JSON property set stable.
/// </summary>
internal sealed record VisualizationCommandResult
{
    public bool Success { get; init; } = true;
    public int ExitCode { get; init; }
    public string Backend { get; init; }
    public string Availability { get; init; }
    public bool Portable { get; init; }

    public string TimelinePath { get; init; }
    public string MasterAudioPath { get; init; }
    public string VideoPath { get; init; }

    public int SampleRate { get; init; }
    public long TimelineSamples { get; init; }
    public long AudioSamples { get; init; }

    public int DeviceCount { get; init; }
    public int NoteCount { get; init; }
    public int InstrumentCount { get; init; }

    public string Encoder { get; init; }
    /// <summary>Semantic raster backend; always "Cpu" (the GPU experiment was removed).</summary>
    public string Renderer { get; init; }
    public EncoderFallbackResult? EncoderFallback { get; init; }
    public long OutputSizeBytes { get; init; }
    public double TrackDurationSeconds { get; init; }
    public double EffectiveOutputFps { get; init; }
    public double RealTimeFactor { get; init; }

    public ScopeResultSummary Scope { get; init; }
    public VisualizationStageMetrics Stages { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Error { get; init; }
}

/// <summary>Scope-plane summary: what was requested and what actually ran.</summary>
internal sealed record ScopeResultSummary(
    string Requested,
    string Execution,
    int Stems);

internal sealed record EncoderFallbackResult(
    string Phase,
    string Reason,
    string Diagnostics,
    bool Retried);

internal sealed record VisualizationStageMetrics
{
    public double BackendResolutionSeconds { get; init; }
    public double PreparationSeconds { get; init; }
    public double SourcePlaybackStateSeconds { get; init; }
    public double TimelineCaptureSeconds { get; init; }
    public double StemExportSeconds { get; init; }
    public double AudioProcessingSeconds { get; init; }
    public double EnergyAnalysisSeconds { get; init; }
    public double ScopeOverlayEncodeSeconds { get; init; }
    public double OverallSeconds { get; init; }
    public double CorrscopeWaitSeconds { get; init; }
    public double OverlayCpuSeconds { get; init; }
    public double FfmpegWriteWaitSeconds { get; init; }
    public double PixelConversionSeconds { get; init; }
    public double MuxFinalizationSeconds { get; init; }
    public int MaxQueueDepth { get; init; }
    public long StarvationCount { get; init; }
    public double BlockingSeconds { get; init; }
    public double PipelineWallTimeSeconds { get; init; }
    public long FrameCount { get; init; }
    public double ScopeFrameReadSeconds { get; init; }
    public double RenderSeconds { get; init; }
    public double DynamicLayerSeconds { get; init; }
    public double FrameStateUpdateSeconds { get; init; }
    public double CompositingSeconds { get; init; }
    public double LayoutSeconds { get; init; }
    public double StaticLayerSeconds { get; init; }
    public double TextSeconds { get; init; }
    public double PianoRollSeconds { get; init; }
    public double PitchGridSeconds { get; init; }
    public double PitchBandSeconds { get; init; }
    public double GridLineSeconds { get; init; }
    public double RibbonSeconds { get; init; }
    public double RibbonDecorationSeconds { get; init; }
    public long RibbonColumnsEvaluated { get; init; }
    public long RibbonPixelsBlended { get; init; }
    public long PitchSegmentsVisited { get; init; }
    public double WaveformSeconds { get; init; }
    public long FullRedraws { get; init; }
    public long PartialRedraws { get; init; }
    public long UnchangedFrames { get; init; }
    public long RenderedPixels { get; init; }
    public long AvoidedPixels { get; init; }
    public long SurfaceCopies { get; init; }
    public long FullFrameCopies { get; init; }
    public long ScopeCopies { get; init; }
    public long CopiedBytes { get; init; }
    public long SourceCursorAdvances { get; init; }
    public long PianoRollCursorAdvances { get; init; }
    public long VisibleNotesVisited { get; init; }
    public long AllocatedBytes { get; init; }
    public double AllocatedBytesPerFrame { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public double QueueWaitSeconds { get; init; }
    public double RendererIdleSeconds { get; init; }
    public double RendererBlockedSeconds { get; init; }
    public double EncoderIdleSeconds { get; init; }
    public double EncoderBlockedSeconds { get; init; }
    public double GpuDrawSeconds { get; init; }
    public double GpuFlushSyncSeconds { get; init; }
    public double GpuReadbackSeconds { get; init; }
    public double ScopeUploadSeconds { get; init; }
}

/// <summary>
/// Single serializer for visualization command results. Deserializes a
/// <see cref="VisualizationCommandResult"/> into identical human-readable and
/// JSON forms for every backend. Human output is emitted only when requested;
/// JSON output is controlled by the render runtime options.
/// </summary>
/// <summary>
/// Shared builder that maps the workspace, timeline, scope, and composer
/// artifacts into the single <see cref="VisualizationCommandResult"/>. Both
/// runners call this so the emitted shape is identical regardless of backend.
/// </summary>
internal static class VisualizationResultBuilder
{
    public static VisualizationCommandResult Build(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationPipeline.Result capture,
        ScopeRenderer.ScopeResult scope,
        string backend,
        string availability,
        bool portable,
        string encoder,
        EncoderFallbackResult encoderFallback,
        double preparationSeconds,
        double captureSeconds,
        double stemExportSeconds,
        double energySeconds,
        double compositionSeconds,
        double backendResolutionSeconds,
        double overallSeconds,
        SinglePassComposer.ComposeMetrics composeMetrics,
        IReadOnlyList<string> warnings,
        string error = null)
    {
        VisualizationTimeline timeline = capture?.Timeline;
        return BuildCore(
            request, runtime, workspace, timeline, scope,
            backend, availability, portable, encoder, encoderFallback,
            preparationSeconds, captureSeconds, stemExportSeconds, energySeconds,
            compositionSeconds, backendResolutionSeconds, overallSeconds,
            composeMetrics, warnings, error ?? capture?.LastError,
            timelineSamples: capture?.CapturedSamples ?? timeline?.EndSample ?? 0);
    }

    /// <summary>
    /// Generic-path builder: capture is a <see cref="VisualizationTimeline"/>
    /// plus a master WAV rather than an FMP pipeline result. Same model and
    /// writer, same emitted JSON shape.
    /// </summary>
    public static VisualizationCommandResult BuildGeneric(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationTimeline timeline,
        ScopeRenderer.ScopeResult scope,
        string backend,
        string availability,
        bool portable,
        string encoder,
        EncoderFallbackResult encoderFallback,
        double captureSeconds,
        double stemExportSeconds,
        double compositionSeconds,
        double backendResolutionSeconds,
        double overallSeconds,
        SinglePassComposer.ComposeMetrics composeMetrics,
        IReadOnlyList<string> warnings,
        string error = null) =>
        BuildCore(
            request, runtime, workspace, timeline, scope,
            backend, availability, portable, encoder, encoderFallback,
            preparationSeconds: backendResolutionSeconds, captureSeconds, stemExportSeconds,
            energySeconds: 0, compositionSeconds, backendResolutionSeconds, overallSeconds,
            composeMetrics, warnings, error,
            timelineSamples: timeline?.EndSample ?? 0);

    private static VisualizationCommandResult BuildCore(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationTimeline timeline,
        ScopeRenderer.ScopeResult scope,
        string backend,
        string availability,
        bool portable,
        string encoder,
        EncoderFallbackResult encoderFallback,
        double preparationSeconds,
        double captureSeconds,
        double stemExportSeconds,
        double energySeconds,
        double compositionSeconds,
        double backendResolutionSeconds,
        double overallSeconds,
        SinglePassComposer.ComposeMetrics composeMetrics,
        IReadOnlyList<string> warnings,
        string error,
        long timelineSamples)
    {
        int sampleRate = scope?.SampleRate ?? timeline?.SampleRate ?? request.Playback.SampleRate;
        long audioSamples = scope?.MasterSamples ?? 0;
        double trackDurationSeconds = sampleRate > 0
            ? audioSamples / (double)sampleRate : 0;
        long outputSizeBytes = !string.IsNullOrWhiteSpace(workspace.VideoPath)
            && File.Exists(workspace.VideoPath)
                ? new FileInfo(workspace.VideoPath).Length : 0;

        return new VisualizationCommandResult
        {
            Success = true,
            ExitCode = 0,
            Backend = backend,
            Availability = availability,
            Portable = portable,
            TimelinePath = workspace.TimelinePath,
            MasterAudioPath = workspace.MasterAudioPath,
            VideoPath = workspace.VideoPath,
            SampleRate = sampleRate,
            TimelineSamples = timelineSamples,
            AudioSamples = audioSamples,
            DeviceCount = timeline?.Devices.Count ?? 0,
            NoteCount = timeline?.Notes.Count ?? 0,
            InstrumentCount = timeline?.Instruments.Count ?? 0,
            Encoder = encoder,
            Renderer = composeMetrics?.Renderer?.BackendName ?? "Cpu",
            EncoderFallback = encoderFallback,
            OutputSizeBytes = outputSizeBytes,
            TrackDurationSeconds = trackDurationSeconds,
            EffectiveOutputFps = compositionSeconds > 0 && sampleRate > 0
                ? Math.Ceiling(audioSamples * request.Output.FpsNumerator / (double)sampleRate) / compositionSeconds
                : 0,
            RealTimeFactor = trackDurationSeconds > 0 && compositionSeconds > 0
                ? trackDurationSeconds / compositionSeconds : 0,
            Scope = BuildScope("channel", false, scope),
            Stages = BuildStages(preparationSeconds, captureSeconds, stemExportSeconds,
                energySeconds, compositionSeconds, backendResolutionSeconds, overallSeconds, composeMetrics),
            Warnings = warnings,
            Error = error,
        };
    }

    private static ScopeResultSummary BuildScope(
        string requested,
        bool stemsOnly,
        ScopeRenderer.ScopeResult scope)
    {
        if (string.Equals(requested, "off", StringComparison.Ordinal))
            return new ScopeResultSummary("off", "disabled", 0);
        if (stemsOnly || scope is null)
            return new ScopeResultSummary(requested, "stems", scope?.Stems.Count ?? 0);
        return new ScopeResultSummary(requested, "scopes", scope.Stems.Count);
    }

    private static VisualizationStageMetrics BuildStages(
        double preparation,
        double capture,
        double stemExport,
        double energy,
        double composition,
        double backendResolution,
        double overall,
        SinglePassComposer.ComposeMetrics m) => new()
        {
            BackendResolutionSeconds = backendResolution,
            PreparationSeconds = preparation,
            SourcePlaybackStateSeconds = capture,
            TimelineCaptureSeconds = capture,
            StemExportSeconds = stemExport,
            AudioProcessingSeconds = stemExport,
            EnergyAnalysisSeconds = energy,
            ScopeOverlayEncodeSeconds = composition,
            OverallSeconds = overall,
            CorrscopeWaitSeconds = m?.CorrscopeWaitSeconds ?? 0,
            OverlayCpuSeconds = m?.OverlayCpuSeconds ?? 0,
            FfmpegWriteWaitSeconds = m?.FfmpegWriteWaitSeconds ?? 0,
            PixelConversionSeconds = 0,
            MuxFinalizationSeconds = m?.MuxFinalizationSeconds ?? 0,
            MaxQueueDepth = m?.MaxQueueDepth ?? 0,
            StarvationCount = m?.StarvationCount ?? 0,
            BlockingSeconds = m?.BlockingSeconds ?? 0,
            PipelineWallTimeSeconds = m?.WallTimeSeconds ?? 0,
            FrameCount = m?.FrameCount ?? 0,
            ScopeFrameReadSeconds = m?.ScopeFrameReadSeconds ?? 0,
            RenderSeconds = m?.Renderer?.RenderSeconds ?? 0,
            DynamicLayerSeconds = m?.Renderer?.DynamicSeconds ?? 0,
            FrameStateUpdateSeconds = m?.Renderer?.FrameStateSeconds ?? 0,
            CompositingSeconds = m?.Renderer?.CompositingSeconds ?? 0,
            LayoutSeconds = m?.Renderer?.LayoutSeconds ?? 0,
            StaticLayerSeconds = m?.Renderer?.StaticLayerSeconds ?? 0,
            TextSeconds = m?.Renderer?.TextSeconds ?? 0,
            PianoRollSeconds = m?.Renderer?.PianoRollSeconds ?? 0,
            PitchGridSeconds = m?.Renderer?.PitchGridSeconds ?? 0,
            PitchBandSeconds = m?.Renderer?.PitchBandSeconds ?? 0,
            GridLineSeconds = m?.Renderer?.GridLineSeconds ?? 0,
            RibbonSeconds = m?.Renderer?.RibbonSeconds ?? 0,
            RibbonDecorationSeconds = m?.Renderer?.RibbonDecorationSeconds ?? 0,
            RibbonColumnsEvaluated = m?.Renderer?.RibbonColumnsEvaluated ?? 0,
            RibbonPixelsBlended = m?.Renderer?.RibbonPixelsBlended ?? 0,
            PitchSegmentsVisited = m?.Renderer?.PitchSegmentsVisited ?? 0,
            WaveformSeconds = m?.Renderer?.WaveformSeconds ?? 0,
            FullRedraws = m?.Renderer?.FullRedraws ?? 0,
            PartialRedraws = m?.Renderer?.PartialRedraws ?? 0,
            UnchangedFrames = m?.Renderer?.UnchangedFrames ?? 0,
            RenderedPixels = m?.Renderer?.RenderedPixels ?? 0,
            AvoidedPixels = m?.Renderer?.AvoidedPixels ?? 0,
            SurfaceCopies = m?.Renderer?.SurfaceCopies ?? 0,
            FullFrameCopies = m?.Renderer?.FullFrameCopies ?? 0,
            ScopeCopies = m?.Renderer?.ScopeCopies ?? 0,
            CopiedBytes = m?.Renderer?.CopiedBytes ?? 0,
            SourceCursorAdvances = m?.Renderer?.SourceCursorAdvances ?? 0,
            PianoRollCursorAdvances = m?.Renderer?.PianoRollCursorAdvances ?? 0,
            VisibleNotesVisited = m?.Renderer?.VisibleNotesVisited ?? 0,
            AllocatedBytes = m?.Renderer?.AllocatedBytes ?? 0,
            AllocatedBytesPerFrame = m?.Renderer?.AllocatedBytesPerFrame ?? 0,
            PeakWorkingSetBytes = m?.Renderer?.PeakWorkingSetBytes ?? 0,
            QueueWaitSeconds = m?.QueueWaitSeconds ?? 0,
            RendererIdleSeconds = m?.QueueWaitSeconds ?? 0,
            RendererBlockedSeconds = m?.RendererBlockedSeconds ?? 0,
            EncoderIdleSeconds = m?.EncoderIdleSeconds ?? 0,
            EncoderBlockedSeconds = m?.FfmpegWriteWaitSeconds ?? 0,
            GpuDrawSeconds = m?.Renderer?.GpuDrawSeconds ?? 0,
            GpuFlushSyncSeconds = m?.Renderer?.GpuFlushSyncSeconds ?? 0,
            GpuReadbackSeconds = m?.Renderer?.GpuReadbackSeconds ?? 0,
            ScopeUploadSeconds = m?.Renderer?.ScopeUploadSeconds ?? 0,
        };
}

internal static class VisualizationResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(
        VisualizationCommandResult result,
        bool humanReadable,
        bool json,
        bool quiet,
        TextWriter output = null)
    {
        output ??= Console.Out;
        if (humanReadable && !quiet)
            WriteHumanReadable(result, output);

        if (json)
            output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static void WriteHumanReadable(VisualizationCommandResult result, TextWriter output)
    {
        output.WriteLine($"Visualization events written to: {result.TimelinePath}");
        output.WriteLine($"  Samples: {result.TimelineSamples}");
        output.WriteLine($"  Notes: {result.NoteCount}");
        output.WriteLine($"  Instruments: {result.InstrumentCount}");
        if (!string.IsNullOrEmpty(result.MasterAudioPath))
            output.WriteLine($"Master WAV written to: {result.MasterAudioPath}");
        if (!string.IsNullOrEmpty(result.VideoPath))
        {
            output.WriteLine($"Visualization video written to: {result.VideoPath}\n");
            VisualizationStageMetrics stages = result.Stages;
            string line = $"  Preparation: {stages.PreparationSeconds:F1}s  "
                + $"Timeline capture: {stages.TimelineCaptureSeconds:F1}s  "
                + $"Stem export: {stages.StemExportSeconds:F1}s  "
                + $"Energy: {stages.EnergyAnalysisSeconds:F1}s  "
                + $"Composition: {stages.ScopeOverlayEncodeSeconds:F1}s  "
                + $"Total: {stages.OverallSeconds:F1}s";
            output.WriteLine(line);
        }
        if (result.EncoderFallback is { Retried: true })
            Console.Error.WriteLine($"encoder fallback: {result.EncoderFallback.Reason} ({result.EncoderFallback.Diagnostics})");
        foreach (string warning in result.Warnings)
            Console.Error.WriteLine($"warning: {warning}");
    }
}
