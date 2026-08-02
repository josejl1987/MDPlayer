using Fmp.Application.Contracts;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

internal sealed record VisualizationCaptureArtifacts(
    VisualizationTimeline Timeline,
    long MasterSamples,
    int SampleRate,
    bool MasterAudioProduced);

internal static class VisualizationCaptureCoordinator
{
    public static VisualizationCaptureArtifacts Capture(
        VisualizationBackendResolution resolution,
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        PreparedTrack? preparedFmpTrack)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(workspace);

        return string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal)
            ? CaptureFmp(preparedFmpTrack ?? throw new InvalidOperationException("FMP track was not prepared"), request)
            : CaptureBackend(resolution, request, workspace);
    }

    internal static VisualizationCaptureArtifacts CaptureFmpForTest(
        PreparedTrack track, VisualizationRequest request)
        => CaptureFmp(track, request);

    private static VisualizationCaptureArtifacts CaptureFmp(
        PreparedTrack track, VisualizationRequest request)
    {
        PlaybackSettings playback = request.Playback;
        VisualizationPipeline.Result result = new VisualizationPipeline(
            track.Assets, track.FileSystem, playback.SampleRate).Capture(
                track.Data, track.Input.FullName,
                new VisualizationPipeline.Options
                {
                    LoopCount = playback.LoopCount,
                    FadeSeconds = playback.FadeSeconds,
                    TailSeconds = playback.TailSeconds,
                    MaxDurationSeconds = playback.MaximumDurationSeconds ?? 300,
                });

        if (!result.Success || result.Timeline is null)
            throw new VisualizationExecutionException(
                $"visualization capture failed: {result.LastError}", 7);

        return new VisualizationCaptureArtifacts(
            result.Timeline, result.Timeline.EndSample,
            result.Timeline.SampleRate, MasterAudioProduced: false);
    }

    private static VisualizationCaptureArtifacts CaptureBackend(
        VisualizationBackendResolution resolution,
        VisualizationRequest request,
        VisualizationWorkspace workspace)
    {
        PlaybackSettings playback = request.Playback;
        int sampleRate = resolution.Probe.NativeSampleRate > 0
            ? resolution.Probe.NativeSampleRate : playback.SampleRate;
        var eventSink = new TimelineDecoderEventSink(sampleRate);
        using IPlaybackCaptureSession session = resolution.Backend.Open(
            resolution.Input,
            new PlaybackOptions(
                playback.LoopCount, playback.FadeSeconds, playback.TailSeconds,
                playback.MaximumDurationSeconds ?? 300,
                workspace.MasterAudioPath, playback.SampleRate, WriteSpcStems: true,
                MapSpcPitch(playback.SpcPitch), playback.SsgGainDb), eventSink);
        session.Run();
        VisualizationTimeline timeline = eventSink.Complete(
            session.SamplePosition, "completed", new TrackMetadata(
                resolution.Input.Extension.TrimStart('.').ToLowerInvariant(),
                Path.GetFileNameWithoutExtension(resolution.Input.Name),
                resolution.Backend.Id, resolution.Input.Name));
        return new VisualizationCaptureArtifacts(
            timeline, session.SamplePosition, sampleRate, MasterAudioProduced: true);
    }

    private static SpcPitchMode MapSpcPitch(SpcPitchInterpretation pitch) => pitch switch
    {
        SpcPitchInterpretation.Estimate => SpcPitchMode.Estimate,
        SpcPitchInterpretation.Relative => SpcPitchMode.Relative,
        _ => throw new ArgumentOutOfRangeException(nameof(pitch), pitch, null),
    };
}
