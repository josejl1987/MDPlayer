namespace Fmp.Core.Rendering;

/// <summary>
/// Resolves executable paths from explicit option or PATH.
/// </summary>
internal static class ExecutableResolver
{
    /// <summary>
    /// Resolves an executable by checking: explicit path → PATH.
    /// Returns null if not found.
    /// </summary>
    public static string Resolve(string explicitPath, string name)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        foreach (string dir in
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
                if (File.Exists(full + ".exe")) return full + ".exe";
                if (File.Exists(full + ".bat")) return full + ".bat";
            }
            catch { }
        }
        return null;
    }
}
