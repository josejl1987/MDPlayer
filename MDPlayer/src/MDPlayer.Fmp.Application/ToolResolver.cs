namespace Fmp.Cli;

/// <summary>
/// Resolves executable and asset file paths from explicit option, assets
/// directory, executable directory, and PATH.
/// </summary>
internal static class ToolResolver
{
    /// <summary>
    /// Resolves a file by checking: explicit path → assets directory →
    /// executable directory. Returns null if not found.
    /// </summary>
    public static string ResolveFile(string explicitPath, string assetsDir, string filename)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        if (!string.IsNullOrEmpty(assetsDir))
        {
            string candidate = Path.Combine(assetsDir, filename);
            if (File.Exists(candidate))
                return candidate;
        }

        string appPath = Path.Combine(AppContext.BaseDirectory, filename);
        if (File.Exists(appPath))
            return appPath;

        return null;
    }

    /// <summary>
    /// Resolves an executable by checking: explicit path → PATH.
    /// Returns null if not found.
    /// </summary>
    public static string ResolveExecutable(string explicitPath, string name)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        foreach (string directory in
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
                if (File.Exists(candidate + ".exe"))
                    return candidate + ".exe";
            }
            catch { }
        }
        return null;
    }
}
