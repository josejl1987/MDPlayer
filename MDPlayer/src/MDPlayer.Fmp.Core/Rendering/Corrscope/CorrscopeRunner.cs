using System.Diagnostics;

namespace Fmp.Core.Rendering.Corrscope;

/// <summary>
/// Invokes Corrscope to render a video from a YAML project file.
/// Modeled on the FFmpeg process-lifecycle pattern used elsewhere in the pipeline.
/// Writes to a temporary path and atomically renames on success.
///
/// Corrscope is an external Python tool (BSD-2-Clause) installed via pip:
///   pip install corrscope
/// or from source at https://github.com/corrscope/corrscope
/// </summary>
internal class CorrscopeRunner
{
    private readonly string _corrPath;
    private bool _available;
    private readonly int _timeoutMinutes;

    public CorrscopeRunner(int timeoutMinutes = 30, string executablePath = null)
    {
        _corrPath = ExecutableResolver.Resolve(executablePath, "corr");
        _available = _corrPath != null;
        _timeoutMinutes = timeoutMinutes;
    }

    /// <summary>Whether the `corr` binary was found on PATH.</summary>
    public bool IsAvailable => _available;

    /// <summary>Path to the `corr` binary, or "corr" if not found.</summary>
    public string CorrPath => _corrPath ?? "corr";

    /// <summary>
    /// Render a video from a Corrscope YAML project file.
    /// </summary>
    /// <param name="yamlPath">Path to the corrscope.yaml project file.</param>
    /// <param name="outputVideoPath">Desired output video path (e.g. out/video.mp4).</param>
    /// <param name="preview">If true, run `corr --play` for preview instead of encoding.</param>
    /// <returns>The path to the rendered video on success, null on failure.</returns>
    public string Render(string yamlPath, string outputVideoPath, bool preview = false)
    {
        if (!_available)
            throw new InvalidOperationException(
                "corr not found on PATH. Install with: pip install corrscope\n" +
                $"YAML project file available at: {yamlPath}");

        string tempPath = outputVideoPath + ".tmp.mp4";

        var args = new List<string>
        {
            yamlPath
        };

        if (preview)
        {
            args.Add("--play");
        }
        else
        {
            args.Add("--render");
            args.Add(tempPath);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _corrPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }
        };
        foreach (string arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        string stderr = null;
        string stdout = null;
        int exitCode;
        try
        {
            process.Start();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            bool exited = process.WaitForExit((int)TimeSpan.FromMinutes(_timeoutMinutes).TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"corr exceeded the {_timeoutMinutes}-minute timeout");
            }

            stderr = stderrTask.GetAwaiter().GetResult();
            stdout = stdoutTask.GetAwaiter().GetResult();

            if (preview)
            {
                // Preview mode runs indefinitely; user stops it. Return success.
                return null;
            }

            exitCode = process.ExitCode;

            if (exitCode != 0 || !File.Exists(tempPath))
            {
                string allOutput = stdout + "\n" + stderr;
                if (allOutput.Length > 2000)
                    allOutput = allOutput[..2000] + "... (truncated)";
                throw new InvalidOperationException(
                    $"corr failed (exit {exitCode}):\n{allOutput}");
            }

            // Atomic rename: temp → output
            if (File.Exists(outputVideoPath))
                File.Delete(outputVideoPath);
            File.Move(tempPath, outputVideoPath);

            return outputVideoPath;
        }
        finally
        {
            try { if (!preview && File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            process.Dispose();
        }
    }

    /// <summary>
    /// Starts the raw-frame bridge: renders the YAML project to raw RGBA frames
    /// on stdout with no video encoding. The caller consumes
    /// <see cref="Process.StandardOutput"/> as a binary stream and must call
    /// <see cref="Process.WaitForExit()"/> and check the exit code.
    /// </summary>
    public Process StartRawFrames(string pythonPath, string bridgeScriptPath, string yamlPath)
    {
        if (!_available)
            throw new InvalidOperationException(
                "corr not found on PATH. Install with: pip install corrscope\n" +
                $"YAML project file available at: {yamlPath}");
        if (string.IsNullOrWhiteSpace(pythonPath))
            throw new ArgumentException("Python interpreter path is required.", nameof(pythonPath));
        if (string.IsNullOrWhiteSpace(bridgeScriptPath))
            throw new ArgumentException("Bridge script path is required.", nameof(bridgeScriptPath));

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        process.StartInfo.ArgumentList.Add(bridgeScriptPath);
        process.StartInfo.ArgumentList.Add(yamlPath);
        process.Start();
        return process;
    }

    /// <summary>
    /// Resolves the Python interpreter that can import corrscope, preferring a
    /// sibling of the `corr` executable (typical for venv installs:
    /// <c>.../bin/corr</c> → <c>.../bin/python</c>).
    /// </summary>
    public static string ResolvePythonPath(string corrPath)
    {
        string resolvedCorrPath = ResolveLinkTarget(corrPath);
        string directory = string.IsNullOrWhiteSpace(resolvedCorrPath)
            ? null
            : Path.GetDirectoryName(resolvedCorrPath);
        if (directory != null && Directory.Exists(directory))
        {
            foreach (string candidate in new[] { "python", "python3" })
            {
                string full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                    return full;
            }
        }
        return "python3";
    }

    private static string ResolveLinkTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try
        {
            FileSystemInfo target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch
        {
            // Some executable wrappers are not filesystem links. Keep the
            // original path and let the normal sibling/PATH fallback apply.
            return path;
        }
    }

    /// <summary>
    /// Get the corrscope version string.
    /// </summary>
    public string GetVersion()
    {
        if (!_available) return "not found";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _corrPath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd().Trim();
        if (string.IsNullOrEmpty(output))
            output = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit(5000);
        return string.IsNullOrEmpty(output) ? "unknown" : output;
    }
}
