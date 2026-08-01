using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class AnalysisProcessRunnerTests
{
    [Fact]
    public void Run_RejectsNonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisProcessRunner.Run(
            "/bin/sh", "worker.sh", "input.json", "output.json", AnalysisDetail.Minimal, 0));
    }

    [Fact]
    public void Run_KillsWorkerWhenTimeoutExpires()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), "mdplayer-analysis-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script = Path.Combine(root, "worker.sh");
            File.WriteAllText(script, "sleep 10\n");

            AnalysisProcessResult result = AnalysisProcessRunner.Run(
                "/bin/sh", script, "input.json", "output.json", AnalysisDetail.Minimal,
                TimeSpan.FromMilliseconds(100));

            Assert.True(result.TimedOut);
            Assert.Equal(124, result.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_DrainsBothRedirectedStreamsBeforeWaiting()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), "mdplayer-analysis-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script = Path.Combine(root, "worker.sh");
            File.WriteAllText(script,
                "i=0\n" +
                "while [ $i -lt 20000 ]; do echo stderr-line >&2; i=$((i+1)); done\n" +
                "exit 0\n");

            AnalysisProcessResult result = AnalysisProcessRunner.Run(
                "/bin/sh", script, Path.Combine(root, "input.json"),
                Path.Combine(root, "output.json"), AnalysisDetail.Minimal, 1);

            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("stderr-line", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_TerminatesWorkerWhenTimeoutExpires()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = Path.Combine(Path.GetTempPath(), "mdplayer-analysis-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script = Path.Combine(root, "worker.sh");
            File.WriteAllText(script, "echo worker-started >&2\nsleep 5\n");

            AnalysisProcessResult result = AnalysisProcessRunner.Run(
                "/bin/sh", script, Path.Combine(root, "input.json"),
                Path.Combine(root, "output.json"), AnalysisDetail.Minimal,
                TimeSpan.FromMilliseconds(100));

            Assert.True(result.TimedOut);
            Assert.Equal(124, result.ExitCode);
            Assert.Contains("worker-started", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
