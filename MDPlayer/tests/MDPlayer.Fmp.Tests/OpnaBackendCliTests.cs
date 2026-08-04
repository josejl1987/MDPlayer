using Fmp.Cli;
using Fmp.Core.Playback.Opna;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 CLI surface: --opna-backend must parse, validate and pass through
/// to the renderer, while the default stays the byte-identical MDSound path.
/// </summary>
public class OpnaBackendCliTests
{
    private static BatchRenderSettings Parse(params string[] args)
    {
        var settings = new BatchRenderSettings();
        var reader = new ArgumentReader(args);
        while (reader.HasMore)
        {
            if (!reader.TryReadOption(out string name, out _))
                break;
            bool handled = RenderOptionsParser.TryParse(ref reader, name, settings);
            if (!handled)
                Assert.Fail($"unexpected option {name}");
        }
        settings.ValidateCommon();
        return settings;
    }

    [Fact]
    public void Parse_DefaultBackend_IsNull_MeaningMdsound()
    {
        var settings = Parse("--loops", "2");
        Assert.Null(settings.OpnaBackend);
        Assert.False(settings.OpnaBackendExplicit);
        // Null maps to the byte-identical default path.
        Assert.Equal(FmpOpnaBackend.Mdsound, ToBackend(settings));
    }

    [Fact]
    public void Parse_OpnaBackend_Mdsound_Accepted()
    {
        var settings = Parse("--opna-backend", "mdsound");
        Assert.Equal("mdsound", settings.OpnaBackend);
        Assert.True(settings.OpnaBackendExplicit);
        Assert.Equal(FmpOpnaBackend.Mdsound, ToBackend(settings));
    }

    [Fact]
    public void Parse_OpnaBackend_NativeAudio_Accepted()
    {
        var settings = Parse("--opna-backend", "native-audio");
        Assert.Equal("native-audio", settings.OpnaBackend);
        Assert.True(settings.OpnaBackendExplicit);
        Assert.Equal(FmpOpnaBackend.NativeAudio, ToBackend(settings));
    }

    [Fact]
    public void Parse_OpnaBackend_NativeLle_IsRejected_notAliased()
    {
        var settings = new BatchRenderSettings();
        var reader = new ArgumentReader(new[] { "--opna-backend", "native-lle" });
        Assert.True(reader.TryReadOption(out string name, out _));

        ArgumentException ex = null;
        try
        {
            RenderOptionsParser.TryParse(ref reader, name, settings);
        }
        catch (ArgumentException caught)
        {
            ex = caught;
        }
        Assert.NotNull(ex);
        Assert.Equal(
            "The native-lle backend is not available. Use native-audio for native YM2608 audio rendering.",
            ex.Message);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("bogus")]
    [InlineData("NativeLle")]
    [InlineData("native")]
    [InlineData("mdsound ")]
    public void Parse_OpnaBackend_InvalidValue_Rejected(string value)
    {
        var settings = new BatchRenderSettings();
        var reader = new ArgumentReader(new[] { "--opna-backend", value });
        Assert.True(reader.TryReadOption(out string name, out _));

        ArgumentException ex = null;
        try
        {
            RenderOptionsParser.TryParse(ref reader, name, settings);
        }
        catch (ArgumentException caught)
        {
            ex = caught;
        }
        Assert.NotNull(ex);
        Assert.Contains("--opna-backend", ex.Message);
    }

    [Fact]
    public void Parse_OpnaBackend_MissingValue_Throws()
    {
        var settings = new BatchRenderSettings();
        var reader = new ArgumentReader(new[] { "--opna-backend" });
        Assert.True(reader.TryReadOption(out string name, out _));
        bool threw = false;
        try
        {
            RenderOptionsParser.TryParse(ref reader, name, settings);
        }
        catch (Exception)
        {
            threw = true;
        }
        Assert.True(threw, "expected --opna-backend with no value to throw");
    }

    [Fact]
    public void TrackRenderer_DefaultBackend_RendersByteIdentical()
    {
        string ovi = FindOviFixture();
        if (ovi == null || !File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM"))) return;

        var settings = new BatchRenderSettings
        {
            FmpCom = Path.Combine(AppContext.BaseDirectory, "FMP.COM"),
            FmpComExplicit = true,
            Loops = 2,
            Fade = 0.5,
            Tail = 0.1,
            MaxDuration = 1.0,
        };
        var prepared = TrackPreparation.Prepare(ovi, settings.FmpCom, null, new[] { Path.GetDirectoryName(ovi) });
        string outPath = Path.Combine(Path.GetTempPath(), $"cli-default-{Guid.NewGuid():N}.wav");
        try
        {
            var outcome = new TrackRenderer().Render(prepared, outPath, settings);
            Assert.True(outcome.Success, outcome.LastError);
            Assert.True(File.Exists(outPath));
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void TrackRenderer_NativeAudioBackend_PassesThroughAndRenders()
    {
        string lib = FindNativeLibrary();
        string ovi = FindOviFixture();
        if (lib == null || ovi == null || !File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM"))) return;

        string previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, lib);
        try
        {
            var settings = new BatchRenderSettings
            {
                FmpCom = Path.Combine(AppContext.BaseDirectory, "FMP.COM"),
                FmpComExplicit = true,
                Loops = 1,
                Fade = 0.5,
                Tail = 0.1,
                MaxDuration = 1.0,
                // Native YM2608-LLE replay is ~0.2x real time (documented),
                // so a 1 s window plus fade/tail can take several seconds of
                // wall time; a generous timeout is a robustness threshold, not
                // a performance assertion.
                Timeout = 90.0,
                OpnaBackend = "native-audio",
                OpnaBackendExplicit = true,
            };
            var prepared = TrackPreparation.Prepare(ovi, settings.FmpCom, null, new[] { Path.GetDirectoryName(ovi) });
            string outPath = Path.Combine(Path.GetTempPath(), $"cli-native-{Guid.NewGuid():N}.wav");
            try
            {
                var outcome = new TrackRenderer().Render(prepared, outPath, settings);
                Assert.True(outcome.Success, $"native-audio render failed: {outcome.StopReason}: {outcome.LastError}");
                Assert.True(File.Exists(outPath));
            }
            finally
            {
                if (File.Exists(outPath)) File.Delete(outPath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, previous);
        }
    }

    private static FmpOpnaBackend ToBackend(BatchRenderSettings settings) =>
        settings.OpnaBackend switch
        {
            "native-audio" => FmpOpnaBackend.NativeAudio,
            _ => FmpOpnaBackend.Mdsound,
        };

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

    private static string FindNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}