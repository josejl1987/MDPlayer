namespace Fmp.Benchmarks;

/// <summary>Deterministic, repository-relative selection for real MIDI fixtures.</summary>
internal static class MidiFixtureResolver
{
    private static readonly string[] KnownFixtures =
    [
        "21 Master Ninja.vgz",
        "26 - Robotnik.vgz",
        "02 Stranger ~ Wandering Swordsman.vgz",
        "05 - Twilight Express.vgz",
        "XA2021.OVI",
    ];

    public static string? Resolve(string? requested)
    {
        string root = FindRepositoryRoot(requested ?? Environment.CurrentDirectory);
        if (requested is not null)
        {
            string path = Path.GetFullPath(requested, Environment.CurrentDirectory);
            return File.Exists(path) && IsSupported(path) ? path : null;
        }

        return KnownFixtures.Select(name => Path.Combine(root, name))
            .FirstOrDefault(path => File.Exists(path));
    }

    public static string FindRepositoryRoot(string path)
    {
        string? current = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
        for (int depth = 0; depth < 10 && current is not null; depth++)
        {
            if (File.Exists(Path.Combine(current, ".git", "HEAD")) || Directory.Exists(Path.Combine(current, ".git")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        return Environment.CurrentDirectory;
    }

    private static bool IsSupported(string path) =>
        Path.GetExtension(path).Equals(".vgz", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".ovi", StringComparison.OrdinalIgnoreCase);
}
