using System.Diagnostics;

namespace Fmp.Cli;

internal sealed record AnalysisProcessResult(int ExitCode, bool TimedOut, string StandardError);

internal static class AnalysisProcessRunner
{
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
}
