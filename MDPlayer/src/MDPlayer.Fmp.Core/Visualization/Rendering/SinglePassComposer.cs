using System.Diagnostics;
using System.Globalization;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Fmp.Core.Rendering;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Single-pass compositor: Corrscope renders raw RGB0 frames to a pipe (via a
/// small Python bridge — no intermediate video is encoded), the .NET overlay is
/// composited onto each frame in memory, and one FFmpeg process encodes the
/// final video. This eliminates the intermediate H.264 encode/decode and the
/// crop/reassembly filter graph used by the retired compositor.
/// </summary>
internal sealed class SinglePassComposer
{
    internal interface IRawFrameSourceFactory
    {
        IRawFrameSource Create(Process process);
    }
    internal interface IRawFrameSource : IDisposable
    {
        bool Read(byte[] buffer, int count);
        void Drain();
    }
    internal sealed class ProcessRawFrameSourceFactory : IRawFrameSourceFactory
    {
        public IRawFrameSource Create(Process process) => new ProcessRawFrameSource(process.StandardOutput.BaseStream);
    }
    private sealed class ProcessRawFrameSource : IRawFrameSource
    {
        private readonly Stream _stream;
        public ProcessRawFrameSource(Stream stream) => _stream = stream;
        public bool Read(byte[] buffer, int count) => ReadExactly(_stream, buffer, count);
        public void Drain() => SinglePassComposer.Drain(_stream);
        public void Dispose() { }
    }
    internal sealed record ComposeMetrics(
        double CorrscopeWaitSeconds,
        double OverlayCpuSeconds,
        double FfmpegWriteWaitSeconds,
        int MaxQueueDepth = 0,
        long StarvationCount = 0,
        double BlockingSeconds = 0,
        double WallTimeSeconds = 0,
        long FrameCount = 0,
        int QueueCapacity = 3);

    public sealed class Options
    {
        public int AudioBitrateKbps { get; set; } = 192;
        public int TimeoutMinutes { get; set; } = 60;
        /// <summary>libx264 preset name (translated to the encoder's vocabulary).</summary>
        public string VideoPreset { get; set; } = "veryfast";
        /// <summary>libx264 CRF (quality) for the final encode.</summary>
        public string VideoCrf { get; set; } = "18";
        /// <summary>Encoder for the final encode.</summary>
        public VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;
        /// <summary>Bounded producer/consumer queue capacity, constrained to 2..4.</summary>
        public int QueueCapacity { get; set; } = 3;
    }

    private readonly string _ffmpegPath;
    private readonly Options _options;
    private readonly IVideoEncoderProbe _encoderProbe;

    public ComposeMetrics LastMetrics { get; private set; } = new(0, 0, 0);
    public EncoderProbeResult LastEncoderProbe { get; private set; }

    public SinglePassComposer(string ffmpegPath, Options options = null)
    {
        _ffmpegPath = ExecutableResolver.Resolve(ffmpegPath, "ffmpeg");
        _options = options ?? new Options();
        if (_options.QueueCapacity is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(options), "QueueCapacity must be between 2 and 4.");
        _encoderProbe = new FfmpegVideoEncoderProbe(_ffmpegPath);
        if (_options.Encoder == VideoEncoder.Auto)
        {
            LastEncoderProbe = _encoderProbe.Probe(VideoEncoder.Nvenc);
            _options.Encoder = LastEncoderProbe.Supported
                ? VideoEncoder.Nvenc
                : VideoEncoder.LibX264;
        }
    }

    public bool IsAvailable => _ffmpegPath != null;
    public string FfmpegPath => _ffmpegPath ?? "ffmpeg";
    public VideoEncoder EffectiveEncoder => _options.Encoder;

    public bool SupportsEncoder(VideoEncoder encoder)
        => ProbeEncoder(encoder).Supported;

    public EncoderProbeResult ProbeEncoder(VideoEncoder encoder)
    {
        LastEncoderProbe = _encoderProbe.Probe(encoder);
        return LastEncoderProbe;
    }

    /// <summary>
    /// Composes the final video from raw Corrscope frames. The bridge process
    /// streams RGB0 grid frames on stdout; each frame is composited in memory
    /// with the overlay and written to a single FFmpeg process that encodes
    /// video + audio once.
    /// </summary>
    public string Compose(
        Process corrProcess,
        string masterAudioPath,
        string outputVideoPath,
        PanelOverlayRenderer overlayRenderer,
        IRawFrameSourceFactory sourceFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(corrProcess);
        ArgumentNullException.ThrowIfNull(overlayRenderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        if (!IsAvailable)
            throw new InvalidOperationException("ffmpeg not found on PATH");
        if (!File.Exists(masterAudioPath))
            throw new FileNotFoundException("Master WAV not found.", masterAudioPath);

        string outputDirectory = Path.GetDirectoryName(outputVideoPath) ?? ".";
        Directory.CreateDirectory(outputDirectory);
        string extension = Path.GetExtension(outputVideoPath);
        if (string.IsNullOrEmpty(extension))
            extension = ".mp4";
        string tempPath = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(outputVideoPath) + ".partial" + extension);

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in BuildArguments(
            masterAudioPath,
            tempPath,
            overlayRenderer.Width,
            overlayRenderer.Height,
            overlayRenderer.FpsNumerator,
            overlayRenderer.FpsDenominator,
            _options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var ffmpeg = new Process { StartInfo = startInfo };
        Task<string> corrErrTask = null;
        Task<string> ffmpegErrTask = null;
        try
        {
            ffmpeg.Start();
            ffmpegErrTask = ffmpeg.StandardError.ReadToEndAsync();
            corrErrTask = corrProcess.StandardError.ReadToEndAsync();

            int gridFrameBytes = overlayRenderer.ScopeFrameByteCount;
            int outFrameBytes = overlayRenderer.FrameByteCount;
            long total = overlayRenderer.TotalFrames;

            Stream ffmpegIn = ffmpeg.StandardInput.BaseStream;
            using IRawFrameSource source = (sourceFactory ?? new ProcessRawFrameSourceFactory()).Create(corrProcess);
            SequentialCompositeSession session = overlayRenderer.CreateSequentialSession();
            bool anyGrid = false;
            ComposeMetrics pipelineMetrics;
            try
            {
                pipelineMetrics = RunFramePipeline(
                    _options.QueueCapacity,
                    gridFrameBytes,
                    outFrameBytes,
                    total,
                    (slot, _) =>
                    {
                        slot.HasGrid = source.Read(slot.Grid, gridFrameBytes);
                        return true;
                    },
                    (slot, index, metrics) =>
                    {
                        // On EOF, RenderNext receives an empty grid and keeps
                        // the last scope frame frozen during the audio tail.
                        if (slot.HasGrid)
                            anyGrid = true;
                        if (!slot.HasGrid)
                            metrics.StarvationCount++;

                        long stageStart = Stopwatch.GetTimestamp();
                        session.RenderNext(index, anyGrid ? slot.Grid : ReadOnlySpan<byte>.Empty, slot.Frame);
                        metrics.OverlayTicks += Stopwatch.GetTimestamp() - stageStart;

                        stageStart = Stopwatch.GetTimestamp();
                        try
                        {
                            ffmpegIn.Write(slot.Frame, 0, outFrameBytes);
                        }
                        catch (IOException)
                        {
                            // FFmpeg's exit status and stderr below are authoritative.
                            return false;
                        }
                        metrics.FfmpegWriteTicks += Stopwatch.GetTimestamp() - stageStart;
                        return true;
                    },
                    slot => session.Initialize(slot.Frame),
                    includeQueueWaitInCorrscopeMetrics: true,
                    cancellationToken,
                    abortProducer: () => { try { corrProcess.Kill(entireProcessTree: true); } catch { } });
                LastMetrics = pipelineMetrics;
                ffmpeg.StandardInput.Close();

                // Corrscope emits one more frame than the overlay expects
                // (end_frame = fps * end_time + 1). Drain the remainder so the
                // bridge exits cleanly instead of hitting a broken pipe.
                try { source.Drain(); } catch (IOException) { }
            }
            finally
            {
                try { corrProcess.StandardOutput.Close(); } catch { }
                try { ffmpeg.StandardInput.Close(); } catch { }
            }

            bool corrExited = corrProcess.WaitForExit(
                (int)TimeSpan.FromMinutes(_options.TimeoutMinutes).TotalMilliseconds);
            if (!corrExited)
            {
                try { corrProcess.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"corr exceeded the {_options.TimeoutMinutes}-minute timeout");
            }

            bool ffmpegExited = ffmpeg.WaitForExit(
                (int)TimeSpan.FromMinutes(_options.TimeoutMinutes).TotalMilliseconds);
            if (!ffmpegExited)
            {
                try { ffmpeg.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"ffmpeg exceeded the {_options.TimeoutMinutes}-minute timeout");
            }

            string corrErr = corrErrTask?.GetAwaiter().GetResult() ?? "";
            string ffmpegErr = ffmpegErrTask?.GetAwaiter().GetResult() ?? "";
            if (corrProcess.ExitCode != 0)
            {
                if (corrErr.Length > 2000)
                    corrErr = corrErr[..2000] + "... (truncated)";
                throw new InvalidOperationException(
                    $"corr failed (exit {corrProcess.ExitCode}):\n{corrErr}");
            }
            if (ffmpeg.ExitCode != 0 || !File.Exists(tempPath))
            {
                if (ffmpegErr.Length > 4000)
                    ffmpegErr = ffmpegErr[..4000] + "... (truncated)";
                throw new InvalidOperationException(
                    $"ffmpeg failed (exit {ffmpeg.ExitCode}):\n{ffmpegErr}");
            }

            if (File.Exists(outputVideoPath))
                File.Delete(outputVideoPath);
            File.Move(tempPath, outputVideoPath);
            return outputVideoPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            try { corrProcess.Kill(entireProcessTree: true); } catch { }
            try { ffmpeg.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            try { corrProcess.Dispose(); } catch { }
            try { ffmpeg.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Composes a generic register-log timeline when no isolated Corrscope
    /// stems are available. The master WAV is used as a single waveform layer;
    /// frames still follow the same in-memory sequential overlay path and are
    /// encoded exactly once by FFmpeg.
    /// </summary>
    public string ComposeMasterOnly(
        string masterAudioPath,
        string outputVideoPath,
        PanelOverlayRenderer overlayRenderer,
        bool includeWaveform = true)
    {
        ArgumentNullException.ThrowIfNull(overlayRenderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        if (!IsAvailable)
            throw new InvalidOperationException("ffmpeg not found on PATH");
        if (!File.Exists(masterAudioPath))
            throw new FileNotFoundException("Master WAV not found.", masterAudioPath);

        string outputDirectory = Path.GetDirectoryName(outputVideoPath) ?? ".";
        Directory.CreateDirectory(outputDirectory);
        string extension = Path.GetExtension(outputVideoPath);
        if (string.IsNullOrEmpty(extension))
            extension = ".mp4";
        string tempPath = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(outputVideoPath) + ".partial" + extension);
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in BuildMasterOnlyArguments(
            masterAudioPath,
            tempPath,
            overlayRenderer.Width,
            overlayRenderer.Height,
            overlayRenderer.FpsNumerator,
            overlayRenderer.FpsDenominator,
            includeWaveform,
            _options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            SequentialCompositeSession session = overlayRenderer.CreateSequentialSession();
            int gridFrameBytes = overlayRenderer.ScopeFrameByteCount;
            int outFrameBytes = overlayRenderer.FrameByteCount;
            Stream input = process.StandardInput.BaseStream;
            ComposeMetrics pipelineMetrics;
            try
            {
                pipelineMetrics = RunFramePipeline(
                    _options.QueueCapacity,
                    gridFrameBytes,
                    outFrameBytes,
                    overlayRenderer.TotalFrames,
                    (slot, _) =>
                    {
                        slot.HasGrid = false;
                        return true;
                    },
                    (slot, frameIndex, metrics) =>
                    {
                        long stageStart = Stopwatch.GetTimestamp();
                        session.RenderNext(frameIndex, ReadOnlySpan<byte>.Empty, slot.Frame);
                        metrics.OverlayTicks += Stopwatch.GetTimestamp() - stageStart;

                        stageStart = Stopwatch.GetTimestamp();
                        try
                        {
                            input.Write(slot.Frame, 0, outFrameBytes);
                        }
                        catch (IOException)
                        {
                            // FFmpeg's exit status and stderr below are authoritative.
                            return false;
                        }
                        metrics.FfmpegWriteTicks += Stopwatch.GetTimestamp() - stageStart;
                        return true;
                    },
                    slot => session.Initialize(slot.Frame),
                    includeQueueWaitInCorrscopeMetrics: false,
                    CancellationToken.None,
                    abortProducer: null);
                LastMetrics = pipelineMetrics;
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { }
            }

            bool exited = process.WaitForExit(
                (int)TimeSpan.FromMinutes(_options.TimeoutMinutes).TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"ffmpeg exceeded the {_options.TimeoutMinutes}-minute timeout");
            }

            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(tempPath))
            {
                if (stderr.Length > 4000)
                    stderr = stderr[..4000] + "... (truncated)";
                throw new InvalidOperationException(
                    $"ffmpeg failed (exit {process.ExitCode}):\n{stderr}");
            }

            if (File.Exists(outputVideoPath))
                File.Delete(outputVideoPath);
            File.Move(tempPath, outputVideoPath);
            return outputVideoPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Composes the final video by asking the shared
    /// <see cref="VisualizationFrameRenderer"/> for every frame and writing the
    /// resulting RGBA straight into a single FFmpeg encode. The frame renderer
    /// already composes scopes + overlay in memory (exactly as preview and
    /// review do), so this is the production path that makes final, preview and
    /// review share one frame renderer.
    ///
    /// <paramref name="includeWaveform"/> is a last-resort FFmpeg waveform
    /// overlay for the pathological case where scopes are enabled but neither
    /// a Corrscope bridge nor the internal master-waveform source could be
    /// created (no usable master WAV). Normal fallback is the shared
    /// <see cref="MasterWaveformFrameSource"/>, which keeps final == preview.
    /// </summary>
    public string Compose(
        string masterAudioPath,
        string outputVideoPath,
        VisualizationFrameRenderer frameRenderer,
        bool includeWaveform = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frameRenderer);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        if (!IsAvailable)
            throw new InvalidOperationException("ffmpeg not found on PATH");
        if (!File.Exists(masterAudioPath))
            throw new FileNotFoundException("Master WAV not found.", masterAudioPath);

        string outputDirectory = Path.GetDirectoryName(outputVideoPath) ?? ".";
        Directory.CreateDirectory(outputDirectory);
        string extension = Path.GetExtension(outputVideoPath);
        if (string.IsNullOrEmpty(extension))
            extension = ".mp4";
        string tempPath = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(outputVideoPath) + ".partial" + extension);
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in BuildMasterOnlyArguments(
            masterAudioPath,
            tempPath,
            frameRenderer.Width,
            frameRenderer.Height,
            frameRenderer.TotalFrames > 0 ? frameRenderer.OverlayFpsNumerator : 0,
            frameRenderer.OverlayFpsDenominator,
            includeWaveform,
            _options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Stream input = process.StandardInput.BaseStream;
            ComposeMetrics pipelineMetrics;
            try
            {
                // Bounded producer/consumer pipeline: the frame renderer fills
                // reusable slots while FFmpeg drains the previous frames, so
                // rendering overlaps with pipe writes instead of serializing
                // (overlay rendering is the principal bottleneck).
                pipelineMetrics = RunFramePipeline(
                    _options.QueueCapacity,
                    gridFrameBytes: 0,
                    outFrameBytes: frameRenderer.FrameByteCount,
                    totalFrames: frameRenderer.TotalFrames,
                    (slot, _) =>
                    {
                        slot.HasGrid = false;
                        return true;
                    },
                    (slot, frameIndex, metrics) =>
                    {
                        long stageStart = Stopwatch.GetTimestamp();
                        frameRenderer.RenderFrame(frameIndex, slot.Frame);
                        metrics.OverlayTicks += Stopwatch.GetTimestamp() - stageStart;

                        stageStart = Stopwatch.GetTimestamp();
                        try
                        {
                            input.Write(slot.Frame, 0, slot.Frame.Length);
                        }
                        catch (IOException)
                        {
                            // FFmpeg's exit status and stderr below are authoritative.
                            return false;
                        }
                        metrics.FfmpegWriteTicks += Stopwatch.GetTimestamp() - stageStart;
                        return true;
                    },
                    slot => { },
                    includeQueueWaitInCorrscopeMetrics: false,
                    cancellationToken,
                    abortProducer: null);
                LastMetrics = pipelineMetrics;
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { }
            }

            bool exited = process.WaitForExit(
                (int)TimeSpan.FromMinutes(_options.TimeoutMinutes).TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"ffmpeg exceeded the {_options.TimeoutMinutes}-minute timeout");
            }

            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(tempPath))
            {
                if (stderr.Length > 4000)
                    stderr = stderr[..4000] + "... (truncated)";
                throw new InvalidOperationException(
                    $"ffmpeg failed (exit {process.ExitCode}):\n{stderr}");
            }

            if (File.Exists(outputVideoPath))
                File.Delete(outputVideoPath);
            File.Move(tempPath, outputVideoPath);
            return outputVideoPath;
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class PipelineMetrics
    {
        public long QueueWaitTicks;
        public long OverlayTicks;
        public long FfmpegWriteTicks;
        public long BlockingTicks;
        public int MaxQueueDepth;
        public long StarvationCount;
        public long FrameCount;

        public ComposeMetrics ToComposeMetrics(bool includeQueueWaitInCorrscopeMetrics, long wallStart, int queueCapacity)
        {
            return new ComposeMetrics(
                includeQueueWaitInCorrscopeMetrics
                    ? QueueWaitTicks / (double)Stopwatch.Frequency
                    : 0,
                OverlayTicks / (double)Stopwatch.Frequency,
                FfmpegWriteTicks / (double)Stopwatch.Frequency,
                MaxQueueDepth,
                StarvationCount,
                BlockingTicks / (double)Stopwatch.Frequency,
                (Stopwatch.GetTimestamp() - wallStart) / (double)Stopwatch.Frequency,
                FrameCount,
                queueCapacity);
        }
    }

    private static ComposeMetrics RunFramePipeline(
        int queueCapacity,
        int gridFrameBytes,
        int outFrameBytes,
        long totalFrames,
        Func<FrameSlot, long, bool> fillFrame,
        Func<FrameSlot, long, PipelineMetrics, bool> consumeFrame,
        Action<FrameSlot> initializeSession,
        bool includeQueueWaitInCorrscopeMetrics,
        CancellationToken cancellationToken,
        Action abortProducer)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var free = new BlockingCollection<FrameSlot>(queueCapacity);
        var ready = new BlockingCollection<FrameSlot>(queueCapacity);
        var slots = new FrameSlot[queueCapacity];
        var metrics = new PipelineMetrics();
        long wallStart = Stopwatch.GetTimestamp();
        Exception producerError = null;
        Task producer = null;

        try
        {
            for (int n = 0; n < slots.Length; n++)
            {
                slots[n] = new FrameSlot(gridFrameBytes, outFrameBytes);
                free.Add(slots[n]);
            }

            // Reserve a pooled output buffer while the session is initialized.
            // The producer starts only after it has been returned to the free queue.
            FrameSlot initial = free.Take(CancellationToken.None);
            try
            {
                initializeSession(initial);
            }
            finally
            {
                free.Add(initial);
            }

            producer = Task.Run(() =>
            {
                try
                {
                    for (long index = 0; index < totalFrames; index++)
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        long waitStart = Stopwatch.GetTimestamp();
                        FrameSlot slot = free.Take(linkedCancellation.Token);
                        metrics.BlockingTicks += Stopwatch.GetTimestamp() - waitStart;
                        bool published = false;
                        try
                        {
                            slot.Index = index;
                            slot.HasGrid = fillFrame(slot, index);
                            ready.Add(slot, linkedCancellation.Token);
                            published = true;
                        }
                        finally
                        {
                            if (!published)
                                free.Add(slot);
                        }
                    }
                }
                catch (Exception error)
                {
                    producerError = error;
                    linkedCancellation.Cancel();
                }
                finally
                {
                    ready.CompleteAdding();
                }
            }, CancellationToken.None);

            Exception consumerError = null;
            bool stopped = false;
            try
            {
                for (long index = 0; index < totalFrames; index++)
                {
                    // Check the token on every consumer iteration, not only when
                    // blocked in Take: once the producer has published every
                    // frame, Take returns immediately without examining the
                    // token, so without this check a cancellation would be
                    // ignored for the entire remainder of the encode.
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    FrameSlot slot;
                    long waitStart = Stopwatch.GetTimestamp();
                    try
                    {
                        slot = ready.Take(linkedCancellation.Token);
                    }
                    catch (Exception error)
                    {
                        consumerError = error;
                        break;
                    }

                    if (includeQueueWaitInCorrscopeMetrics)
                        metrics.QueueWaitTicks += Stopwatch.GetTimestamp() - waitStart;
                    metrics.MaxQueueDepth = Math.Max(metrics.MaxQueueDepth, ready.Count);
                    try
                    {
                        if (!consumeFrame(slot, index, metrics))
                        {
                            stopped = true;
                            linkedCancellation.Cancel();
                            break;
                        }

                        metrics.FrameCount++;
                    }
                    catch (Exception error)
                    {
                        consumerError = error;
                        linkedCancellation.Cancel();
                        break;
                    }
                    finally
                    {
                        // The slot remains pooled for the complete run and is
                        // returned to ArrayPool only after the producer joins.
                        free.Add(slot, CancellationToken.None);
                    }
                }
            }
            finally
            {
                if (consumerError != null || stopped)
                {
                    linkedCancellation.Cancel();
                    try { abortProducer?.Invoke(); } catch { }
                }
            }

            producer.GetAwaiter().GetResult();

            if (producerError != null &&
                !(producerError is OperationCanceledException && (stopped || consumerError != null)))
            {
                ExceptionDispatchInfo.Capture(producerError).Throw();
            }

            if (consumerError != null)
                ExceptionDispatchInfo.Capture(consumerError).Throw();

            return metrics.ToComposeMetrics(includeQueueWaitInCorrscopeMetrics, wallStart, queueCapacity);
        }
        finally
        {
            linkedCancellation.Cancel();
            if (producer != null)
            {
                try { producer.GetAwaiter().GetResult(); } catch { }
            }

            while (free.TryTake(out FrameSlot returned))
                _ = returned;
            while (ready.TryTake(out FrameSlot returned))
                _ = returned;
            free.Dispose();
            ready.Dispose();
            foreach (FrameSlot slot in slots)
                slot?.Return();
        }
    }

    private sealed class FrameSlot
    {
        public readonly byte[] Grid;
        public readonly byte[] Frame;
        public long Index;
        public bool HasGrid;
        private int _returned;

        public FrameSlot(int gridBytes, int frameBytes)
        {
            Grid = ArrayPool<byte>.Shared.Rent(gridBytes);
            try
            {
                Frame = ArrayPool<byte>.Shared.Rent(frameBytes);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(Grid);
                throw;
            }
        }

        public void Return()
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0)
                return;
            ArrayPool<byte>.Shared.Return(Grid);
            ArrayPool<byte>.Shared.Return(Frame);
        }
    }

    internal static IReadOnlyList<string> BuildMasterOnlyArguments(
        string masterAudioPath,
        string outputVideoPath,
        int width,
        int height,
        int fpsNumerator,
        int fpsDenominator,
        bool includeWaveform,
        Options options = null)
    {
        options ??= new Options();
        string frameRate = FormattableString.Invariant($"{fpsNumerator}/{fpsDenominator}");
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-f", "rawvideo",
            "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}",
            "-framerate", frameRate,
            "-i", "pipe:0",
            "-i", masterAudioPath,
        };

        if (includeWaveform)
        {
            args.Add("-filter_complex");
            args.Add(
                $"[0:v]null[overlay];[1:a]showwaves=s={width}x{height}:mode=cline:rate={frameRate}:colors=0x7aa4ff,format=rgba[wave];" +
                "[wave][overlay]overlay=shortest=1:format=auto[v]");
            args.AddRange(["-map", "[v]"]);
        }
        else
        {
            args.AddRange(["-map", "0:v:0"]);
        }

        args.AddRange(["-map", "1:a:0"]);
        VideoEncoderArgs.Append(args, options.Encoder, options.VideoPreset, options.VideoCrf);
        args.AddRange([
            "-fps_mode", "cfr",
            "-r", frameRate,
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", options.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
            "-movflags", "+faststart",
            "-shortest",
            outputVideoPath,
        ]);
        return args;
    }

    /// <summary>
    /// Builds the FFmpeg arguments for the single-pass encode: one raw RGBA
    /// stream on stdin (the composited frames) plus the master WAV.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(
        string masterAudioPath,
        string outputVideoPath,
        int width,
        int height,
        int fpsNumerator,
        int fpsDenominator,
        Options options = null)
    {
        options ??= new Options();
        string frameRate = FormattableString.Invariant($"{fpsNumerator}/{fpsDenominator}");

        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-f", "rawvideo",
            "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}",
            "-framerate", frameRate,
            "-i", "pipe:0",
            "-i", masterAudioPath,
        };
        args.AddRange(["-map", "0:v:0", "-map", "1:a:0"]);
        VideoEncoderArgs.Append(args, options.Encoder, options.VideoPreset, options.VideoCrf);
        args.AddRange(new[]
        {
            "-fps_mode", "cfr",
            "-r", frameRate,
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", options.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k",
            "-movflags", "+faststart",
            "-shortest",
            outputVideoPath,
        });
        return args;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from <paramref name="stream"/>.
    /// Returns false when the stream ends before all bytes are read.
    /// </summary>
    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }

    /// <summary>
    /// Reads and discards until the stream ends, letting the producer finish
    /// cleanly. Any I/O error is swallowed — the producer's exit code is the
    /// source of truth.
    /// </summary>
    private static void Drain(Stream stream)
    {
        var scratch = new byte[64 * 1024];
        try
        {
            while (stream.Read(scratch, 0, scratch.Length) > 0)
            {
                // discard
            }
        }
        catch (IOException)
        {
        }
    }
}
