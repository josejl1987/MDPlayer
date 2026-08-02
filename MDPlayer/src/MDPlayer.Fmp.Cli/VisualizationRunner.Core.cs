using System.Diagnostics;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Core.Analysis;
using Fmp.Core.Audio;
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
internal static partial class VisualizationRunner
{
    private static int RunCore(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationBackendResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(resolution);

        OutputSettings output = request.Output;
        FileInfo input = resolution.Input;
        IPlaybackBackend backend = resolution.Backend;
        PlaybackProbeResult probe = resolution.Probe;
        ProgressJsonlWriter? progress = ProgressJsonlWriter.CreateIfRequested(runtime);
        progress?.Started();
        progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.PreparingInput));
        Stopwatch preparationWatch = Stopwatch.StartNew();
        PreparedTrack? preparedFmpTrack = null;
        if (string.Equals(backend.Id, "fmp", StringComparison.Ordinal))
        {
            try
            {
                preparedFmpTrack = TrackPreparation.Prepare(
                    input.FullName, resolution.FmpComPath,
                    runtime.AssetsDir, resolution.SearchPaths);
            }
            catch (TrackPreparationException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ex.ExitCode;
            }
        }
        preparationWatch.Stop();
        progress?.StageCompleted(
            ProgressJsonlWriter.StageName(ExportStage.PreparingInput),
            preparationWatch.Elapsed.TotalSeconds);

        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);
        if (!output.Overwrite && workspace.HasConflict(true))
        {
            Console.Error.WriteLine(
                $"error: output exists: {workspace.FirstConflict(true)} (use --overwrite)");
            return 9;
        }
        workspace.EnsureDirectories();
        string audioPath = workspace.MasterAudioPath;
        string videoPath = workspace.VideoPath;

        var totalWatch = Stopwatch.StartNew();
        double captureSeconds = 0;
        double stemRenderSeconds = 0;
        double compositionSeconds = 0;
        SinglePassComposer composer = null;
        VideoEncoder requestedEncoder = (VideoEncoder)output.Encoder;
        VideoEncoder effectiveEncoder = requestedEncoder;
        var encoderFallback = new VisualizationSupport.EncoderFallbackState();

        try
        {
            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline));
            Stopwatch captureWatch = Stopwatch.StartNew();

            // Shared preparation: capture → layout → scope → energy → plan.
            PreparedVisualizationSource prepared = VisualizationPrepareCoordinator.Prepare(
                request, runtime, workspace, resolution, preparedFmpTrack);
            captureWatch.Stop();
            captureSeconds = captureWatch.Elapsed.TotalSeconds;
            progress?.StageCompleted(
                ProgressJsonlWriter.StageName(ExportStage.CapturingSemanticTimeline), captureSeconds);

            VisualizationTimeline timeline = prepared.Timeline;
            ResolvedVisualizationLayout resolvedLayout = prepared.Layout;
            OverlayLayout layout = resolvedLayout.Geometry;
            VisualizationScopeArtifacts scopeArtifacts = prepared.Scope;
            ScopeRenderer.ScopeResult scopeResult = scopeArtifacts.Result;

            if (string.Equals(backend.Id, "fmp", StringComparison.Ordinal)
                && scopeResult.SampleRate != timeline.SampleRate)
            {
                Console.Error.WriteLine(
                    $"error: scope/timeline sample-rate mismatch: {scopeResult.SampleRate} vs {timeline.SampleRate}");
                return 7;
            }
            if (string.Equals(backend.Id, "fmp", StringComparison.Ordinal))
            {
                StemPass[] expectedStems = layout.HasScopes
                    ? DefaultStems.All
                    : [DefaultStems.All[0]];
                string[] missingStems = expectedStems
                    .Where(pass => !scopeResult.Stems.Any(stem =>
                        stem.Name == pass.Name && stem.Success
                        && stem.RenderedSamples == scopeResult.MasterSamples
                        && File.Exists(stem.WavPath)))
                    .Select(pass => pass.Name)
                    .ToArray();
                if (scopeResult.MasterSamples <= 0 || missingStems.Length > 0)
                {
                    Console.Error.WriteLine(
                        $"error: Corrscope layout requires synchronized stems: {string.Join(", ", missingStems)}");
                    return 7;
                }
            }

            progress?.StageStarted(ProgressJsonlWriter.StageName(ExportStage.AnalyzingEnergy));
            Stopwatch energyWatch = Stopwatch.StartNew();
            // Energy analysis already ran inside the shared coordinator; report
            // the stage for parity with the previous explicit analysis.
            energyWatch.Stop();
            progress?.StageCompleted(
                ProgressJsonlWriter.StageName(ExportStage.AnalyzingEnergy),
                energyWatch.Elapsed.TotalSeconds);

            using (VisualizationFrameRenderer frameRenderer =
                VisualizationFrameRendererFactory.Create(
                    prepared, workspace, runtime, introOutro: true))
            {
                composer = VisualizationComposition.CreateComposer(
                    runtime, requestedEncoder,
                    output.Quality == RenderQuality.Final ? "veryfast" : "ultrafast",
                    output.Quality == RenderQuality.Final ? "18" : "20");
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
                    composer.Compose(audioPath, videoPath, frameRenderer);
                }
                catch (Exception ex) when (encoderFallback.ShouldRetry(requestedEncoder, ex))
                {
                    progress?.Warning("NVENC runtime failure; retrying with libx264");
                    Console.Error.WriteLine("warning: NVENC runtime failure; retrying with libx264");
                    effectiveEncoder = VideoEncoder.LibX264;
                    composer = VisualizationComposition.CreateComposer(
                        runtime, VideoEncoder.LibX264,
                        output.Quality == RenderQuality.Final ? "veryfast" : "ultrafast",
                        output.Quality == RenderQuality.Final ? "18" : "20");
                    composer.Compose(audioPath, videoPath, frameRenderer);
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
        catch (VisualizationScopeException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
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
}
