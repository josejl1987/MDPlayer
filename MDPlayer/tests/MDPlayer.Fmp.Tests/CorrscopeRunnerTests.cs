using Fmp.Core.Rendering.Corrscope;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class CorrscopeRunnerTests
{
    [SkippableFact]
    public void ResolvePythonPath_FollowsCorrscopeSymlinkToItsVenv()
    {
        Skip.If(OperatingSystem.IsWindows(), "Corrscope symlink resolution test requires Unix semantics.");

        string root = Directory.CreateTempSubdirectory("fmp-corrscope-runner-").FullName;
        try
        {
            string venvBin = Path.Combine(root, "pipx", "corrscope", "bin");
            Directory.CreateDirectory(venvBin);
            string targetCorr = Path.Combine(venvBin, "corr");
            string targetPython = Path.Combine(venvBin, "python");
            File.WriteAllText(targetCorr, "#!/bin/sh\n");
            File.WriteAllText(targetPython, "#!/bin/sh\n");

            string link = Path.Combine(root, "corr");
            File.CreateSymbolicLink(link, targetCorr);

            Assert.Equal(targetPython, CorrscopeRunner.ResolvePythonPath(link));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
