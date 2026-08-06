using System.IO;
using Fmp.Core.Rendering.Corrscope;
using Xunit;

namespace MDPlayer.Fmp.Tests.Rendering.Corrscope;

/// <summary>
/// Opt-in end-to-end verification of the real venv provisioning + pip install
/// path. Only runs when MDPLAYER_RUN_INSTALL_TEST is set (manual/CI with
/// network and a working python3); otherwise skips so offline builds stay green.
/// </summary>
public class CorrscopeInstallerEndToEndTests
{
    [Fact]
    public void Ensure_ResolvesAnInterpretableRuntime()
    {
        // Opt-in: requires python3 (+ network for a first-time install). Only
        // runs when MDPLAYER_RUN_INSTALL_TEST is set so offline builds stay green.
        if (Environment.GetEnvironmentVariable("MDPLAYER_RUN_INSTALL_TEST") is not { Length: > 0 })
            return;

        string venv = Path.Combine(Path.GetTempPath(), "mdplayer-corrscope-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MDPLAYER_CORRSCOPE_VENV", venv);
        try
        {
            string python = CorrscopeInstaller.Ensure(TimeSpan.FromMinutes(5));
            Assert.True(File.Exists(python), $"resolved python does not exist: {python}");
            // The resolved interpreter must really import corrscope.
            Assert.True(ImportsCorrscope(python), $"resolved interpreter cannot import corrscope: {python}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MDPLAYER_CORRSCOPE_VENV", null);
            try { Directory.Delete(venv, recursive: true); } catch { }
        }
    }

    private static bool ImportsCorrscope(string python)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(python)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("import corrscope");
        process.Start();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(20_000);
        return process.ExitCode == 0;
    }
}
