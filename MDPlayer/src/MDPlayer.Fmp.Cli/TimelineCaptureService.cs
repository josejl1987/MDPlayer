using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Captures a <see cref="VisualizationTimeline"/> for an input track file.
/// Shared by the analysis and MIDI command paths so they agree on how a raw
/// input becomes symbolic timeline data. Accepts either a runnable track or
/// an existing timeline JSON path.
/// </summary>
internal static class TimelineCaptureService
{
    /// <summary>
    /// Captures (or loads) the timeline for <paramref name="inputPath"/>.
    /// When <paramref name="timelinePath"/> is non-null and exists, that file is
    /// loaded instead of capturing (mirrors the analyze <c>--timeline</c> reuse).
    /// </summary>
    public static VisualizationTimeline Capture(
        string inputPath,
        string? timelinePath,
        BatchRenderSettings settings,
        Dictionary<string, string>? captureDependencies = null)
    {
        if (timelinePath != null && File.Exists(timelinePath))
            return CaptureSerializedTimeline(timelinePath, settings);

        var input = new FileInfo(inputPath);
        if (FmpFormat.IsSupportedExtension(input.Extension))
            return CaptureFmpTimeline(input, settings, captureDependencies);

        return CaptureBackendTimeline(input, settings, captureDependencies);
    }

    /// <summary>
    /// Loads a serialized timeline and routes it through the SAME producer-clock
    /// normalization boundary as a decoder capture. The serialized timeline's
    /// declared <see cref="VisualizationTimeline.SampleRate"/> is the producer
    /// source clock; this capture's <c>settings.SampleRate</c> is the destination
    /// playback/output clock. <see cref="TimelineBuilder.Merge"/> converts every
    /// timed event family exactly once (spec §4.1) and rejects an ambiguous/unknown
    /// source clock with an actionable <see cref="Fmp.Core.Timing.MusicalTimingException"/>
    /// — never a silent clock-equality assumption.
    /// </summary>
    private static VisualizationTimeline CaptureSerializedTimeline(
        string timelinePath,
        BatchRenderSettings settings)
    {
        VisualizationTimeline loaded = VisualizationJsonWriter.Read(timelinePath);
        // Merge validates/derives the clock converter up front, so an ambiguous
        // source rate throws before any event is consumed.
        var builder = new TimelineBuilder(settings.SampleRate);
        builder.Merge(loaded);
        long endSample = ProducerClockNormalization.ConvertSamplePosition(
            "serialized-timeline",
            loaded.EndSample,
            loaded.SampleRate,
            settings.SampleRate);
        long startSample = ProducerClockNormalization.ConvertSamplePosition(
            "serialized-timeline",
            loaded.StartSample,
            loaded.SampleRate,
            settings.SampleRate);
        // Preserve the serialized timeline's [StartSample, EndSample] range on the
        // destination clock while every event sample was normalized through the
        // same boundary above. Rebuilding with StartSample=0 would silently reset
        // a valid nonzero serialized start range while event samples stay absolute
        // (spec §4.1 song-range contract).
        return builder.Build(endSample, loaded.StopReason, loaded.Source, startSample);
    }

    private static VisualizationTimeline CaptureFmpTimeline(
        FileInfo input,
        BatchRenderSettings settings,
        Dictionary<string, string>? captureDependencies)
    {
        PreparedTrack track = TrackPreparation.Prepare(
            input.FullName, settings.FmpCom, settings.AssetsDir, settings.SearchPaths);
        captureDependencies?.TrySet("input", () => TimelineCaptureFileIdentity.FileIdentity(track.Input.FullName));
        captureDependencies?.TrySet("fmpCom", () => TimelineCaptureFileIdentity.FileIdentity(track.Assets.FmpComPath));
        captureDependencies?.TrySet("virtualFileSystem", () => string.Join("|", track.FileSystem.SearchPaths));

        var capture = new VisualizationPipeline(track.Assets, track.FileSystem, settings.SampleRate).Capture(
            track.Data,
            track.Input.FullName,
            new VisualizationPipeline.Options
            {
                LoopCount = settings.Loops,
                FadeSeconds = settings.Fade,
                TailSeconds = settings.Tail,
                MaxDurationSeconds = settings.MaxDuration,
                TimeoutSeconds = settings.Timeout,
            });
        if (!capture.Success || capture.Timeline == null)
            throw new InvalidOperationException($"visualization capture failed: {capture.LastError}");
        return capture.Timeline;
    }

    private static VisualizationTimeline CaptureBackendTimeline(
        FileInfo input,
        BatchRenderSettings settings,
        Dictionary<string, string>? captureDependencies)
    {
        IReadOnlyList<string> searchPaths = VisualizationBackendResolver.BuildSearchPaths(
            input, settings.SearchPaths, settings.AssetsDir, settings.FmpCom);
        var environment = new PlaybackEnvironment(searchPaths, true, settings.SampleRate);
        string fmpCom = PlaybackBackendRegistry.ResolveFmpCom(settings.FmpCom, searchPaths);
        PlaybackBackendRegistry registry = PlaybackBackendRegistry.CreateDefault(environment, fmpCom);
        if (!registry.TrySelect(
                input,
                environment,
                "auto",
                out IPlaybackBackend backend,
                out PlaybackProbeResult probe))
        {
            string details = probe.Warnings.Count == 0
                ? "no playback backend accepted the input"
                : string.Join("; ", probe.Warnings);
            throw new TrackPreparationException(
                $"unsupported format: {input.Extension.ToLowerInvariant()} ({details})", 3);
        }
        if (!probe.Visualizable)
            throw new TrackPreparationException(
                "MDPlayer can play this track, but none of its active devices expose supported note data", 3);

        captureDependencies?.TrySet("input", () => TimelineCaptureFileIdentity.FileIdentity(input.FullName));
        int timelineSampleRate = probe.NativeSampleRate > 0
            ? probe.NativeSampleRate
            : settings.SampleRate;
        var eventSink = new TimelineDecoderEventSink(timelineSampleRate);
        using IPlaybackCaptureSession session = backend.Open(
            input,
            new PlaybackOptions(
                settings.Loops,
                settings.Fade,
                settings.Tail,
                settings.MaxDuration,
                OutputAudioPath: null,
                settings.SampleRate,
                WriteSpcStems: false,
                SpcPitchMode.Estimate,
                settings.SsgGainDb),
            eventSink);
        session.Run();
        VisualizationTimeline timeline = eventSink.Complete(
            session.SamplePosition,
            "completed",
            new TrackMetadata(
                input.Extension.TrimStart('.').ToLowerInvariant(),
                Path.GetFileNameWithoutExtension(input.Name),
                backend.Id,
                input.Name));
        if (!VisualizationContentAvailability.HasRenderableContent(timeline))
            throw new TrackPreparationException(
                "visualization capture contains neither semantic events nor waveform activity", 3);
        return timeline;
    }
}

internal static class CaptureDependencyExtensions
{
    /// <summary>Sets a lazily-computed dependency only when the dictionary is present.</summary>
    public static void TrySet(this Dictionary<string, string>? dependencies, string key, Func<string> value)
    {
        if (dependencies is not null)
            dependencies[key] = value();
    }
}

internal static class TimelineCaptureFileIdentity
{
    /// <summary>Stable file identity used to key capture caches.</summary>
    public static string FileIdentity(string path)
    {
        var info = new FileInfo(path);
        string hash;
        using (var stream = File.OpenRead(path))
        using (var incremental = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                incremental.AppendData(buffer, 0, read);
            hash = Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
        }
        return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{hash}";
    }
}
