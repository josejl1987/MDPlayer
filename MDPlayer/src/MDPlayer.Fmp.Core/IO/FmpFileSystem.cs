using System.Security.Cryptography;

namespace Fmp.Core.IO;

/// <summary>
/// DOS-compatible file system with case-insensitive lookup,
/// search-path precedence, and ambiguous-match detection.
/// </summary>
public class FmpFileSystem : IFmpFileSystem
{
    private readonly List<string> _searchPaths = new();

    public FmpFileSystem(IEnumerable<string> searchPaths)
    {
        foreach (var p in searchPaths)
        {
            var normalized = NormalizePath(p);
            if (!string.IsNullOrEmpty(normalized) && Directory.Exists(normalized))
                _searchPaths.Add(normalized);
        }
    }

    public bool TryReadFile(DosPath path, out ReadOnlyMemory<byte> data, out ResolvedFmpFile source)
    {
        data = default;
        source = default;

        var dosPath = path.Value;
        if (string.IsNullOrEmpty(dosPath)) return false;

        // Prevent directory traversal
        if (ContainsTraversal(dosPath))
        {
            throw new InvalidOperationException($"Path traversal detected: {dosPath}");
        }

        // Extract filename and optional directory prefix
        string fileName = dosPath;
        int lastSep = dosPath.LastIndexOf('\\');
        if (lastSep >= 0) fileName = dosPath.Substring(lastSep + 1);

        string relativeDir = lastSep >= 0 ? dosPath.Substring(0, lastSep) : "";

        // Reject rooted relativeDir — prevents search-path sandbox escape
        if (!string.IsNullOrEmpty(relativeDir))
        {
            string normalizedRel = relativeDir.Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedRel))
                throw new InvalidOperationException(
                    $"Absolute DOS path not allowed: {dosPath}");
        }

        // Reject UNC-style paths (\\server\share\...)
        if (dosPath.StartsWith("\\\\"))
            throw new InvalidOperationException(
                $"UNC-style DOS path not allowed: {dosPath}");

        // Search paths in order — first match wins
        foreach (var searchDir in _searchPaths)
        {
            string targetDir = searchDir;
            if (!string.IsNullOrEmpty(relativeDir))
                targetDir = Path.Combine(searchDir, relativeDir.Replace('\\', Path.DirectorySeparatorChar));

            if (!Directory.Exists(targetDir)) continue;

            var (found, isAmbiguous) = FindCaseInsensitiveFile(targetDir, fileName);
            if (isAmbiguous)
            {
                throw new InvalidOperationException(
                    $"Ambiguous DOS filename: {fileName}\n" +
                    $"Directory: {targetDir}");
            }
            if (found != null)
            {
                byte[] bytes = File.ReadAllBytes(found);
                source = new ResolvedFmpFile(
                    RequestedName: fileName,
                    ResolvedPath: Path.GetFullPath(found),
                    Sha256: Convert.ToHexString(SHA256.HashData(bytes))
                );
                data = bytes.AsMemory();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Find a file in a directory using case-insensitive comparison.
    /// Returns (null, false) if not found, (path, false) if found uniquely,
    /// or (null, true) if ambiguous (multiple matches).
    /// </summary>
    private static (string? found, bool isAmbiguous) FindCaseInsensitiveFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory)) return (null, false);

        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch { return (null, false); }

        List<string> matches = new();
        foreach (var f in files)
        {
            string name = Path.GetFileName(f);
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                matches.Add(f);
        }

        if (matches.Count == 1)
            return (matches[0], false);

        if (matches.Count > 1)
            return (null, true);

        return (null, false);
    }

    private static bool ContainsTraversal(string path)
    {
        string normalized = path.Replace('/', '\\');
        return normalized.Contains("..\\") || normalized.StartsWith("..\\")
            || normalized.Contains("\\..\\") || normalized.EndsWith("\\..");
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.Replace('/', Path.DirectorySeparatorChar)
                   .Replace('\\', Path.DirectorySeparatorChar)
                   .TrimEnd(Path.DirectorySeparatorChar);
    }
}
