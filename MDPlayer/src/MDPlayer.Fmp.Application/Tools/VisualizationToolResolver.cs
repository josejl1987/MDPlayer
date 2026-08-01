using System.Diagnostics;
using Fmp.Application.Contracts;

namespace Fmp.Application.Tools;

/// <summary>
/// Resolves the external tools the visualization pipeline needs (ffmpeg,
/// corrscope, the analysis Python interpreter and the mdplayer-render CLI)
/// and probes their versions.
/// <para>
/// Resolution order per role: explicit override → bundled
/// (<see cref="AppContext.BaseDirectory"/>) → PATH. Sources are reported as
/// "override", "user setting", "bundled" or "PATH".
/// </para>
/// </summary>
public static class VisualizationToolResolver
{
    /// <summary>Version-probe timeout; a slower probe kills the process and reports failure.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Resolves a single tool. For <see cref="ToolRoles.RenderCli"/> the
    /// explicit path comes from <paramref name="userSettingPath"/> (a user
    /// preference, reported as source "user setting"); for the other roles it
    /// comes from the corresponding <see cref="ToolPaths"/> override.
    /// </summary>
    public static ToolStatus Resolve(string role, ToolPaths overrides, string? userSettingPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        string? explicitPath = role switch
        {
            ToolRoles.Ffmpeg => overrides?.FfmpegPath,
            ToolRoles.Corrscope => overrides?.CorrscopePath,
            ToolRoles.AnalysisPython => overrides?.AnalysisPython,
            ToolRoles.RenderCli => userSettingPath,
            _ => null,
        };

        string explicitSource = role == ToolRoles.RenderCli ? "user setting" : "override";
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Available(role, Path.GetFullPath(explicitPath), explicitSource);

        string[] names = CandidateNames(role);

        // Bundled: next to the running application.
        string appDir = AppContext.BaseDirectory;
        foreach (string name in names)
        {
            string bundled = Path.Combine(appDir, name);
            if (File.Exists(bundled))
                return Available(role, bundled, "bundled");
        }

        string? onPath = FindOnPath(names);
        if (onPath != null)
            return Available(role, onPath, "PATH");

        return new ToolStatus
        {
            Role = role,
            IsAvailable = false,
            Detail = "not found on PATH",
        };
    }

    /// <summary>Resolves all four pipeline tool roles in one call.</summary>
    public static IReadOnlyList<ToolStatus> ResolveAll(ToolPaths overrides, string? renderCliUserPath = null)
    {
        return
        [
            Resolve(ToolRoles.Ffmpeg, overrides),
            Resolve(ToolRoles.Corrscope, overrides),
            Resolve(ToolRoles.AnalysisPython, overrides),
            Resolve(ToolRoles.RenderCli, overrides, renderCliUserPath),
        ];
    }

    /// <summary>
    /// Runs the tool's <c>--version</c> (or <c>-version</c> for ffmpeg) with a
    /// 15-second timeout and fills <see cref="ToolStatus.Version"/>. When the
    /// probe fails the returned status has <see cref="ToolStatus.IsAvailable"/>
    /// set to false and a <see cref="ToolStatus.Detail"/> explaining why.
    /// </summary>
    public static async Task<ToolStatus> ProbeVersionAsync(ToolStatus status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.IsAvailable || string.IsNullOrEmpty(status.ResolvedPath))
            return status;

        string versionArg = status.Role switch
        {
            ToolRoles.Ffmpeg => "-version",
            _ => "--version",
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        var psi = new ProcessStartInfo(status.ResolvedPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(versionArg);

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return status with { IsAvailable = false, Detail = "failed to start the version probe" };

            using var killOnTimeout = timeoutCts.Token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            string? version = FirstLine(stdout) ?? FirstLine(stderr);
            if (string.IsNullOrWhiteSpace(version))
            {
                string detail = process.ExitCode == 0
                    ? "tool returned no version output"
                    : $"version probe exited with code {process.ExitCode}";
                return status with { IsAvailable = false, Detail = detail };
            }

            return status with { Version = version.Trim() };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return status with { IsAvailable = false, Detail = "version probe timed out after 15 seconds" };
        }
        catch (Exception ex)
        {
            return status with { IsAvailable = false, Detail = ex.Message };
        }
    }

    private static ToolStatus Available(string role, string path, string source) => new()
    {
        Role = role,
        IsAvailable = true,
        ResolvedPath = path,
        Source = source,
    };

    private static string[] CandidateNames(string role)
    {
        bool windows = OperatingSystem.IsWindows();
        return role switch
        {
            ToolRoles.Ffmpeg => windows ? ["ffmpeg.exe"] : ["ffmpeg"],
            ToolRoles.Corrscope => windows ? ["corrscope.exe"] : ["corrscope"],
            ToolRoles.AnalysisPython => windows ? ["python.exe"] : ["python3", "python"],
            ToolRoles.RenderCli => windows ? ["mdplayer-render.exe"] : ["mdplayer-render"],
            _ => Array.Empty<string>(),
        };
    }

    private static string? FindOnPath(string[] names)
    {
        string pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                try
                {
                    string candidate = Path.Combine(directory, name);
                    if (File.Exists(candidate))
                        return candidate;
                    if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string exe = candidate + ".exe";
                        if (File.Exists(exe))
                            return exe;
                    }
                }
                catch
                {
                    // Unreadable PATH entry; keep searching.
                }
            }
        }
        return null;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim('\r', ' ', '\t');
            if (trimmed.Length > 0)
                return trimmed;
        }
        return null;
    }
}
