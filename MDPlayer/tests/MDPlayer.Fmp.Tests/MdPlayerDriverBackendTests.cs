using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MdPlayerDriverBackendTests
{
    [Fact]
    public void Probe_MdxReportsPlatformBoundaryAndMissingPdx()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-driver-{Guid.NewGuid():N}.mdx");
        try
        {
            File.WriteAllBytes(path,
            [
                (byte)'T', (byte)'e', (byte)'s', (byte)'t', (byte)'\r',
                0x1A, (byte)'D', (byte)'r', (byte)'u', (byte)'m', (byte)'s', 0x00,
                0x80, 0x10,
            ]);

            PlaybackProbeResult result = new MdPlayerDriverBackend().Probe(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]));

            Assert.False(result.Supported);
            Assert.False(result.Portable);
            Assert.Equal(PlaybackAvailability.PlatformSpecific, result.Availability);
            Assert.Contains(result.RequiredAssets, asset => asset.Name == "Drums");
            Assert.Contains("Drums", result.MissingAssets);
            Assert.Contains(result.Warnings, warning => warning.Contains("MXDRV", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RegistryDoesNotAdmitRecognizedButUnavailableDriverFormat()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-driver-{Guid.NewGuid():N}.muc");
        try
        {
            File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
            bool selected = PlaybackBackendRegistry.CreateDefault().TrySelect(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]),
                out _,
                out PlaybackProbeResult result);

            Assert.False(selected);
            Assert.Contains(result.Warnings, warning => warning.Contains("mdplayer-driver", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, warning => warning.Contains("headless driver bridge", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".nrd")]
    [InlineData(".rcp")]
    [InlineData(".rcs")]
    [InlineData(".gbs")]
    [InlineData(".sid")]
    public void ProbeReportsKnownMdPlayerFormatsAtThePlatformBoundary(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-driver-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
            PlaybackProbeResult result = new MdPlayerDriverBackend().Probe(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]));

            Assert.False(result.Supported);
            Assert.False(result.Portable);
            Assert.Equal(PlaybackAvailability.PlatformSpecific, result.Availability);
            Assert.Contains(result.Warnings, warning => warning.Contains("headless driver bridge", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
