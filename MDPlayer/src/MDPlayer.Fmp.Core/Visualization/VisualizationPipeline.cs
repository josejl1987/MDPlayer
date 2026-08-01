using System.Diagnostics;
using Fmp.Core.IO;
using Fmp.Core.Rendering;

namespace Fmp.Core.Visualization;

/// <summary>
/// First visualization slice: capture a deterministic musical timeline without
/// rendering scopes or video. Corrscope remains the sole oscilloscope renderer.
/// </summary>
internal sealed class VisualizationPipeline
{
    private readonly FmpRuntimeAssets _assets;
    private readonly IFmpFileSystem _fileSystem;
    private readonly int _sampleRate;

    public VisualizationPipeline(
        FmpRuntimeAssets assets,
        IFmpFileSystem fileSystem = null,
        int sampleRate = 44_100)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _fileSystem = fileSystem;
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
    }

    public sealed class Options
    {
        public int LoopCount { get; set; } = 2;
        public double FadeSeconds { get; set; } = 5.0;
        public double TailSeconds { get; set; } = 0.5;
        public double? MaxDurationSeconds { get; set; } = 300.0;
        public double? TimeoutSeconds { get; set; }
    }

    public sealed class Result
    {
        public bool Success { get; set; }
        public string StopReason { get; set; } = "";
        public string LastError { get; set; } = "";
        public long CapturedSamples { get; set; }
        public VisualizationTimeline Timeline { get; set; }
    }

    public Result Capture(byte[] trackData, string trackFileName, Options options = null)
    {
        ArgumentNullException.ThrowIfNull(trackData);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackFileName);
        options ??= new Options();

        if (options.LoopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.LoopCount));
        if (options.FadeSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options.FadeSeconds));
        if (options.TailSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options.TailSeconds));
        if (options.MaxDurationSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxDurationSeconds));

        var result = new Result();
        var eventSink = new TimelineDecoderEventSink(_sampleRate);
        var sink = new FmpPlaybackEventSinkAdapter(eventSink);
        var runtime = new FmpRuntime(sink, _assets, _fileSystem);
        Stopwatch timeout = options.TimeoutSeconds is > 0 ? Stopwatch.StartNew() : null;

        try
        {
            runtime.Initialize(trackData, trackFileName);
            runtime.SetWaitSamples(_sampleRate * 500 / 1000);
            eventSink.OnLoopBoundary(new TimedLoopBoundary(0, 0));

            int blockSize = Math.Max(1, _sampleRate / 100);
            long totalSamples = 0;
            long maxSamples = options.MaxDurationSeconds.HasValue
                ? SecondsToSamples(options.MaxDurationSeconds.Value)
                : SecondsToSamples(3600);
            var termination = new PlaybackTermination(
                options.LoopCount,
                SecondsToSamples(options.FadeSeconds),
                SecondsToSamples(options.TailSeconds));
            int currentLoop = 0;
            string stopReason = "";

            while (totalSamples < maxSamples)
            {
                if (timeout != null && timeout.Elapsed.TotalSeconds >= options.TimeoutSeconds!.Value)
                {
                    stopReason = "timeout";
                    break;
                }

                if (termination.IsComplete(totalSamples))
                {
                    runtime.Stop();
                    break;
                }

                if (runtime.IsStopped && !termination.Started)
                {
                    if (!string.IsNullOrEmpty(runtime.LastError))
                    {
                        result.Success = false;
                        result.StopReason = "error";
                        result.LastError = runtime.LastError;
                        return result;
                    }
                    stopReason = options.MaxDurationSeconds.HasValue ? "max_duration" : "safety_limit";
                    break;
                }

                int samplesThisBlock = (int)Math.Min(blockSize, maxSamples - totalSamples);
                // A natural end stops the emulator before the explicit tail;
                // a loop-limit fade keeps it running while the fade is audible.
                if (!termination.Started || termination.FadeActive)
                {
                    for (int sample = 0; sample < samplesThisBlock; sample++)
                        runtime.Tick();
                }
                totalSamples += samplesThisBlock;

                if (runtime.CurrentLoop != currentLoop)
                {
                    currentLoop = runtime.CurrentLoop;
                    eventSink.OnLoopBoundary(new TimedLoopBoundary(totalSamples, currentLoop));
                }

                if (!termination.Started)
                {
                    termination.Observe(runtime.PlaybackEnded, currentLoop, totalSamples);
                    if (termination.StopReason == "natural_stop")
                        runtime.Stop();
                }

                if (termination.IsComplete(totalSamples))
                {
                    runtime.Stop();
                    break;
                }
            }

            if (runtime.IsStopped && !string.IsNullOrEmpty(runtime.LastError))
            {
                result.Success = false;
                result.StopReason = "error";
                result.LastError = runtime.LastError;
                return result;
            }

            if (totalSamples >= maxSamples && !termination.IsComplete(totalSamples))
                stopReason = options.MaxDurationSeconds.HasValue ? "max_duration" : "safety_limit";

            if (string.IsNullOrEmpty(stopReason))
                stopReason = !string.IsNullOrEmpty(termination.StopReason)
                    ? termination.StopReason
                    : runtime.IsStopped ? "natural_stop" : "completed";

            result.CapturedSamples = totalSamples;
            result.StopReason = stopReason;
            result.Timeline = eventSink.Complete(
                totalSamples,
                stopReason,
                new TrackMetadata(
                    Path.GetExtension(trackFileName).TrimStart('.').ToLowerInvariant(),
                    Path.GetFileNameWithoutExtension(trackFileName),
                    "fmp",
                    Path.GetFileName(trackFileName)));
            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.StopReason = "error";
            result.LastError = $"{ex.GetType().Name}: {ex.Message}";
            return result;
        }
        finally
        {
            runtime.Stop();
        }
    }

    private long SecondsToSamples(double seconds)
    {
        return checked((long)Math.Ceiling(seconds * _sampleRate));
    }
}
