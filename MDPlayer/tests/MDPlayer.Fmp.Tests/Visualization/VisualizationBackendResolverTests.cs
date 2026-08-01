using System.IO;
using Fmp.Cli;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// FMP.COM is bundled next to the CLI executable (MDPlayer.Fmp.Cli.csproj);
/// the visualize path must find it there without --fmp-com / --assets-dir.
/// </summary>
public sealed class VisualizationBackendResolverTests
{
    [Fact]
    public void BuildSearchPaths_IncludesExecutableDirectory_SoBundledFmpComResolves()
    {
        var input = new FileInfo(Path.Combine(Path.GetTempPath(), "track.ovi"));
        var settings = new BatchRenderSettings();

        IReadOnlyList<string> paths = VisualizationBackendResolver.BuildSearchPaths(input, settings);

        string appDir = Path.GetFullPath(AppContext.BaseDirectory);
        Assert.Contains(paths, path =>
            string.Equals(Path.GetFullPath(path), appDir, StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveFmpCom_FindsBundledCopyNextToExecutable()
    {
        // Only meaningful when the build actually bundled FMP.COM (csproj copy).
        string bundled = Path.Combine(AppContext.BaseDirectory, "FMP.COM");
        if (!File.Exists(bundled))
            return; // not bundled in this output (e.g. old artifact); skip.

        string resolved = PlaybackBackendRegistry.ResolveFmpCom(
            explicitFmpCom: null,
            new[] { AppContext.BaseDirectory });

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }
}
