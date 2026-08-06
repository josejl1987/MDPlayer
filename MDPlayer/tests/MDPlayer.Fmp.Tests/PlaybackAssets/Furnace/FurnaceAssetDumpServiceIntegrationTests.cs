using Fmp.Core.Playback.Opna;
using Fmp.Core.PlaybackAssets.Furnace;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Furnace;

/// <summary>
/// End-to-end: the public <see cref="FurnaceAssetDumpService"/> (the exact code
/// the GUI "Export Furnace…" button calls) must produce .tfi + manifest for a
/// real OVI through the native OPNA render. Skipped cleanly when the native
/// library or a fixture is not available.
/// </summary>
public class FurnaceAssetDumpServiceIntegrationTests
{
    private static readonly string[] FixtureNames =
    {
        "Action 3.OVI",
        "Action 1(1).OVI",
        "Alejandra.OVI",
    };

    [Fact]
    public void Dump_Produces_42ByteTfi_AndManifest_ForRealOvi()
    {
        // The native FMP render is slow (minutes); keep it opt-in so the
        // default suite stays fast. Run with FMP_DUMP_SLOW_TESTS=1 to verify.
        if (Environment.GetEnvironmentVariable("FMP_DUMP_SLOW_TESTS") != "1")
            return;

        string baseDir = AppContext.BaseDirectory;
        string? ovi = FixtureNames
            .Select(n => Path.Combine(baseDir, n))
            .FirstOrDefault(File.Exists);
        if (ovi is null)
            return; // fixture absent: skip quietly

        string? fmpCom = TryResolveFmpCom(baseDir);
        if (fmpCom is null)
            return; // FMP.COM absent: skip quietly

        string nativeLib = TryResolveNativeLibrary(baseDir);
        if (nativeLib is null)
            return; // native OPNA library not built: skip quietly

        string previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, nativeLib);
        try
        {
            string targetDir = Path.Combine(
                Path.GetTempPath(), "furnace-dump-" + Guid.NewGuid().ToString("N"));

            var result = FurnaceAssetDumpService.Dump(ovi, targetDir, sampleRate: 44100, fmpCom);
            Assert.True(result.RenderSucceeded, result.RenderError);

            string fmDir = Path.Combine(targetDir, "fm");
            Assert.True(Directory.Exists(fmDir), "expected fm output directory");
            string[] tfiFiles = Directory.GetFiles(fmDir, "*.tfi");
            Assert.NotEmpty(tfiFiles);
            foreach (string file in tfiFiles)
                Assert.Equal(42, new FileInfo(file).Length);

            Assert.True(File.Exists(Path.Combine(targetDir, "manifest.json")));

            try { Directory.Delete(targetDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, previous);
        }
    }

    private static string? TryResolveFmpCom(string dir)
    {
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "FMP.COM");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string? TryResolveNativeLibrary(string dir)
    {
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
