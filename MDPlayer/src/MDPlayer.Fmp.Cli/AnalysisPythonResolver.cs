using System.Diagnostics;
using System.ComponentModel;

namespace Fmp.Cli;

internal sealed record AnalysisPython(string Path, string Music21Version);

internal static class AnalysisPythonResolver
{
    internal const string ExpectedMusic21Version = "10.5.0";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static AnalysisPython Resolve(string explicitPath, string repositoryRoot = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
            candidates.Add(explicitPath);
        else
        {
            string configured = Environment.GetEnvironmentVariable("MDPLAYER_ANALYSIS_PYTHON");
            if (!string.IsNullOrWhiteSpace(configured))
                candidates.Add(configured);
            string currentLocal = Path.Combine(Directory.GetCurrentDirectory(), ".analysis-venv", "bin", "python");
            string repoLocal = string.IsNullOrWhiteSpace(repositoryRoot)
                ? ""
                : Path.Combine(repositoryRoot, ".analysis-venv", "bin", "python");
            if (File.Exists(currentLocal)) candidates.Add(currentLocal);
            if (File.Exists(repoLocal) && !candidates.Contains(repoLocal, StringComparer.Ordinal)) candidates.Add(repoLocal);
            candidates.Add("python3");
            candidates.Add("python");
        }
        string lastError = "no Python interpreter found";
        foreach (string path in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var probe = new ProcessStartInfo(path)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                probe.ArgumentList.Add("-c"); probe.ArgumentList.Add("import music21; print(music21.__version__)");
                using var process = Process.Start(probe);
                if (process is null)
                {
                    lastError = $"unable to start Python '{path}'";
                    continue;
                }
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task exit = process.WaitForExitAsync();
                Task completed = Task.WhenAny(exit, Task.Delay(ProbeTimeout)).GetAwaiter().GetResult();
                if (!ReferenceEquals(completed, exit))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { exit.GetAwaiter().GetResult(); } catch { }
                    WaitForProbeStreams(stdoutTask, stderrTask);
                    lastError = $"Python probe timed out for '{path}'";
                    continue;
                }
                exit.GetAwaiter().GetResult();
                Task.WaitAll(stdoutTask, stderrTask);
                string stdout = stdoutTask.GetAwaiter().GetResult().Trim();
                string stderr = stderrTask.GetAwaiter().GetResult().Trim();
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    lastError = $"music21 is unavailable in Python '{path}': {stderr}";
                    continue;
                }
                string version = stdout.Split('\n')[^1].Trim();
                if (!string.Equals(version, ExpectedMusic21Version, StringComparison.Ordinal))
                {
                    lastError = $"unsupported music21 version '{version}' in Python '{path}'; expected {ExpectedMusic21Version}";
                    continue;
                }
                return new AnalysisPython(path, version);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                lastError = ex.Message;
            }
        }
        throw new InvalidOperationException(lastError);
    }

    private static void WaitForProbeStreams(Task<string> stdout, Task<string> stderr)
    {
        try { Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(1)); }
        catch { }
    }
}
