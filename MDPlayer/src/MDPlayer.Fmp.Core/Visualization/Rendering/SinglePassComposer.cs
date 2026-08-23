using System.Diagnostics;
using System.Globalization;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Fmp.Core.Rendering;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Single-pass compositor: Corrscope renders raw RGBA frames to a pipe (via a
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
        bool FramesAreOpaque => false;
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
        public bool FramesAreOpaque => true;
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
        int QueueCapacity = 3,
        RenderPerformanceSnapshot? Renderer = null,
        double ScopeFrameReadSeconds = 0,
        double QueueWaitSeconds = 0,
        double RendererBlockedSeconds = 0,
        double EncoderIdleSeconds = 0,
        double MuxFinalizationSeconds = 0);

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
    /// streams RGBA grid frames on stdout; each frame is composited in memory
    /// with the overlay and written to a single FFmpeg process that encodes
    /// video + audio once.
    ///
    /// No production callers (the frame-renderer overload is the production
    /// path); kept for tests. It applies the same output-frame→scope-frame
    /// mapping as <see cref="VisualizationFrameRenderer"/> (auto
    /// min(outputFps, 30) unless <paramref name="scopeFps"/> is given) and
    /// reuses the last grid for repeated mapped indices, so a 30 Hz scope
    /// stream is consumed at scope cadence instead of freezing mid-video.
    /// </summary>
    public string Compose(
        Process corrProcess,
        string masterAudioPath,
        string outputVideoPath,
        PanelOverlayRenderer overlayRenderer,
        IRawFrameSourceFactory sourceFactory = null,
        CancellationToken cancellationToken = default,
        double? scopeFps = null)
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
            double outputFps = overlayRenderer.FpsNumerator / (double)overlayRenderer.FpsDenominator;
            double resolvedScopeFps = ScopeFrameMapping.Resolve(scopeFps, outputFps);
            byte[] lastScopeGrid = new byte[gridFrameBytes];
            long lastMapped = -1;
            bool haveScopeGrid = false;

            Stream ffmpegIn = ffmpeg.StandardInput.BaseStream;
            using IRawFrameSource source = (sourceFactory ?? new ProcessRawFrameSourceFactory()).Create(corrProcess);
            SequentialCompositeSession session =
                overlayRenderer.CreateSequentialSession(source.FramesAreOpaque);
            bool anyGrid = false;
            ComposeMetrics pipelineMetrics;
            try
            {
                pipelineMetrics = RunFramePipeline(
                    _options.QueueCapacity,
                    gridFrameBytes,
                    outFrameBytes,
                    total,
                    (slot, frameIndex) =>
                    {
                        // Mapped reads only: when consecutive output frames
                        // share a scope frame, reuse the last grid instead of
                        // consuming another raw frame (the source is strictly
                        // sequential — a repeated read would desync the pipe).
                        long mapped = ScopeFrameMapping.Map(
                            frameIndex, resolvedScopeFps, outputFps);
                        if (mapped == lastMapped && haveScopeGrid)
                        {
                            lastScopeGrid.AsSpan().CopyTo(slot.Grid);
                            slot.HasGrid = true;
                        }
                        else
                        {
                            slot.HasGrid = source.Read(slot.Grid, gridFrameBytes);
                            if (slot.HasGrid)
                            {
                                slot.Grid.AsSpan(0, gridFrameBytes).CopyTo(lastScopeGrid);
                                lastMapped = mapped;
                                haveScopeGrid = true;
                            }
                        }
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
                    },
                    (slot, metrics) =>
                    {
                        long stageStart = Stopwatch.GetTimestamp();
                        try
                        {
                            // Frame bytes are premultiplied (Skia storage);
                            // rawvideo rgba is straight-alpha, so convert first.
                            // GPU output is opaque, where straight == premultiplied.
                            if (!overlayRenderer.ProducesOpaqueFrames)
                                RgbaConversions.UnpremultiplyInPlace(slot.Frame.AsSpan(0, outFrameBytes));
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
                LastMetrics = pipelineMetrics with { Renderer = overlayRenderer.Performance };
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
            long finalizationStart = 0;
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
                    },
                    (slot, metrics) =>
                    {
                        long stageStart = Stopwatch.GetTimestamp();
                        try
                        {
                            // Premultiplied Skia storage vs straight rawvideo rgba;
                            // GPU output is opaque so it skips the conversion.
                            if (!overlayRenderer.ProducesOpaqueFrames)
                                RgbaConversions.UnpremultiplyInPlace(slot.Frame.AsSpan(0, outFrameBytes));
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
                LastMetrics = pipelineMetrics with { Renderer = overlayRenderer.Performance };
            }
            finally
            {
                finalizationStart = Stopwatch.GetTimestamp();
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

            LastMetrics = LastMetrics with
            {
                MuxFinalizationSeconds =
                    (finalizationStart > 0
                        ? Stopwatch.GetTimestamp() - finalizationStart
                        : 0) / (double)Stopwatch.Frequency,
            };

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
        CancellationToken cancellationToken = default,
        Action<float>? progress = null)
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
            VisualizationFrameRenderer.SequentialSession session =
                frameRenderer.CreateSequentialSession();
            ComposeMetrics pipelineMetrics;
            long finalizationStart = 0;
            try
            {
                // Three-stage bounded pipeline: a scope-fill producer
                // (dedicated task) feeds ready; a dedicated render task
                // composites each frame in order (overlay rendering is the
                // principal bottleneck); the write stage (this thread) drains
                // the rendered frames into the FFmpeg pipe. Render and write
                // run concurrently, so t_frame ~ max(t_render, t_write)
                // instead of their sum, with frames written in order.
                pipelineMetrics = RunFramePipeline(
                    _options.QueueCapacity,
                    gridFrameBytes: frameRenderer.HasScopeSource
                        ? frameRenderer.ScopeFrameByteCount
                        : 0,
                    outFrameBytes: frameRenderer.FrameByteCount,
                    totalFrames: frameRenderer.TotalFrames,
                    (slot, frameIndex) =>
                    {
                        if (frameRenderer.HasScopeSource)
                        {
                            frameRenderer.ReadScopeFrame(frameIndex, slot.Grid);
                            slot.HasGrid = true;
                        }
                        else
                        {
                            slot.HasGrid = false;
                        }
                        return true;
                    },
                    (slot, frameIndex, metrics) =>
                    {
                        long stageStart = Stopwatch.GetTimestamp();
                        session.RenderNext(
                            frameIndex,
                            slot.HasGrid ? slot.Grid : ReadOnlySpan<byte>.Empty,
                            slot.Frame);
                        metrics.OverlayTicks += Stopwatch.GetTimestamp() - stageStart;
                    },
                    (slot, metrics) =>
                    {
                        long stageStart = Stopwatch.GetTimestamp();
                        try
                        {
                            WriteRawFrame(input, slot.Frame, frameRenderer.FrameByteCount);
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
                    cancellationToken,
                    abortProducer: null,
                    progress);
                LastMetrics = pipelineMetrics with
                {
                    Renderer = frameRenderer.Performance,
                    ScopeFrameReadSeconds = frameRenderer.ScopeFrameReadSeconds,
                };
            }
            finally
            {
                finalizationStart = Stopwatch.GetTimestamp();
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

            LastMetrics = LastMetrics with
            {
                MuxFinalizationSeconds =
                    (finalizationStart > 0
                        ? Stopwatch.GetTimestamp() - finalizationStart
                        : 0) / (double)Stopwatch.Frequency,
            };

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
    /// Per-stage accumulators shared by the pipeline's stages. Each field is
    /// written by exactly one stage (the render and write stages touch
    /// disjoint fields), so the stages never contend; waits and depths that
    /// merge several stages are kept separate here and combined in
    /// <see cref="ToComposeMetrics"/>.
    /// </summary>
    internal sealed class PipelineMetrics
    {
        /// <summary>Write-stage renderReady.Take waits (encoder-side queue wait;
        /// the successor to the old consumer's ready.Take wait).</summary>
        public long QueueWaitTicks;
        /// <summary>Render-stage RenderNext time only.</summary>
        public long OverlayTicks;
        /// <summary>Write-stage WriteRawFrame time only.</summary>
        public long FfmpegWriteTicks;
        /// <summary>Scope-fill free.Take waits (merged into BlockingSeconds).</summary>
        public long ProducerBlockingTicks;
        /// <summary>Render-stage ready.Take waits (merged into BlockingSeconds).</summary>
        public long RenderBlockingTicks;
        /// <summary>Max depth observed on <c>ready</c> (merged into MaxQueueDepth).</summary>
        public int ReadyMaxDepth;
        /// <summary>Max depth observed on <c>renderReady</c> (merged into MaxQueueDepth).</summary>
        public int RenderReadyMaxDepth;
        /// <summary>Frames the render stage received without a scope grid
        /// (clean scope EOF -> frozen tail).</summary>
        public long StarvationCount;
        /// <summary>Frames successfully written to the encoder.</summary>
        public long FrameCount;

        public ComposeMetrics ToComposeMetrics(bool includeQueueWaitInCorrscopeMetrics, long wallStart, int queueCapacity)
        {
            long blockingTicks = ProducerBlockingTicks + RenderBlockingTicks;
            int maxQueueDepth = Math.Max(ReadyMaxDepth, RenderReadyMaxDepth);
            return new ComposeMetrics(
                includeQueueWaitInCorrscopeMetrics
                    ? QueueWaitTicks / (double)Stopwatch.Frequency
                    : 0,
                OverlayTicks / (double)Stopwatch.Frequency,
                FfmpegWriteTicks / (double)Stopwatch.Frequency,
                maxQueueDepth,
                StarvationCount,
                blockingTicks / (double)Stopwatch.Frequency,
                (Stopwatch.GetTimestamp() - wallStart) / (double)Stopwatch.Frequency,
                FrameCount,
                queueCapacity,
                null,
                QueueWaitTicks / (double)Stopwatch.Frequency,
                blockingTicks / (double)Stopwatch.Frequency,
                QueueWaitTicks / (double)Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// Reports encode progress as a 0..1 fraction of successfully written
    /// frames, throttled to ~10Hz to avoid flooding the progress stream on
    /// long renders. Consumers (and the JSONL writer) read the fraction;
    /// missing updates simply show the last reported progress until the
    /// stage completes at 1.
    /// </summary>
    private static void ReportProgress(Action<float>? progress, long frameIndex, long total)
    {
        if (progress is null || total <= 0)
            return;
        // Emit at most every ~0.1s (coarse: every 64 frames for 600fps files,
        // denser for short renders) plus the final 1.
        if (frameIndex % 64 == 0 || frameIndex == total - 1)
            progress((float)Math.Clamp((double)frameIndex / total, 0, 1));
    }

    /// <summary>
    /// Runs the three-stage bounded frame pipeline shared by every Compose
    /// path: 1. SCOPE FILL (a dedicated task) takes a pooled slot from
    /// <c>free</c>, fills its scope grid via <paramref name="fillFrame"/>
    /// (false = clean EOF), and publishes it to <c>ready</c>. 2. RENDER (a
    /// dedicated task) drains <c>ready</c> in frame order, renders each frame
    /// into its pooled output buffer via <paramref name="renderFrame"/> (the
    /// single-threaded sequential session), and publishes the rendered slot
    /// to <c>renderReady</c>. 3. WRITE (this thread) drains <c>renderReady</c>
    /// in frame order, writes each frame to the encoder via
    /// <paramref name="writeFrame"/> (false = broken pipe -> the
    /// <c>stopped</c> path), reports <paramref name="progress"/> per encoded
    /// frame, and returns the slot to <c>free</c>. Render and write therefore
    /// overlap: t_frame ~ max(t_render, t_write) instead of their sum, with
    /// in-order frames preserved by construction (one producer and one
    /// consumer per queue).
    ///
    /// Slot pool: 2 x <paramref name="queueCapacity"/> slots. The scope grid
    /// bytes are only needed until the frame is rendered, so the doubled
    /// pool lets encode backpressure (a stalled FFmpeg pipe) absorb extra
    /// rendered frames without stalling the render. This doubles frame buffer
    /// memory (2 x queueCapacity x (grid + frame) bytes, e.g. 2 x 3 x 8.3 MB
    /// at 1080p) - accepted to decouple encode backpressure from render; the
    /// slot lifetime stays one pool, one return path.
    ///
    /// Queue capacities: <c>free</c> is sized to the pool; <c>ready</c> and
    /// <c>renderReady</c> are deliberately capped at the original
    /// <paramref name="queueCapacity"/> so the observed MaxQueueDepth stays
    /// comparable to Options.QueueCapacity (and the recorded QueueCapacity),
    /// and backpressure still works: a stage blocks when its upstream queue
    /// is full or no slot is available.
    ///
    /// Metrics (see cref="PipelineMetrics"): OverlayTicks = render-stage
    /// RenderNext time only; FfmpegWriteTicks = write-stage WriteRawFrame time
    /// only; BlockingTicks = scope-fill free.Take waits + render-stage
    /// ready.Take waits (merged); QueueWaitTicks (flag-gated) = write-stage
    /// renderReady.Take waits (the encoder-side queue wait, successor to the
    /// old consumer's ready.Take wait); MaxQueueDepth = max depth observed
    /// across ready and renderReady; FrameCount = successfully written frames.
    ///
    /// Errors: a stage failure cancels the linked token so the other stages
    /// exit promptly; after the joins, the root cause is rethrown (producer
    /// -> render -> write priority), a plain cancellation surfaces as the
    /// write stage's OperationCanceledException, and a write-stage broken
    /// pipe (<c>stopped</c>) falls through so the caller can treat FFmpeg's
    /// exit status and stderr as authoritative. Every slot is returned to
    /// the pool exactly once on every path.
    /// </summary>
    internal static ComposeMetrics RunFramePipeline(
        int queueCapacity,
        int gridFrameBytes,
        int outFrameBytes,
        long totalFrames,
        Func<FrameSlot, long, bool> fillFrame,
        Action<FrameSlot, long, PipelineMetrics> renderFrame,
        Func<FrameSlot, PipelineMetrics, bool> writeFrame,
        Action<FrameSlot> initializeSession,
        bool includeQueueWaitInCorrscopeMetrics,
        CancellationToken cancellationToken,
        Action abortProducer,
        Action<float>? progress = null)
    {
        int slotCount = queueCapacity * 2;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var free = new BlockingCollection<FrameSlot>(slotCount);
        var ready = new BlockingCollection<FrameSlot>(queueCapacity);
        var renderReady = new BlockingCollection<FrameSlot>(queueCapacity);
        var slots = new FrameSlot[slotCount];
        var metrics = new PipelineMetrics();
        long wallStart = Stopwatch.GetTimestamp();
        Exception producerError = null;
        Exception renderError = null;
        Task producer = null;
        Task renderer = null;

        try
        {
            for (int n = 0; n < slots.Length; n++)
            {
                slots[n] = new FrameSlot(gridFrameBytes, outFrameBytes);
                free.Add(slots[n]);
            }

            // Reserve a pooled output buffer while the session is initialized.
            // The stages start only after it has been returned to the free queue.
            FrameSlot initial = free.Take(CancellationToken.None);
            try
            {
                initializeSession(initial);
            }
            finally
            {
                free.Add(initial);
            }

            // Stage 1: scope fill (dedicated task): free -> fill -> ready.
            producer = Task.Run(() =>
            {
                try
                {
                    for (long index = 0; index < totalFrames; index++)
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        long waitStart = Stopwatch.GetTimestamp();
                        FrameSlot slot = free.Take(linkedCancellation.Token);
                        metrics.ProducerBlockingTicks += Stopwatch.GetTimestamp() - waitStart;
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

            // Stage 2: render (dedicated task): ready -> RenderNext -> renderReady.
            renderer = Task.Run(() =>
            {
                try
                {
                    for (long index = 0; index < totalFrames; index++)
                    {
                        // Check the token on every render iteration, not only when
                        // blocked in Take: once the producer has published every
                        // frame, Take returns immediately without examining the
                        // token, so without this check a cancellation would be
                        // ignored for the entire remainder of the pipeline.
                        try
                        {
                            linkedCancellation.Token.ThrowIfCancellationRequested();
                            long waitStart = Stopwatch.GetTimestamp();
                            FrameSlot slot;
                            try
                            {
                                slot = ready.Take(linkedCancellation.Token);
                            }
                            catch (Exception error)
                            {
                                renderError = error;
                                break;
                            }
                            metrics.RenderBlockingTicks += Stopwatch.GetTimestamp() - waitStart;
                            metrics.ReadyMaxDepth = Math.Max(metrics.ReadyMaxDepth, ready.Count);
                            bool published = false;
                            try
                            {
                                renderFrame(slot, index, metrics);
                                renderReady.Add(slot, linkedCancellation.Token);
                                published = true;
                            }
                            catch (Exception error)
                            {
                                renderError = error;
                                linkedCancellation.Cancel();
                            }
                            finally
                            {
                                if (!published)
                                    free.Add(slot, CancellationToken.None);
                            }

                            if (renderError != null)
                                break;
                        }
                        catch (Exception error)
                        {
                            // A cancellation raised by the scope-fill failure must not
                            // escape before the stages are joined; that join
                            // rethrows the root cause (e.g. frame-boundary drift).
                            renderError = error;
                            break;
                        }
                    }
                }
                finally
                {
                    renderReady.CompleteAdding();
                }
            }, CancellationToken.None);

            // Stage 3: write (this thread): renderReady -> encoder -> free.
            Exception consumerError = null;
            bool stopped = false;
            try
            {
                for (long index = 0; index < totalFrames; index++)
                {
                    // Check the token on every write iteration, not only when
                    // blocked in Take: once the render stage has published every
                    // frame, Take returns immediately without examining the
                    // token, so without this check a cancellation would be
                    // ignored for the entire remainder of the encode.
                    try
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        long waitStart = Stopwatch.GetTimestamp();
                        FrameSlot slot;
                        try
                        {
                            slot = renderReady.Take(linkedCancellation.Token);
                        }
                        catch (Exception error)
                        {
                            consumerError = error;
                            break;
                        }

                        if (includeQueueWaitInCorrscopeMetrics)
                            metrics.QueueWaitTicks += Stopwatch.GetTimestamp() - waitStart;
                        metrics.RenderReadyMaxDepth = Math.Max(metrics.RenderReadyMaxDepth, renderReady.Count);
                        try
                        {
                            if (!writeFrame(slot, metrics))
                            {
                                stopped = true;
                                linkedCancellation.Cancel();
                                break;
                            }

                            metrics.FrameCount++;
                            ReportProgress(progress, index, totalFrames);
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
                            // returned to ArrayPool only after the stages join.
                            free.Add(slot, CancellationToken.None);
                        }
                    }
                    catch (Exception error)
                    {
                        // A cancellation raised by an upstream stage's failure
                        // must not escape before the stages are joined; that join
                        // rethrows the root cause (e.g. frame-boundary drift).
                        consumerError = error;
                        break;
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

            // Join the background stages; the write stage is this thread.
            producer.GetAwaiter().GetResult();
            renderer.GetAwaiter().GetResult();

            // Root-cause rethrow, generalizing the old producer/consumer
            // priority: a real failure shadows the downstream stages'
            // cancellation noise, a plain cancellation surfaces as the write
            // stage's OperationCanceledException, and a write-stage broken
            // pipe (stopped) falls through so the caller can treat FFmpeg's
            // exit status and stderr as authoritative.
            if (producerError != null &&
                !(producerError is OperationCanceledException && (stopped || renderError != null || consumerError != null)))
            {
                ExceptionDispatchInfo.Capture(producerError).Throw();
            }

            if (renderError != null &&
                !(renderError is OperationCanceledException && (stopped || consumerError != null)))
            {
                ExceptionDispatchInfo.Capture(renderError).Throw();
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
            if (renderer != null)
            {
                try { renderer.GetAwaiter().GetResult(); } catch { }
            }

            while (free.TryTake(out FrameSlot returned))
                _ = returned;
            while (ready.TryTake(out FrameSlot returned))
                _ = returned;
            while (renderReady.TryTake(out FrameSlot returned))
                _ = returned;
            free.Dispose();
            ready.Dispose();
            renderReady.Dispose();
            foreach (FrameSlot slot in slots)
                slot?.Return();
        }
    }

    internal sealed class FrameSlot
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
    /// Returns false only when the stream ends cleanly at a frame boundary
    /// (corrscope's documented one-extra-frame tail, drained by the caller).
    /// A stream that ends partway through a frame throws: the producer wrote a
    /// different number of bytes per frame than the renderer expects, which is
    /// raw-video frame-boundary drift (FFmpeg would consume the next frame's
    /// bytes and the image scrolls and wraps).
    /// </summary>
    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                if (read > 0)
                {
                    throw new InvalidOperationException(
                        $"Raw frame stream ended mid-frame after {read} of {count} bytes. " +
                        "The producer wrote a different frame size than the renderer " +
                        "expects (packed width × height × 4); FFmpeg frame boundaries " +
                        "would drift and the video would scroll and wrap.");
                }
                return false;
            }
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

    /// <summary>
    /// Writes exactly <paramref name="frameByteCount"/> bytes of an RGBA frame to
    /// <paramref name="output"/>. The caller's pooled buffer (<paramref name="buffer"/>)
    /// may be larger than the logical frame size (ArrayPool rents can exceed the
    /// requested length), so only the logical byte count is written — writing the
    /// buffer's full capacity would shift every FFmpeg frame boundary.
    /// </summary>
    internal static void WriteRawFrame(
        Stream output,
        byte[] buffer,
        int frameByteCount)
    {
        if (buffer.Length < frameByteCount)
            throw new ArgumentException("Frame buffer is too small.", nameof(buffer));

        output.Write(buffer, 0, frameByteCount);
    }
}
