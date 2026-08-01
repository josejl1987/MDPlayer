using System.Text.Json;
using Fmp.Core.Audio;
using Fmp.Core.Metadata;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

internal static class VisualizationSupport
{
    internal sealed class EncoderFallbackState
    {
        public string Phase { get; set; } = "none";
        public string Reason { get; set; }
        public string Diagnostics { get; set; }
        public bool Retried { get; set; }

        public bool ShouldRetry(VideoEncoder requested, Exception error)
        {
            if (requested != VideoEncoder.Auto || Retried || error is OperationCanceledException)
                return false;
            string text = error.ToString();
            bool classified = text.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
                || text.Contains("encoder", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
                || text.Contains("pipe", StringComparison.OrdinalIgnoreCase);
            if (!classified) return false;
            Phase = "runtime-fallback";
            Reason = "nvenc-runtime-initialization-or-encode-failure";
            Diagnostics = error.Message;
            Retried = true;
            return true;
        }
    }

    public static VisualizationPresentation ResolvePresentation(VisualizeOptions options, FileInfo input)
    {
        string title = options.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            try
            {
                title = FmpMetadata.FromFmpFile(input.FullName, options.SampleRate, options.Loops, options.Fade).Title;
            }
            catch { title = null; }
        }
        title = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(input.Name)
            : title.Trim();
        return new VisualizationPresentation(
            title,
            string.IsNullOrWhiteSpace(options.Subtitle) ? "" : options.Subtitle.Trim(),
            string.IsNullOrWhiteSpace(options.Credits) ? "" : options.Credits.Trim());
    }

    public static VisualizationTimeline AlignTimelineToAudio(
        VisualizationTimeline timeline,
        long audioEndSample)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (audioEndSample < timeline.StartSample)
            throw new ArgumentOutOfRangeException(nameof(audioEndSample));

        NoteEvent[] notes = timeline.Notes
            .Where(note => note.StartSample < audioEndSample)
            .Select(note => note with
            {
                EndSample = Math.Min(note.EndSample, audioEndSample),
                Pitch = note.Pitch.Where(point => point.SamplePosition < audioEndSample).ToArray(),
            })
            .Where(note => note.EndSample > note.StartSample)
            .ToArray();
        RhythmEvent[] rhythm = timeline.Rhythm
            .Where(evt => evt.SamplePosition < audioEndSample).ToArray();
        Ppz8Event[] ppz8 = timeline.Ppz8
            .Where(evt => evt.StartSample < audioEndSample)
            .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
            .Where(evt => evt.EndSample > evt.StartSample).ToArray();
        AdpcmBEvent[] adpcm = timeline.AdpcmB
            .Where(evt => evt.StartSample < audioEndSample)
            .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
            .Where(evt => evt.EndSample > evt.StartSample).ToArray();

        return new VisualizationTimeline
        {
            SampleRate = timeline.SampleRate,
            StartSample = timeline.StartSample,
            EndSample = audioEndSample,
            SchemaVersion = timeline.SchemaVersion,
            Source = timeline.Source,
            StopReason = timeline.StopReason,
            Devices = timeline.Devices,
            Voices = timeline.Voices,
            Notes = notes,
            Rhythm = rhythm,
            Ppz8 = ppz8,
            AdpcmB = adpcm,
            Timing = timeline.Timing.Where(evt => evt.SamplePosition < audioEndSample).ToArray(),
            LoopMarkers = timeline.LoopMarkers.Where(marker => marker.SamplePosition < audioEndSample).ToArray(),
            Instruments = timeline.Instruments,
            Capabilities = timeline.Capabilities,
            Warnings = timeline.Warnings,
        };
    }

    public static IProgress<ScopeProgress> CreateProgressReporter(bool quiet)
        => quiet ? new NullScopeProgressReporter() : new ConsoleScopeProgressReporter();

    public static void WriteSummary(
        VisualizeOptions options,
        string timelinePath,
        string videoPath,
        VisualizationPipeline.Result capture,
        ScopeRenderer.ScopeResult scope,
        double captureSeconds = 0,
        double stemRenderSeconds = 0,
        double energySeconds = 0,
        double preparationSeconds = 0,
        double compositionSeconds = 0,
        double totalSeconds = 0,
        SinglePassComposer.ComposeMetrics composeMetrics = null,
        EncoderFallbackState encoderFallback = null)
    {
        if (!options.Quiet)
        {
            Console.WriteLine($"Visualization events written to: {timelinePath}");
            Console.WriteLine($"  Samples: {capture.CapturedSamples}");
            Console.WriteLine($"  Notes: {capture.Timeline.Notes.Count}");
            Console.WriteLine($"  Rhythm events: {capture.Timeline.Rhythm.Count}");
            Console.WriteLine($"  Instruments: {capture.Timeline.Instruments.Count}");
            if (videoPath != null)
                Console.WriteLine($"Visualization video written to: {videoPath}\n" +
                    $"  Preparation: {preparationSeconds:F1}s  Audio capture: {captureSeconds:F1}s  " +
                    $"Stem export: {stemRenderSeconds:F1}s  Energy: {energySeconds:F1}s  " +
                    $"Scope+overlay+encode: {compositionSeconds:F1}s  Total: {totalSeconds:F1}s");
            if (encoderFallback?.Retried == true)
                Console.Error.WriteLine($"encoder fallback: {encoderFallback.Reason} ({encoderFallback.Diagnostics})");
        }

        if (!options.Json)
            return;
        double trackDurationSeconds = scope?.SampleRate > 0
            ? scope.MasterSamples / (double)scope.SampleRate : 0;
        long outputSizeBytes = !string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath)
            ? new FileInfo(videoPath).Length : 0;
        double effectiveFps = compositionSeconds > 0 && scope?.SampleRate > 0
            ? Math.Ceiling(scope.MasterSamples * options.Fps / (double)scope.SampleRate) / compositionSeconds : 0;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true,
            timeline = timelinePath,
            video = videoPath,
            samples = capture.CapturedSamples,
            audioSamples = scope?.MasterSamples,
            stopReason = capture.StopReason,
            notes = capture.Timeline.Notes.Count,
            rhythmEvents = capture.Timeline.Rhythm.Count,
            instruments = capture.Timeline.Instruments.Count,
            encoder = options.Encoder == VideoEncoder.Nvenc ? "h264_nvenc" : "libx264",
            encoderFallbackPhase = encoderFallback?.Phase ?? "none",
            encoderFallbackReason = encoderFallback?.Reason,
            encoderFallbackDiagnostics = encoderFallback?.Diagnostics,
            outputSizeBytes,
            trackDurationSeconds,
            effectiveOutputFps = effectiveFps,
            realTimeFactor = trackDurationSeconds > 0 && compositionSeconds > 0
                ? trackDurationSeconds / compositionSeconds : 0,
            stages = new
            {
                timelineCaptureSeconds = captureSeconds,
                stemExportSeconds = stemRenderSeconds,
                energyAnalysisSeconds = energySeconds,
                scopeOverlayEncodeSeconds = compositionSeconds,
                overallSeconds = totalSeconds,
                corrscopeWaitSeconds = composeMetrics?.CorrscopeWaitSeconds ?? 0,
                overlayCpuSeconds = composeMetrics?.OverlayCpuSeconds ?? 0,
                ffmpegWriteWaitSeconds = composeMetrics?.FfmpegWriteWaitSeconds ?? 0,
                maxQueueDepth = composeMetrics?.MaxQueueDepth ?? 0,
                queueCapacity = composeMetrics?.QueueCapacity ?? 3,
                starvationCount = composeMetrics?.StarvationCount ?? 0,
                blockingSeconds = composeMetrics?.BlockingSeconds ?? 0,
                pipelineWallTimeSeconds = composeMetrics?.WallTimeSeconds ?? 0,
                frameCount = composeMetrics?.FrameCount ?? 0,
            },
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class NullScopeProgressReporter : IProgress<ScopeProgress>
    {
        public void Report(ScopeProgress value) { }
    }

    private sealed class ConsoleScopeProgressReporter : IProgress<ScopeProgress>
    {
        private readonly object _gate = new();
        public void Report(ScopeProgress value)
        {
            lock (_gate)
            {
                string status = value.Completed ? "completed" : "rendering";
                Console.WriteLine($"scope {value.StemName}: {value.RenderedSamples} samples, " +
                    $"elapsed {value.Elapsed.TotalSeconds:0.0}s, {status}");
            }
        }
    }
}
