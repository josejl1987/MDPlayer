using System.Diagnostics;
using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;
using Fmp.Core.Tracing;

namespace Fmp.Core.Rendering;

/// <summary>
/// Pull-based deterministic renderer. Drives FMP emulation + MDSound synthesis
/// and writes PCM to WAV. Handles loop count, fade, tail, and safety limits.
/// Supports optional register tracing (JSONL) and wall-clock timeout.
/// </summary>
internal class FmpRenderer
{
    private readonly FmpRuntime _runtime;
    private readonly MdsoundFmpChipSink _sink;
    private readonly int _sampleRate;

    public FmpRenderer(FmpRuntimeAssets assets, IFmpFileSystem fileSystem = null, int sampleRate = 44100, double ssgGainDb = 0)
    {
        _sampleRate = sampleRate;
        _sink = new MdsoundFmpChipSink(sampleRate, ssgGainDb: ssgGainDb);
        _runtime = new FmpRuntime(_sink, assets, fileSystem);
    }

    /// <summary>
    /// Render options.
    /// </summary>
    public class Options
    {
        public int LoopCount { get; set; } = 2;
        public double FadeSeconds { get; set; } = 5.0;
        public double TailSeconds { get; set; } = 0.5;
        public double? MaxDurationSeconds { get; set; } = null;
        public double? TimeoutSeconds { get; set; } = null;

        /// <summary>
        /// Path for the register trace JSONL output. If null, tracing is disabled.
        /// </summary>
        public string TracePath { get; set; } = null;
    }

    /// <summary>
    /// Render result information.
    /// </summary>
    public class Result
    {
        public bool Success { get; set; }
        public long RenderedSamples { get; set; }
        public string StopReason { get; set; } = "";
        public string LastError { get; set; } = "";

        /// <summary>Whether playback stopped naturally (FMP finished).</summary>
        public bool NaturallyStopped { get; set; }

        /// <summary>Trace writer reference, if tracing was enabled.</summary>
        public RegisterTraceWriter TraceWriter { get; set; }
    }

    /// <summary>
    /// Render an OVI track to a WAV file.
    /// </summary>
    public Result RenderToWav(byte[] trackData, string trackFileName, string outputWavPath, Options opts = null)
    {
        opts ??= new Options();
        var result = new Result();

        // Optional trace writer — opened BEFORE Initialize to capture boot events
        RegisterTraceWriter traceWriter = null;
        if (!string.IsNullOrEmpty(opts.TracePath))
        {
            try
            {
                traceWriter = new RegisterTraceWriter(opts.TracePath);
                _runtime.TraceWriter = traceWriter;
                result.TraceWriter = traceWriter;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.StopReason = "trace_error";
                result.LastError = $"Failed to open trace: {ex.Message}";
                return result;
            }
        }

        // Wall-clock timeout
        Stopwatch timeoutWatch = null;
        if (opts.TimeoutSeconds.HasValue && opts.TimeoutSeconds.Value > 0)
        {
            timeoutWatch = Stopwatch.StartNew();
        }

        try
        {
            // Start chip synthesis
            _sink.Start();

            // Initialize FMP runtime (boots FMP.COM, loads track)
            // Trace writer is already attached, so boot/init events are captured.
            _runtime.Initialize(trackData, trackFileName);

            // Reproduce the original MDPlayer WaitTime startup latency
            // (WaitTime = 500ms at 44.1kHz). During this period the FMP driver
            // does not tick, so the chip renders its reset state.
            _runtime.SetWaitSamples(_sampleRate * 500 / 1000);

            // Create WAV writer
            using var wav = new WavWriter(outputWavPath, _sampleRate);

            // Render loop
            int bufferSize = _sampleRate / 100; // 10ms buffer
            var outputs = new int[2][] { new int[bufferSize], new int[bufferSize] };
            var interleaved = new short[bufferSize * 2];
            long totalSamples = 0;
            int currentLoop = 0;
            long fadeSamples = checked((long)Math.Ceiling(opts.FadeSeconds * _sampleRate));
            long tailSamples = checked((long)Math.Ceiling(opts.TailSeconds * _sampleRate));
            var termination = new PlaybackTermination(opts.LoopCount, fadeSamples, tailSamples);

            // Hard safety limit: 1 hour even if MaxDurationSeconds is null
            double maxDuration = opts.MaxDurationSeconds ?? 3600.0;
            long maxSamples = checked((long)Math.Ceiling(maxDuration * _sampleRate));

            while (totalSamples < maxSamples)
            {
                // Check wall-clock timeout first
                if (timeoutWatch != null && timeoutWatch.Elapsed.TotalSeconds >= opts.TimeoutSeconds.Value)
                {
                    result.StopReason = "timeout";
                    break;
                }

                if (termination.IsComplete(totalSamples))
                {
                    _runtime.Stop();
                    break;
                }

                if (_runtime.IsStopped && !termination.Started)
                    break;

                // Check trace writer failure (fail-closed)
                if (traceWriter != null && traceWriter.Failed)
                {
                    result.StopReason = "trace_error";
                    result.LastError = $"Trace write failed: {traceWriter.FirstError?.Message}";
                    break;
                }

                // Render PCM from chips, interleaving FMP emulation ticks
                // with sample rendering via the frame callback. MDSound calls
                // the callback once per output stereo sample, matching the
                // original MDPlayer's oneFrameProc pattern.
                int samplesThisBlock = (int)Math.Min(bufferSize, maxSamples - totalSamples);
                Action callback = !termination.Started || termination.FadeActive
                    ? _runtime.Tick
                    : null;
                _sink.Render(outputs, samplesThisBlock, callback);

                // Convert int[][] to interleaved short[]
                for (int i = 0; i < samplesThisBlock; i++)
                {
                    // Clamp to 16-bit range
                    int l = Math.Clamp(outputs[0][i], -32768, 32767);
                    int r = Math.Clamp(outputs[1][i], -32768, 32767);

                    // Apply fade if active
                    if (termination.FadeActive)
                    {
                        long fadePos = totalSamples + i - termination.FadeStartSample;
                        if (fadePos < 0)
                        {
                            // The boundary is discovered at the end of the
                            // current block; that block remains un-faded.
                        }
                        else if (fadePos < fadeSamples)
                        {
                            double gain = fadeSamples == 0
                                ? 0
                                : 1.0 - (double)fadePos / fadeSamples;
                            l = (int)(l * gain);
                            r = (int)(r * gain);
                        }
                        else
                        {
                            l = 0;
                            r = 0;
                        }
                    }

                    interleaved[i * 2] = (short)l;
                    interleaved[i * 2 + 1] = (short)r;
                }

                // Write to WAV
                wav.Write(interleaved.AsSpan(0, samplesThisBlock * 2));
                totalSamples += samplesThisBlock;
                if (_runtime.CurrentLoop != currentLoop)
                    currentLoop = _runtime.CurrentLoop;

                if (!termination.Started)
                {
                    termination.Observe(_runtime.PlaybackEnded, currentLoop, totalSamples);
                    if (termination.StopReason == "natural_stop")
                        _runtime.Stop();
                }

                if (termination.IsComplete(totalSamples))
                {
                    _runtime.Stop();
                    break;
                }
            }

            if (totalSamples >= maxSamples && !termination.IsComplete(totalSamples))
                result.StopReason = opts.MaxDurationSeconds.HasValue ? "max_duration" : "safety_limit";

            // Finalize WAV
            wav.Close();

            result.NaturallyStopped = termination.Started;
            result.RenderedSamples = totalSamples;
            result.Success = true;
            if (string.IsNullOrEmpty(result.StopReason))
                result.StopReason = !string.IsNullOrEmpty(termination.StopReason)
                    ? termination.StopReason
                    : _runtime.IsStopped ? "natural_stop" : "max_duration";
        }
        catch (Exception ex)
        {
            result.Success = false;
            if (string.IsNullOrEmpty(result.StopReason))
                result.StopReason = "error";
            result.LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            // Cleanup order:
            // 1. Unhook trace writer (null the delegate references so no more callbacks fire)
            // 2. Stop chip synthesis
            // 3. Close/dispose trace writer (flushes, computes SHA-256)
            _runtime.TraceWriter = null;
            _sink.Stop();
            traceWriter?.Dispose();
        }

        return result;
    }
}
