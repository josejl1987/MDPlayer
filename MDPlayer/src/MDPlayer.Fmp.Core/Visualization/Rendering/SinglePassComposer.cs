using System.Diagnostics;
using System.Globalization;
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
    internal sealed record ComposeMetrics(
        double CorrscopeWaitSeconds,
        double OverlayCpuSeconds,
        double FfmpegWriteWaitSeconds);

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
    }

    private readonly string _ffmpegPath;
    private readonly Options _options;

    public ComposeMetrics LastMetrics { get; private set; } = new(0, 0, 0);

    public SinglePassComposer(string ffmpegPath, Options options = null)
    {
        _ffmpegPath = ExecutableResolver.Resolve(ffmpegPath, "ffmpeg");
        _options = options ?? new Options();
        if (_options.Encoder == VideoEncoder.Auto)
            _options.Encoder = SupportsEncoder(VideoEncoder.Nvenc)
                ? VideoEncoder.Nvenc
                : VideoEncoder.LibX264;
    }

    public bool IsAvailable => _ffmpegPath != null;
    public string FfmpegPath => _ffmpegPath ?? "ffmpeg";
    public VideoEncoder EffectiveEncoder => _options.Encoder;

    public bool SupportsEncoder(VideoEncoder encoder)
    {
        if (!IsAvailable)
            return false;
        if (encoder != VideoEncoder.Nvenc)
            return true;

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Listing the encoder is insufficient: FFmpeg can expose h264_nvenc
        // even when the host has no usable CUDA device. Probe one tiny frame
        // so automatic selection reflects actual runtime availability.
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("color=c=black:s=16x16:d=0.1");
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("h264_nvenc");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return false;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            Task.WaitAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
        PanelOverlayRenderer overlayRenderer)
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

            int gridFrameBytes = overlayRenderer.Width * overlayRenderer.Layout.CorrscopeGridHeight * 4;
            int outFrameBytes = overlayRenderer.FrameByteCount;
            byte[] grid = new byte[gridFrameBytes];
            byte[] frame = new byte[outFrameBytes];
            long total = overlayRenderer.TotalFrames;

            Stream corrOut = corrProcess.StandardOutput.BaseStream;
            Stream ffmpegIn = ffmpeg.StandardInput.BaseStream;

            bool anyGrid = false;
            long corrscopeTicks = 0;
            long overlayTicks = 0;
            long ffmpegWriteTicks = 0;
            SequentialCompositeSession session = overlayRenderer.CreateSequentialSession();
            session.Initialize(frame);
            try
            {
                for (long i = 0; i < total; i++)
                {
                    // Read the next scope grid frame; on EOF (Corrscope ends
                    // when its longest channel does) keep the last frame so the
                    // scope freezes during the master-audio tail.
                    long stageStart = Stopwatch.GetTimestamp();
                    if (ReadExactly(corrOut, grid, gridFrameBytes))
                        anyGrid = true;
                    corrscopeTicks += Stopwatch.GetTimestamp() - stageStart;

                    stageStart = Stopwatch.GetTimestamp();
                    session.RenderNext(i, anyGrid ? grid : ReadOnlySpan<byte>.Empty, frame);
                    overlayTicks += Stopwatch.GetTimestamp() - stageStart;

                    stageStart = Stopwatch.GetTimestamp();
                    ffmpegIn.Write(frame, 0, outFrameBytes);
                    ffmpegWriteTicks += Stopwatch.GetTimestamp() - stageStart;
                }
                ffmpeg.StandardInput.Close();

                // Corrscope emits one more frame than the overlay expects
                // (end_frame = fps * end_time + 1). Drain the remainder so the
                // bridge exits cleanly instead of hitting a broken pipe.
                Drain(corrOut);
            }
            catch (IOException)
            {
                // FFmpeg exited early; its exit code/stderr are the truth.
            }
            catch
            {
                try { corrProcess.Kill(entireProcessTree: true); } catch { }
                try { ffmpeg.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            finally
            {
                // Unblock Corrscope's stdout write if it still has frames left.
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
            LastMetrics = new ComposeMetrics(
                corrscopeTicks / (double)Stopwatch.Frequency,
                overlayTicks / (double)Stopwatch.Frequency,
                ffmpegWriteTicks / (double)Stopwatch.Frequency);
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
            byte[] frame = new byte[overlayRenderer.FrameByteCount];
            SequentialCompositeSession session = overlayRenderer.CreateSequentialSession();
            session.Initialize(frame);
            long overlayTicks = 0;
            long writeTicks = 0;
            try
            {
                using Stream input = process.StandardInput.BaseStream;
                for (long frameIndex = 0; frameIndex < overlayRenderer.TotalFrames; frameIndex++)
                {
                    long stageStart = Stopwatch.GetTimestamp();
                    session.RenderNext(frameIndex, ReadOnlySpan<byte>.Empty, frame);
                    overlayTicks += Stopwatch.GetTimestamp() - stageStart;

                    stageStart = Stopwatch.GetTimestamp();
                    input.Write(frame, 0, frame.Length);
                    writeTicks += Stopwatch.GetTimestamp() - stageStart;
                }
            }
            catch (IOException)
            {
                // FFmpeg's exit status and stderr below are authoritative.
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
            LastMetrics = new ComposeMetrics(
                0,
                overlayTicks / (double)Stopwatch.Frequency,
                writeTicks / (double)Stopwatch.Frequency);
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
                $"[1:a]showwaves=s={width}x{height}:mode=cline:rate={frameRate}:colors=0x7aa4ff,format=rgba[wave];" +
                "[wave][0:v]overlay=shortest=1:format=auto[v]");
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
            "-map", "0:v:0",
            "-map", "1:a:0",
        };
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
