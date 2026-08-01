using System.Diagnostics;
using System.Text;

namespace Fmp.Gui.Services;

/// <summary>
/// Argument-safe child-process helpers. Executables and arguments are always
/// passed through <see cref="ProcessStartInfo.ArgumentList"/> (never string
/// concatenation), which prevents injection via file paths or settings values.
/// </summary>
public static class DesktopProcessService
{
    /// <summary>Builds a start-info with argument-safe quoting (UseShellExecute=false).</summary>
    public static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    /// <summary>Kills a process and its whole descendant tree.</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone or no permission.
        }
    }

    /// <summary>Runs a process to completion and captures stdout/stderr.</summary>
    public static async Task<ProcessResult> RunCaptureAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return new ProcessResult(-1, "", "Failed to start process.");

        using var killOnCancel = ct.Register(() => KillTree(process));

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Opens a terminal at the given directory. Linux: tries
    /// x-terminal-emulator / gnome-terminal / xfce4-terminal / konsole with a
    /// working-directory switch (best effort). Windows: explorer.exe.
    /// </summary>
    public static void OpenTerminalAtDirectory(string directory)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                explorer.ArgumentList.Add(directory);
                Process.Start(explorer);
                return;
            }

            foreach (string terminal in new[] { "x-terminal-emulator", "gnome-terminal", "xfce4-terminal", "konsole" })
            {
                try
                {
                    var psi = new ProcessStartInfo(terminal) { UseShellExecute = false, CreateNoWindow = true };
                    psi.ArgumentList.Add("--working-directory");
                    psi.ArgumentList.Add(directory);
                    if (Process.Start(psi) is not null)
                        return;
                }
                catch
                {
                    // Try the next terminal emulator.
                }
            }
        }
        catch
        {
            // Best effort; nothing to do if no terminal is available.
        }
    }

    /// <summary>Opens a file or directory with the desktop's default handler.</summary>
    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best effort; the UI remains usable when no handler is installed.
        }
    }

    /// <summary>
    /// Resolves the mdplayer-render executable: MDPLAYER_RENDER_PATH env →
    /// user settings path → application directory → PATH.
    /// </summary>
    public static string? ResolveRenderCli(string? userSettingPath = null)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("MDPLAYER_RENDER_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        if (!string.IsNullOrWhiteSpace(userSettingPath) && File.Exists(userSettingPath))
            return Path.GetFullPath(userSettingPath);

        string name = OperatingSystem.IsWindows() ? "mdplayer-render.exe" : "mdplayer-render";
        string bundled = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(bundled))
            return bundled;

        return FindOnPath(name);
    }

    private static string? FindOnPath(string name)
    {
        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
                if (!OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                    return candidate + ".exe";
            }
            catch
            {
                // Unreadable PATH entry.
            }
        }
        return null;
    }

    /// <summary>Captured process output.</summary>
    public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + Environment.NewLine + StdErr;
        public string? FirstNonEmptyLine()
        {
            foreach (string line in Combined.Split('\n'))
            {
                string trimmed = line.Trim('\r', ' ', '\t');
                if (trimmed.Length > 0)
                    return trimmed;
            }
            return null;
        }
    }
}
