using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Opna;

/// <summary>
/// Proves the PPZ8 extraction is bit-identical: the shared renderer port at
/// <c>src/MDPlayer.Fmp.Core/Nise98/NisePPZ8.cs</c> must be a verbatim copy of
/// the legacy <c>MDPlayerx64/Driver/FMP/Nise98/NisePPZ8.cs</c> implementation,
/// differing only in the documented header (using directives + namespace).
/// This keeps both future backends (MDSound and native) able to reuse the same
/// PPZ8 rendering logic.
/// </summary>
public sealed class Ppz8ExtractionFidelityTests
{
    private const string LegacyPath = "MDPlayerx64/Driver/FMP/Nise98/NisePPZ8.cs";
    private const string ExtractedPath = "src/MDPlayer.Fmp.Core/Nise98/NisePPZ8.cs";

    [Fact]
    public void ExtractedNisePpz8_IsVerbatimCopy_OfLegacyImplementation()
    {
        string root = FindRepositoryRoot();
        Assert.NotNull(root);

        string legacyFile = Path.Combine(root, LegacyPath);
        string extractedFile = Path.Combine(root, ExtractedPath);
        Assert.True(File.Exists(legacyFile), $"missing legacy file {LegacyPath}");
        Assert.True(File.Exists(extractedFile), $"missing extracted file {ExtractedPath}");

        string legacy = Normalize(File.ReadAllText(legacyFile), legacy: true);
        string extracted = Normalize(File.ReadAllText(extractedFile), legacy: false);

        Assert.True(
            string.Equals(legacy, extracted, StringComparison.Ordinal),
            "The Fmp.Core NisePPZ8 extraction drifted from the legacy implementation. " +
            "Keep src/MDPlayer.Fmp.Core/Nise98/NisePPZ8.cs a verbatim copy of " +
            "MDPlayerx64/Driver/FMP/Nise98/NisePPZ8.cs (only the using block and " +
            "namespace declaration may differ) so both backends share bit-identical PPZ8 rendering.");
    }

    /// <summary>
    /// Normalizes the two copies to a comparable form: strips the BOM, trims
    /// trailing whitespace per line, drops the legacy-only using directives and
    /// renames the legacy namespace to the Fmp.Core namespace.
    /// </summary>
    private static string Normalize(string text, bool legacy)
    {
        text = text.TrimStart('\uFEFF');
        string[] lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd()).ToArray();

        var kept = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            if (legacy)
            {
                if (line.StartsWith("using MDPlayer.Driver.FMP.Nise98;", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("using musicDriverInterface;", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("namespace MDPlayer.Driver.FMP.Nise98", StringComparison.Ordinal))
                {
                    kept.Add(line.Replace("namespace MDPlayer.Driver.FMP.Nise98", "namespace Fmp.Core.Nise98", StringComparison.Ordinal));
                    continue;
                }
            }
            kept.Add(line);
        }
        return string.Join("\n", kept).TrimEnd();
    }

    private static string FindRepositoryRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (File.Exists(Path.Combine(dir, "MDPlayerx64", "MDPlayerx64.csproj")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
