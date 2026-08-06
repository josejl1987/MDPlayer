using System.Security.Cryptography;
using Fmp.Core.PlaybackAssets;
using Fmp.Core.PlaybackAssets.Furnace;
using Fmp.Core.PlaybackAssets.Opn;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Furnace;

public class FurnaceAssetExporterTests
{
    private static OpnFmInstrument GoldenInstrument() => new()
    {
        Algorithm = 5,
        Feedback = 6,
        Op1 = Op(1, 7, 11, 1, 12, 13, 14, 2, 3, 4),
        Op2 = Op(2, 6, 21, 2, 22, 23, 24, 3, 4, 5),
        Op3 = Op(3, 5, 31, 3, 25, 26, 27, 4, 5, 6),
        Op4 = Op(4, 3, 41, 0, 28, 29, 30, 5, 6, 7),
    };

    private static OpnFmOperator Op(params byte[] v) => new()
    {
        Multiplier = v[0],
        DetuneRegister = v[1],
        TotalLevel = v[2],
        RateScaling = v[3],
        AttackRate = v[4],
        DecayRate = v[5],
        SustainRate = v[6],
        ReleaseRate = v[7],
        SustainLevel = v[8],
        SsgEg = v[9],
    };

    private static PlaybackAssetSnapshot SnapshotFrom(PlaybackAssetCollector collector) =>
        collector.Complete();

    private static byte[] GoldenBytes => TfiInstrumentWriter.Write(GoldenInstrument());

    /// <summary>The canonicalized export bytes for the golden instrument
    /// (algorithm 5: carriers Op2/Op3/Op4 with TL 21/31/41, normalized so the
    /// loudest carrier Op2 is at TL=0, i.e. 0/10/20; modulators unchanged).</summary>
    private static byte[] CanonicalGoldenBytes =>
        TfiInstrumentWriter.Write(OpnFmAlgorithm.Canonicalize(GoldenInstrument()));

    [Fact]
    public void Export_Writes_42ByteTfi_AndManifest()
    {
        var collector = new PlaybackAssetCollector();

        // Build a golden instrument through the collector (write the register
        // sequence, then key-on once). Only need op bytes that become the
        // canonicalized file.
        WriteGolden(collector);
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Ym2608,
            ChipIndex = 0,
            Port = 0,
            Address = 0x28,
            Data = 0xF0,
            WriteIndex = 7,
        });

        string dir = Path.Combine(Path.GetTempPath(), "tfi-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = FurnaceAssetExporter.Export(SnapshotFrom(collector), dir);
            Assert.False(result.Failed);

            var file = Directory.GetFiles(Path.Combine(dir, "fm"), "*.tfi").Single();
            Assert.Equal(42, new FileInfo(file).Length);
            Assert.Equal(CanonicalGoldenBytes, File.ReadAllBytes(file));

            string manifestPath = Path.Combine(dir, "manifest.json");
            Assert.True(File.Exists(manifestPath));
            string json = File.ReadAllText(manifestPath);
            Assert.Contains("\"format\": \"TFI\"", json);
            Assert.Contains("\"size\": 42", json);
            Assert.Contains("\"firstSeenPlaybackSample\": null", json);
            Assert.Contains("\"firstSeenWriteIndex\": 7", json);
            Assert.Contains("\"chipTypesObserved\": [", json);
            Assert.Contains("\"Ym2608\"", json);
            Assert.Contains("\"channelsObserved\": [", json);
            Assert.Contains("fm_001_", json);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Export_Filename_UsesFirstSeenOrdinalAndHashPrefix()
    {
        var collector = new PlaybackAssetCollector();
        WriteGolden(collector);
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Ym2608,
            ChipIndex = 0,
            Port = 0,
            Address = 0x28,
            Data = 0xF0,
        });

        string dir = Path.Combine(Path.GetTempPath(), "tfi-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = FurnaceAssetExporter.Export(SnapshotFrom(collector), dir);
            string hash = Convert.ToHexString(SHA256.HashData(CanonicalGoldenBytes)).ToLowerInvariant()[..8];
            string fileName = $"fm_001_{hash}.tfi";
            Assert.True(File.Exists(Path.Combine(dir, "fm", fileName)));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Export_DeduplicatedAssets_ProduceDistinctFiles()
    {
        var collector = new PlaybackAssetCollector();
        WriteGolden(collector);
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Ym2608, ChipIndex = 0, Port = 0, Address = 0x28, Data = 0xF0,
        });
        // change Op2 TL and key on -> second asset
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Ym2608, ChipIndex = 0, Port = 0, Address = 0x44, Data = 0x7F,
        });
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Ym2608, ChipIndex = 0, Port = 0, Address = 0x28, Data = 0xF0,
        });

        string dir = Path.Combine(Path.GetTempPath(), "tfi-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = FurnaceAssetExporter.Export(SnapshotFrom(collector), dir);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "fm"), "*.tfi").Length);
            Assert.Equal(2, result.ExpandedCount);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void WriteGolden(PlaybackAssetCollector collector)
    {
        // Build golden instrumentation via register writes (see collector tests
        // for the full sequence; here we only need the same 42-byte result).
        UpdateOp(collector, 0x30, 0x71, 0x40, 0x0B, 0x50, 0x4C, 0x60, 0x0D, 0x70, 0x0E, 0x80, 0x32, 0x90, 0x04); // Op1
        UpdateOp(collector, 0x34, 0x62, 0x44, 0x15, 0x54, 0x96, 0x64, 0x17, 0x74, 0x18, 0x84, 0x43, 0x94, 0x05); // Op2
        UpdateOp(collector, 0x38, 0x53, 0x48, 0x1F, 0x58, 0xD9, 0x68, 0x1A, 0x78, 0x1B, 0x88, 0x54, 0x98, 0x06); // Op3
        UpdateOp(collector, 0x3C, 0x34, 0x4C, 0x29, 0x5C, 0x1C, 0x6C, 0x1D, 0x7C, 0x1E, 0x8C, 0x65, 0x9C, 0x07); // Op4
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Ym2608, ChipIndex = 0, Port = 0, Address = 0xB0, Data = 0x35,
        });
    }

    private static void UpdateOp(PlaybackAssetCollector c, params byte[] pairs)
    {
        for (int i = 0; i < pairs.Length; i += 2)
        {
            c.Observe(new ChipWriteEvent
            {
                ChipType = ChipType.Ym2608, ChipIndex = 0, Port = 0,
                Address = pairs[i], Data = pairs[i + 1],
            });
        }
    }
}
