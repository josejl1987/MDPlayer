using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 backend selection: the factory must construct exactly the
/// requested <see cref="FmpOpnaBackend"/> session and never fall back. The
/// default render path must remain byte-identical and must not touch the
/// native library.
/// </summary>
public class BackendSelectionTests
{
    private static FmpPlaybackContext NewContext() => new(
        TrackData: Array.Empty<byte>(),
        TrackFileName: "track.ovi",
        Assets: new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
        FileSystem: new FmpFileSystem(new[] { AppContext.BaseDirectory }),
        SampleRate: 44100,
        SsgGainDb: 0,
        LoopCount: 2,
        FadeSeconds: 5.0,
        TailSeconds: 0.5,
        MaxDurationSeconds: 5.0);

    [Fact]
    public void Factory_Mdsound_ReturnsLegacySession()
    {
        using var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.Mdsound, NewContext());
        Assert.Contains("LegacyMdsoundFmpPcmSession", session.GetType().Name);
        Assert.Equal(44100, session.OutputSampleRate);
    }

    [Fact]
    public void Factory_NativeAudio_ReturnsNativeSession()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.NativeAudio, NewContext());
        Assert.Contains("NativeAudioFmpPcmSession", session.GetType().Name);
        Assert.Equal(44100, session.OutputSampleRate);
    }

    [Fact]
    public void DefaultOptions_Backend_IsMdsound()
    {
        // The default (and therefore the default CLI / serialized render) must
        // keep selecting the byte-identical MDSound path.
        var options = new FmpRenderer.Options();
        Assert.Equal(FmpOpnaBackend.Mdsound, options.OpnaBackend);
    }

    [Fact]
    public void Enum_SerializationDefaults_AreStable()
    {
        // The default serialized value must remain 0 (Mdsound) so old configs
        // keep rendering identically.
        Assert.Equal(0, (int)FmpOpnaBackend.Mdsound);
        Assert.Equal(1, (int)FmpOpnaBackend.NativeAudio);
        Assert.Equal(2, Enum.GetNames<FmpOpnaBackend>().Length);
    }

    [Fact]
    public void NoFallback_NativeLibUnavailable_NativeConstructionThrows()
    {
        // Point the native library at a path that cannot exist: constructing
        // the native session must fail loudly — never fall back to MDSound.
        string bogus = Path.Combine(Path.GetTempPath(), "missing-lib-" + Guid.NewGuid().ToString("N"), OpnaNativeSession.NativeLibraryFileName);
        using var restore = SetNativeLibrary(bogus);

        Assert.ThrowsAny<Exception>(() =>
        {
            using var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.NativeAudio, NewContext());
        });
    }

    [Fact]
    public void NoFallback_BrokenNativeLib_LegacyConstructionSucceeds()
    {
        string bogus = Path.Combine(Path.GetTempPath(), "missing-lib-" + Guid.NewGuid().ToString("N"), OpnaNativeSession.NativeLibraryFileName);
        using var restore = SetNativeLibrary(bogus);

        using var session = FmpPcmSessionFactory.Create(FmpOpnaBackend.Mdsound, NewContext());
        Assert.Contains("LegacyMdsoundFmpPcmSession", session.GetType().Name);
    }

    [Fact]
    public void NoFallback_DefaultRender_IgnoresBrokenNativeLib()
    {
        string ovi = FindOviFixture();
        if (ovi == null) return;

        string bogus = Path.Combine(Path.GetTempPath(), "missing-lib-" + Guid.NewGuid().ToString("N"), OpnaNativeSession.NativeLibraryFileName);

        byte[] withBrokenLib;
        using (var restore = SetNativeLibrary(bogus))
        {
            withBrokenLib = RenderDefaultToBytes(ovi);
        }
        byte[] withGoodLib = RenderDefaultToBytes(ovi);

        // The default path must produce identical bytes whether or not the
        // native library is loadable.
        using var sha = SHA256.Create();
        Assert.Equal(Convert.ToHexString(sha.ComputeHash(withBrokenLib)),
                     Convert.ToHexString(sha.ComputeHash(withGoodLib)));
    }

    [Fact]
    public void Render_InvalidBackendValue_Rejected()
    {
        string ovi = FindOviFixture();
        if (ovi == null) return;

        var assets = new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM"));
        var fileSystem = new FmpFileSystem(new[] { Path.GetDirectoryName(ovi) });
        var renderer = new FmpRenderer(assets, fileSystem, 44100);
        string outPath = Path.Combine(Path.GetTempPath(), $"invalid-backend-{Guid.NewGuid():N}.wav");
        var opts = new FmpRenderer.Options
        {
            LoopCount = 2,
            FadeSeconds = 0.5,
            TailSeconds = 0.1,
            MaxDurationSeconds = 1.0,
            OpnaBackend = (FmpOpnaBackend)99,
        };

        var result = renderer.RenderToWav(File.ReadAllBytes(ovi), Path.GetFileName(ovi), outPath, opts);

        Assert.False(result.Success);
        Assert.Equal("invalid_options", result.StopReason);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Render_NativeBackend_WithTrace_IsUnsupported()
    {
        var assets = new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM"));
        var fileSystem = new FmpFileSystem(new[] { AppContext.BaseDirectory });
        var renderer = new FmpRenderer(assets, fileSystem, 44100);
        string outPath = Path.Combine(Path.GetTempPath(), $"native-trace-{Guid.NewGuid():N}.wav");
        var opts = new FmpRenderer.Options
        {
            LoopCount = 2,
            FadeSeconds = 0.5,
            TailSeconds = 0.1,
            MaxDurationSeconds = 1.0,
            TracePath = Path.Combine(Path.GetTempPath(), "native-trace.jsonl"),
            OpnaBackend = FmpOpnaBackend.NativeAudio,
        };

        var result = renderer.RenderToWav(Array.Empty<byte>(), "x.ovi", outPath, opts);

        Assert.False(result.Success);
        Assert.Equal("trace_error", result.StopReason);
        Assert.False(File.Exists(outPath));
    }

    // ---------------------------------------------------------------------
    // Fixture / helpers
    // ---------------------------------------------------------------------

    private static string FmpComPath => Path.Combine(AppContext.BaseDirectory, "FMP.COM");

    private static string FindOviFixture()
    {
        var testDir = AppContext.BaseDirectory;
        string first = Directory.GetFiles(testDir, "*.ovi", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(testDir, "*.OVI", SearchOption.AllDirectories))
            .FirstOrDefault();
        if (first != null) return first;

        string downloads = "/home/jose/Downloads";
        if (OperatingSystem.IsLinux() && Directory.Exists(downloads))
        {
            var dl = Directory.GetFiles(downloads, "*.OVI", SearchOption.TopDirectoryOnly);
            if (dl.Length > 0)
                return dl.OrderBy(f => f, StringComparer.Ordinal).First();
        }
        return null;
    }

    private byte[] RenderDefaultToBytes(string oviPath)
    {
        var assets = new FmpRuntimeAssets(FmpComPath);
        var fileSystem = new FmpFileSystem(new[] { Path.GetDirectoryName(oviPath) });
        var renderer = new FmpRenderer(assets, fileSystem, 44100);
        string outPath = Path.Combine(Path.GetTempPath(), $"default-{Guid.NewGuid():N}.wav");
        var opts = new FmpRenderer.Options
        {
            LoopCount = 2,
            FadeSeconds = 0.5,
            TailSeconds = 0.1,
            MaxDurationSeconds = 1.0,
        };
        var result = renderer.RenderToWav(File.ReadAllBytes(oviPath), Path.GetFileName(oviPath), outPath, opts);
        Assert.True(result.Success, result.LastError);
        try
        {
            return File.ReadAllBytes(outPath);
        }
        finally
        {
            File.Delete(outPath);
        }
    }

    private static string RequireNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Xunit.Sdk.XunitException("Native OPNA library not built.");
    }

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, path);
        return new RestoreEnv(OpnaNativeSession.NativeLibraryEnvVar, previous);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public RestoreEnv(string name, string previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}