using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Fmp.Core.Analysis;

namespace Fmp.Cli;

internal sealed record AnalysisProcessResult(int ExitCode, bool TimedOut, string StandardError);
internal sealed record AnalysisWorkerProbeResult(
    bool Compatible,
    string WorkerVersion,
    string Diagnostics,
    bool CacheHit);

internal sealed record AnalysisExecutionMetrics(TimeSpan Elapsed, long InputBytes, long OutputBytes, bool CacheHit);
internal enum AnalysisCacheStatus { Miss, Hit, Malformed, Bypassed }
internal sealed record AnalysisExecutionResult(
    int ExitCode, AnalysisCacheStatus CacheStatus, string OutputPath,
    AnalysisExecutionMetrics Metrics, AnalysisOutput Output);

internal static class AnalysisProcessRunner
{
    private static readonly ConcurrentDictionary<string, AnalysisWorkerProbeResult> WorkerProbeCache = new(StringComparer.Ordinal);

    internal static AnalysisWorkerProbeResult Probe(string python, string script, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(python);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        string key = Identity(python) + "|" + Identity(script);
        if (WorkerProbeCache.TryGetValue(key, out AnalysisWorkerProbeResult cached))
            return cached with { CacheHit = true };

        var info = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("--probe");
        info.ArgumentList.Add("--json");
        AnalysisWorkerProbeResult result;
        try
        {
            using Process process = Process.Start(info)
                ?? throw new InvalidOperationException($"unable to start analysis worker probe '{python}'");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task exit = process.WaitForExitAsync();
            Task completed = Task.WhenAny(exit, Task.Delay(timeout)).GetAwaiter().GetResult();
            if (!ReferenceEquals(completed, exit))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { exit.GetAwaiter().GetResult(); } catch { }
                result = new(false, "", "worker compatibility probe timed out", false);
            }
            else
            {
                exit.GetAwaiter().GetResult();
                Task.WaitAll(stdout, stderr);
                string output = stdout.GetAwaiter().GetResult().Trim();
                string diagnostics = stderr.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0)
                    result = new(false, "", diagnostics.Length == 0 ? $"probe exited {process.ExitCode}" : diagnostics, false);
                else
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(output);
                        JsonElement root = document.RootElement;
                        string version = root.TryGetProperty("workerVersion", out JsonElement workerVersion)
                            ? workerVersion.GetString() ?? ""
                            : root.TryGetProperty("version", out JsonElement versionElement)
                                ? versionElement.GetString() ?? ""
                                : "";
                        int schema = root.TryGetProperty("schemaVersion", out JsonElement schemaElement)
                            ? schemaElement.GetInt32() : 0;
                        string music21 = root.TryGetProperty("music21Version", out JsonElement music21Element)
                            ? music21Element.GetString() ?? "" : "";
                        bool compatible = version == AnalysisResultValidator.ExpectedWorkerVersion
                            && schema == 1
                            && music21 == AnalysisResultValidator.ExpectedMusic21Version;
                        result = new(compatible, version,
                            compatible ? "" : $"unsupported worker metadata: version={version}, schema={schema}, music21={music21}", false);
                    }
                    catch (Exception ex)
                    {
                        result = new(false, "", $"invalid worker probe JSON: {ex.Message}", false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result = new(false, "", ex.Message, false);
        }
        WorkerProbeCache[key] = result;
        return result;
    }

    public static AnalysisProcessResult Run(string python, string script, string input, string output, AnalysisDetail detail, int timeoutMinutes)
    {
        if (string.IsNullOrWhiteSpace(python))
            throw new ArgumentException("analysis Python interpreter is required", nameof(python));
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("analysis worker script is required", nameof(script));
        if (timeoutMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMinutes), "analysis timeout must be positive");

        return RunCore(python, script, input, output, detail, TimeSpan.FromMinutes(timeoutMinutes));
    }

    internal static AnalysisProcessResult Run(string python, string script, string input, string output, AnalysisDetail detail, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return RunCore(python, script, input, output, detail, timeout);
    }

    private static AnalysisProcessResult RunCore(string python, string script, string input, string output, AnalysisDetail detail, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(script); info.ArgumentList.Add("--input"); info.ArgumentList.Add(input);
        info.ArgumentList.Add("--output"); info.ArgumentList.Add(output); info.ArgumentList.Add("--detail"); info.ArgumentList.Add(detail.ToString().ToLowerInvariant());
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"unable to start analysis worker '{python}'");
        // Start both reads before waiting. Reading one redirected stream
        // synchronously can deadlock a worker that fills the other pipe.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task exit = process.WaitForExitAsync();
        Task completed = Task.WhenAny(exit, Task.Delay(timeout)).GetAwaiter().GetResult();
        if (!ReferenceEquals(completed, exit))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { exit.GetAwaiter().GetResult(); } catch { }
            // Observe both redirected streams after killing the tree. This
            // prevents the killed worker's async readers from surviving the
            // request and keeps diagnostic stderr intact.
            string timedOutError = ReadAfterTermination(stderr);
            _ = ReadAfterTermination(stdout);
            return new AnalysisProcessResult(124, true, timedOutError);
        }
        exit.GetAwaiter().GetResult();
        Task.WaitAll(stdout, stderr);
        _ = stdout.GetAwaiter().GetResult();
        return new AnalysisProcessResult(process.ExitCode, false, stderr.GetAwaiter().GetResult());
    }

    private static string ReadAfterTermination(Task<string> reader)
    {
        try
        {
            if (!reader.Wait(TimeSpan.FromSeconds(2)))
                return string.Empty;
            return reader.GetAwaiter().GetResult();
        }
        catch (Exception) { return string.Empty; }
    }

    private static string Identity(string path)
    {
        try
        {
            FileInfo info = new(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return path; }
    }
}
