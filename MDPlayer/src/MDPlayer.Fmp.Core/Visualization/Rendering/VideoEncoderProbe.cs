using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Fmp.Core.Rendering;

namespace Fmp.Core.Visualization.Rendering;

internal enum EncoderProbeFailureKind
{
    None,
    FfmpegUnavailable,
    EncoderNotExposed,
    ProcessStartFailed,
    Timeout,
    RuntimeFailure,
    Exception,
}

internal sealed record EncoderProbeResult(
    bool Supported,
    VideoEncoder Encoder,
    bool CompileTimeExposed,
    EncoderProbeFailureKind Failure,
    string Diagnostics,
    TimeSpan Elapsed,
    bool CacheHit,
    int? ExitCode = null)
{
    public bool RuntimeProbeSucceeded => Supported && Failure == EncoderProbeFailureKind.None;
    public string FailureClassification => Failure.ToString();
}

internal interface IVideoEncoderProbe
{
    EncoderProbeResult Probe(VideoEncoder encoder);
}

internal sealed class FfmpegVideoEncoderProbe : IVideoEncoderProbe
{
    private static readonly ConcurrentDictionary<string, EncoderProbeResult> Cache = new(StringComparer.Ordinal);
    private readonly string _ffmpegPath;
    private readonly TimeSpan _timeout;

    public FfmpegVideoEncoderProbe(string ffmpegPath, TimeSpan? timeout = null)
    {
        _ffmpegPath = ExecutableResolver.Resolve(ffmpegPath, "ffmpeg");
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public EncoderProbeResult Probe(VideoEncoder encoder)
    {
        if (encoder == VideoEncoder.Auto)
            return Probe(VideoEncoder.Nvenc);
        if (string.IsNullOrWhiteSpace(_ffmpegPath) || !File.Exists(_ffmpegPath))
            return Failure(encoder, EncoderProbeFailureKind.FfmpegUnavailable, "ffmpeg executable was not found.");
        if (encoder == VideoEncoder.LibX264)
            return new(true, encoder, true, EncoderProbeFailureKind.None,
                "libx264 is explicitly selected; capability probing skipped.", TimeSpan.Zero, false);

        string key = CacheKey(encoder);
        if (Cache.TryGetValue(key, out EncoderProbeResult cached))
            return cached with { CacheHit = true };

        Stopwatch timer = Stopwatch.StartNew();
        EncoderProbeResult result;
        try
        {
            ProcessResult exposure = Run(["-hide_banner", "-encoders"], _timeout);
            if (exposure.TimedOut)
                result = Failure(encoder, EncoderProbeFailureKind.Timeout, "ffmpeg encoder listing timed out.", exposure.ExitCode, timer.Elapsed);
            else if (exposure.ExitCode != 0)
                result = Failure(encoder, EncoderProbeFailureKind.RuntimeFailure, Truncate(exposure.Stderr), exposure.ExitCode, timer.Elapsed);
            else if (!HasEncoder(exposure.Stdout, "h264_nvenc"))
                result = Failure(encoder, EncoderProbeFailureKind.EncoderNotExposed, "ffmpeg does not expose h264_nvenc.", exposure.ExitCode, timer.Elapsed, false);
            else
            {
                ProcessResult runtime = Run(VideoEncoderArgs.BuildRuntimeProbeArguments(), _timeout);
                result = runtime.TimedOut
                    ? Failure(encoder, EncoderProbeFailureKind.Timeout, "h264_nvenc runtime probe timed out.", runtime.ExitCode, timer.Elapsed, true)
                    : runtime.ExitCode == 0
                        ? new(true, encoder, true, EncoderProbeFailureKind.None, "h264_nvenc runtime probe succeeded.", timer.Elapsed, false, runtime.ExitCode)
                        : Failure(encoder, EncoderProbeFailureKind.RuntimeFailure, Truncate(runtime.Stderr), runtime.ExitCode, timer.Elapsed, true);
            }
        }
        catch (Exception ex)
        {
            result = Failure(encoder, EncoderProbeFailureKind.Exception, ex.Message, null, timer.Elapsed);
        }
        Cache[key] = result;
        return result;
    }

    private string CacheKey(VideoEncoder encoder)
    {
        try
        {
            FileInfo file = new(_ffmpegPath);
            return $"{_ffmpegPath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{encoder}";
        }
        catch { return $"{_ffmpegPath}|{encoder}"; }
    }

    private ProcessResult Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo { FileName = _ffmpegPath, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) return new(-1, "", "process did not start", false);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new(null, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), true);
        }
        Task.WaitAll(stdout, stderr);
        return new(process.ExitCode, stdout.Result, stderr.Result, false);
    }

    private static bool HasEncoder(string output, string encoder) => output.Split('\n').Any(line =>
        line.Contains(encoder, StringComparison.OrdinalIgnoreCase) && line.TrimStart().StartsWith("V", StringComparison.Ordinal));

    private EncoderProbeResult Failure(VideoEncoder encoder, EncoderProbeFailureKind failure, string diagnostics,
        int? exitCode = null, TimeSpan? elapsed = null, bool exposed = false) =>
        new(false, encoder, exposed, failure, diagnostics, elapsed ?? TimeSpan.Zero, false, exitCode);

    private static string Truncate(string text) => string.IsNullOrWhiteSpace(text) ? "ffmpeg probe failed without diagnostics." : text.Length > 4000 ? text[..4000] : text;
    private readonly record struct ProcessResult(int? ExitCode, string Stdout, string Stderr, bool TimedOut);
}
