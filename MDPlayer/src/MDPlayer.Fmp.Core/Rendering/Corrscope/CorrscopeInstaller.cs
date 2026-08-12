using System.Diagnostics;

namespace Fmp.Core.Rendering.Corrscope;

/// <summary>
/// Raised when the Corrscope tool cannot be made available (the managed venv
/// could not be created, corrscope could not be installed, or the resolved
/// interpreter cannot import corrscope). Carries the underlying command output
/// so the caller can present a concrete, actionable error.
/// </summary>
internal sealed class CorrscopeInstallException : Exception
{
    public CorrscopeInstallException(string message, string? commandOutput = null, Exception? inner = null)
        : base(CombineMessage(message, commandOutput), inner)
    {
    }

    private static string CombineMessage(string message, string? commandOutput)
    {
        if (string.IsNullOrWhiteSpace(commandOutput))
            return message;
        const int cap = 3000;
        string trimmed = commandOutput.Length > cap
            ? commandOutput[..cap] + "... (truncated)"
            : commandOutput;
        return $"{message}\n{trimmed}";
    }
}

/// <summary>
/// Ensures a Corrscope runtime is present for scoped renders, installing it
/// into an isolated, project-local virtual environment when it is missing.
///
/// Locating / installing are idempotent: an existing usable Corrscope (on PATH
/// or via pipx) is preferred and used as-is; only when none is found does this
/// create <c>%LocalAppData%/MDPlayer/tools/corrscope-venv</c> and pip-install
/// corrscope into it. All steps run against a bounded timeout so a hung pip or
/// venv never stalls the render; failures throw <see cref="CorrscopeInstallException"/>.
/// </summary>
internal static class CorrscopeInstaller
{
    /// <summary>Default pip package name.</summary>
    public const string PackageName = "corrscope";

    /// <summary>Characteristic venv directory names per platform.</summary>
    public static string VenvHome { get; } =
        Environment.GetEnvironmentVariable("MDPLAYER_CORRSCOPE_VENV") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDPlayer", "tools", "corrscope-venv");

    /// <summary>Relative venv interpreter path per platform ("bin/python" or "Scripts\\python.exe").</summary>
    public static string VenvInterpreterRelativePath { get; } =
        OperatingSystem.IsWindows() ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python");

    /// <summary>Relative venv console-script path for the <c>corr</c> entry point.</summary>
    public static string VenvCorrRelativePath { get; } =
        OperatingSystem.IsWindows() ? Path.Combine("Scripts", "corr.exe") : Path.Combine("bin", "corr");

    private static readonly object CacheGate = new();
    private static string? CachedAutoPython;
    private static string? CachedExplicitPath;
    private static string? CachedExplicitPython;

    /// <summary>
    /// Makes Corrscope available and returns the Python interpreter that can
    /// import it. When <paramref name="explicitCorrPath"/> is supplied it is
    /// authoritative: its interpreter must work, otherwise this throws (an
    /// explicitly named Corrscope may not silently fall back). Otherwise the
    /// existing PATH/pipx install is reused, or a fresh managed venv is created
    /// and the package installed; if none succeeds this throws.
    /// </summary>
    public static string Ensure(TimeSpan timeout, string explicitCorrPath = null)
    {
        lock (CacheGate)
        {
            bool isExplicit = !string.IsNullOrWhiteSpace(explicitCorrPath);
            string? cachedPython = isExplicit
                ? string.Equals(CachedExplicitPath, explicitCorrPath, StringComparison.Ordinal)
                    ? CachedExplicitPython
                    : null
                : CachedAutoPython;
            if (cachedPython is not null && File.Exists(cachedPython))
            {
                return cachedPython;
            }

            string python = EnsureUncached(timeout, explicitCorrPath);
            if (isExplicit)
            {
                CachedExplicitPath = explicitCorrPath;
                CachedExplicitPython = python;
            }
            else
            {
                CachedAutoPython = python;
            }
            return python;
        }
    }

    private static string EnsureUncached(TimeSpan timeout, string explicitCorrPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitCorrPath))
        {
            string? python = ResolveCorrInterpreter(explicitCorrPath);
            if (CorrscopeImports(python))
                return python;
            throw new CorrscopeInstallException(
                $"The explicitly provided Corrscope at '{explicitCorrPath}' could not be used: " +
                "no interpreter that can 'import corrscope' was found. " +
                "Remove --corrscope to let MDPlayer manage a Corrscope install, or fix the path.");
        }

        // 1. Existing Corrscope on PATH (pipx, pip --user, system) wins. Its
        // interpreter is the corr script's own shebang interpreter first (pipx
        // venvs keep the interpreter their console script points at), falling
        // back to a sibling bin/python. Only a truly importable interpreter is
        // accepted, so a `corr` on PATH that belongs to a different Python is
        // not trusted.
        string? onPath = ResolveCorrOnPath();
        if (onPath != null)
        {
            string? python = ResolveCorrInterpreter(onPath);
            if (CorrscopeImports(python))
                return python;
        }

        // 2. Managed venv: use if already present and working.
        string venvPython = Path.Combine(VenvHome, VenvInterpreterRelativePath);
        if (File.Exists(venvPython) && CorrscopeImports(venvPython))
            return venvPython;

        // 3. Create / repair the managed venv.
        Directory.CreateDirectory(VenvHome);
        if (!File.Exists(venvPython))
            CreateVenv(venvPython, timeout);

        if (!CorrscopeImports(venvPython))
            PipInstall(venvPython, timeout);

        string corrPath = Path.Combine(VenvHome, VenvCorrRelativePath);
        if (!CorrscopeImports(venvPython) || !File.Exists(corrPath))
        {
            throw new CorrscopeInstallException(
                "Corrscope could not be installed: after provisioning the managed venv " +
                $"at '{VenvHome}', the '{PackageName}' package was still not importable.");
        }

        return venvPython;
    }

    /// <summary>Returns the <c>corr</c> executable path for a managed-venv Python (its sibling
    /// <c>corr</c> wrapper), or the resolved PATH <c>corr</c> when preferExisting is set.</summary>
    public static string ResolveCorrExecutable(string venvPython) =>
        Path.Combine(Path.GetDirectoryName(venvPython)!, OperatingSystem.IsWindows() ? "corr.exe" : "corr");

    private static string? ResolveCorrOnPath() =>
        ExecutableResolver.Resolve(null, "corr");

    /// <summary>
    /// Resolves the Python interpreter that can import corrscope for a given
    /// <c>corr</c> executable. Prefers the interpreter named in the corr script's
    /// shebang (pipx virtual environments point their console scripts at the
    /// venv's python), then a sibling <c>bin/python</c> / <c>bin/python3</c>.
    /// Returns null when none can be determined.
    /// </summary>
    private static string? ResolveCorrInterpreter(string corrPath)
    {
        string resolved = corrPath;
        try
        {
            if (File.Exists(corrPath))
                resolved = File.ResolveLinkTarget(corrPath, returnFinalTarget: true)?.FullName ?? corrPath;
        }
        catch
        {
            // Keep the original path.
        }

        // Shebang of the resolved console script (first line of the .py/.corr
        // script), e.g. "#!/.../pipx/venvs/corrscope/bin/python [opts]".
        if (File.Exists(resolved))
        {
            try
            {
                using var reader = new StreamReader(resolved);
                string first = reader.ReadLine()?.Trim() ?? string.Empty;
                if (first.StartsWith("#!", StringComparison.Ordinal))
                {
                    string interpreter = first[2..].Trim().Split(' ')[0].Trim();
                    if (interpreter.Length > 0 && File.Exists(interpreter))
                        return interpreter;
                }
            }
            catch
            {
                // Fall through to sibling lookup.
            }
        }

        return CorrscopeRunner.ResolvePythonPath(resolved);
    }

    private static void CreateVenv(string venvPython, TimeSpan timeout)
    {
        string launcher = ResolveLauncher();
        Run(
            launcher,
            new[] { "-m", "venv", VenvHome },
            timeout,
            onFailure: $"failed to create the Corrscope virtual environment at '{VenvHome}' with '{launcher}'.");
    }

    private static void PipInstall(string venvPython, TimeSpan timeout)
    {
        Run(
            venvPython,
            new[] { "-m", "pip", "install", "--upgrade", PackageName },
            timeout,
            onFailure: $"failed to 'pip install {PackageName}' into the managed venv at '{venvPython}'.");
    }

    private static bool CorrscopeImports(string pythonPath)
    {
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
            return false;
        try
        {
            var psi = new ProcessStartInfo(pythonPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import corrscope");
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return false;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(20_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ResolveLauncher() =>
        ExecutableResolver.Resolve(null, "python3") ?? "python3";

    private static void Run(
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        string onFailure)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new CorrscopeInstallException(
                $"{onFailure} The command exceeded the {timeout.TotalMinutes:0.#}-minute timeout.");
        }

        string output =
            (stdoutTask.GetAwaiter().GetResult()
             + "\n" + stderrTask.GetAwaiter().GetResult()).Trim();
        if (process.ExitCode != 0)
        {
            throw new CorrscopeInstallException(onFailure, output);
        }
    }
}
