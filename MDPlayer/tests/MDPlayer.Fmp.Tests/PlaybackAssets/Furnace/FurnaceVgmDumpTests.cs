using System.Buffers.Binary;
using System.IO.Compression;
using Fmp.Core.PlaybackAssets.Furnace;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Furnace;

/// <summary>
/// The Furnace FM-asset dump must work for VGM/VGZ, not just FMP tracks: a
/// parsed VGM supplies the ordered register writes directly, so FM instruments
/// are collected and exported without any FMP.COM dependency.
/// </summary>
public class FurnaceVgmDumpTests
{
    [Fact]
    public void Vgm_WithOpmYm2612Registers_ExportsTfi()
    {
        byte[] vgm = CreateVgm(
            0x52, 0xB0, 0x35, // YM2612 ch0 algorithm 5, feedback 6
            0x52, 0x30, 0x71, // op1 DT/MT
            0x52, 0x40, 0x0B, // op1 TL
            0x52, 0x28, 0xF0, // key-on ch0 all operators
            0x52, 0x28, 0x00, // key-off
            0x66);            // end

        string outDir = Path.Combine(Path.GetTempPath(), "vgmdump-" + Guid.NewGuid().ToString("N"));
        try
        {
            string vgmPath = Path.Combine(outDir, "song.vgm");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(vgmPath, vgm);

            var result = FurnaceAssetDumpService.Dump(vgmPath, outDir, sampleRate: 44100);

            Assert.True(result.RenderSucceeded, result.RenderError);
            Assert.Null(result.ExportWarning);
            Assert.True(result.ExportedCount >= 1, $"expected >=1 instrument, got {result.ExportedCount}");

            string fmDir = Path.Combine(outDir, "fm");
            Assert.True(Directory.Exists(fmDir));
            string[] tfi = Directory.GetFiles(fmDir, "*.tfi");
            Assert.NotEmpty(tfi);
            Assert.All(tfi, f => Assert.Equal(42, new FileInfo(f).Length));
            Assert.True(File.Exists(Path.Combine(outDir, "manifest.json")));
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Vgz_GzipCompressed_ExportsTfi()
    {
        byte[] vgm = CreateVgm(
            0x52, 0xB0, 0x35,
            0x52, 0x30, 0x71,
            0x52, 0x40, 0x0B,
            0x52, 0x28, 0xF0,
            0x52, 0x28, 0x00,
            0x66);

        string outDir = Path.Combine(Path.GetTempPath(), "vgzdump-" + Guid.NewGuid().ToString("N"));
        try
        {
            string vgzPath = Path.Combine(outDir, "song.vgz");
            Directory.CreateDirectory(outDir);
            using (var fs = File.Create(vgzPath))
            using (var gz = new GZipStream(fs, CompressionMode.Compress))
                gz.Write(vgm, 0, vgm.Length);

            var result = FurnaceAssetDumpService.Dump(vgzPath, outDir, sampleRate: 44100);

            Assert.True(result.RenderSucceeded, result.RenderError);
            Assert.True(result.ExportedCount >= 1);
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NonFmpNonVgm_Rejected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dumpreject-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A real, readable file that is neither a VGM/VGZ nor an FMP-family
            // track must be reported as unsupported.
            string mmdPath = Path.Combine(dir, "song.mmd");
            Directory.CreateDirectory(dir);
            File.WriteAllText(mmdPath, "not a music file");

            var result = FurnaceAssetDumpService.Dump(mmdPath, dir);
            Assert.False(result.RenderSucceeded);
            Assert.Contains("not a supported FM-music format", result.RenderError);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Minimal VGM header v1.50 + raw command stream (mirrors the
    /// helper used by VgmPlaybackBackendTests).</summary>
    private static byte[] CreateVgm(params byte[] commands)
    {
        byte[] data = new byte[0x40 + commands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), 3_579_545);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C, 4), 7_670_454);
        commands.CopyTo(data, 0x40);
        return data;
    }
}
