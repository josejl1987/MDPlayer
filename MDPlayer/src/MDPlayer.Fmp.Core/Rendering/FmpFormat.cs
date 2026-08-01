namespace Fmp.Core.Rendering;

/// <summary>Canonical FMP-family filename recognition shared by CLI and backend code.</summary>
internal static class FmpFormat
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".opi", ".ovi", ".ozi", ".mpi", ".mvi", ".mzi",
    };

    public const string ExpectedDescription =
        "expected .ovi, .opi, .ozi, .mpi, .mvi, or .mzi";

    public static bool IsSupportedExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;
        string normalized = extension[0] == '.' ? extension : "." + extension;
        return Extensions.Contains(normalized);
    }
}
