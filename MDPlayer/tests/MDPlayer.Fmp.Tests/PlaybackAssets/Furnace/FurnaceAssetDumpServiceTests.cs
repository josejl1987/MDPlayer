using Fmp.Core.PlaybackAssets.Furnace;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Furnace;

/// <summary>Guard/error-path behavior of the public dump service. The full
/// native-OPNA render path is exercised by the integration/CLI flow, not here
/// (it requires the native library and FMP.COM).</summary>
public class FurnaceAssetDumpServiceTests
{
    [Fact]
    public void MissingTargetDirectory_ReturnsError()
    {
        var result = FurnaceAssetDumpService.Dump("some.ovi", targetDirectory: null);
        Assert.False(result.RenderSucceeded);
        Assert.Contains("target directory", result.RenderError);
        Assert.Equal(0, result.ExportedCount);
    }

    [Fact]
    public void NonexistentInput_ReturnsReadError()
    {
        var result = FurnaceAssetDumpService.Dump("/no/such/file.ovi", "/tmp/out");
        Assert.False(result.RenderSucceeded);
        Assert.Contains("could not read", result.RenderError);
    }

    [Fact]
    public void EmptyTrackPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            FurnaceAssetDumpService.Dump("", "/tmp/out"));
    }

    [Fact]
    public void UnsupportedExtension_RejectedBeforeFmpRender()
    {
        // A readable, non-VGM/non-FMP input (e.g. any format the GUI can open)
        // is reported as unsupported rather than silently yielding zero FM
        // instruments.
        string dir = Path.Combine(Path.GetTempPath(), "dumprej-" + Guid.NewGuid().ToString("N"));
        try
        {
            string mmdPath = Path.Combine(dir, "song.mmd");
            Directory.CreateDirectory(dir);
            File.WriteAllText(mmdPath, "not a music file");

            var result = FurnaceAssetDumpService.Dump(mmdPath, Path.Combine(dir, "out"));
            Assert.False(result.RenderSucceeded);
            Assert.Contains("not a supported FM-music format", result.RenderError);
            Assert.Equal(0, result.ExportedCount);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
