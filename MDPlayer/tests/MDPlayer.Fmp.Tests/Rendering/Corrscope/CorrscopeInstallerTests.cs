using Fmp.Core.Rendering.Corrscope;
using Xunit;

namespace MDPlayer.Fmp.Tests.Rendering.Corrscope;

/// <summary>
/// Deterministic tests for the managed Corrscope installer: venv layout and the
/// executable derivation that the production scope path relies on. Actual venv
/// creation and pip install are environment/network dependent and are exercised
/// during render rather than in unit tests.
/// </summary>
public class CorrscopeInstallerTests
{
    [Fact]
    public void VenvInterpreterPath_UsesPlatformLayout()
    {
        string relative = CorrscopeInstaller.VenvInterpreterRelativePath;
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine("Scripts", "python.exe"), relative);
        }
        else
        {
            Assert.Equal(Path.Combine("bin", "python"), relative);
        }
    }

    [Fact]
    public void VenvHome_ResidesUnderLocalAppData()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(root, CorrscopeInstaller.VenvHome);
        Assert.EndsWith(Path.Combine("MDPlayer", "tools", "corrscope-venv"), CorrscopeInstaller.VenvHome);
    }

    [Fact]
    public void ResolveCorrExecutable_IsSiblingWrapper()
    {
        // Unix-style venv: bin/python -> bin/corr
        string venvPython = Path.Combine(CorrscopeInstaller.VenvHome, "bin", "python");
        string corr = CorrscopeInstaller.ResolveCorrExecutable(venvPython);
        Assert.Equal(Path.Combine(CorrscopeInstaller.VenvHome, "bin",
            OperatingSystem.IsWindows() ? "corr.exe" : "corr"), corr);
    }

    [Fact]
    public void InstallException_EmbeddsCommandOutput()
    {
        var ex = new CorrscopeInstallException("boom", "stderr detail");
        Assert.Contains("boom", ex.Message);
        Assert.Contains("stderr detail", ex.Message);
    }

    [Fact]
    public void InstallException_CapsHugeOutput()
    {
        string huge = new('x', 10_000);
        var ex = new CorrscopeInstallException("boom", huge);
        Assert.True(ex.Message.Length < 10_000);
        Assert.Contains("(truncated)", ex.Message);
    }
}
