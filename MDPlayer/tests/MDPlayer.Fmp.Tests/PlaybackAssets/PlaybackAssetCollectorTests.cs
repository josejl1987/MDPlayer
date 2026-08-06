using Fmp.Core.PlaybackAssets;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets;

public class PlaybackAssetCollectorTests
{    private static ChipWriteEvent Write(
        byte address, byte data, int port = 0, long? writeIndex = null) => new()
    {
        ChipType = ChipType.Ym2608,
        ChipIndex = 0,
        Port = port,
        Address = address,
        Data = data,
        WriteIndex = writeIndex,
        PlaybackSample = null,
    };

    /// <summary>Sets a full golden FM instrument on YM2608 channel 0 via
    /// ordered register writes (algorithm 5, feedback 6).</summary>
    private static void WriteGoldenInstrument(PlaybackAssetCollector collector, long indexBase = 0)
    {
        long i = indexBase;
        // B0: feedback 6, algorithm 5
        collector.Observe(Write(0xB0, 0x35, writeIndex: i++));

        // Op1
        collector.Observe(Write(0x30, 0x71, writeIndex: i++)); // DT=7, MT=1
        collector.Observe(Write(0x40, 0x0B, writeIndex: i++)); // TL=11
        collector.Observe(Write(0x50, 0x4C, writeIndex: i++)); // RS=1, AR=12
        collector.Observe(Write(0x60, 0x0D, writeIndex: i++)); // DR=13
        collector.Observe(Write(0x70, 0x0E, writeIndex: i++)); // SR=14
        collector.Observe(Write(0x80, 0x32, writeIndex: i++)); // SL=3, RR=2
        collector.Observe(Write(0x90, 0x04, writeIndex: i++)); // SSG=4

        // Op2
        collector.Observe(Write(0x34, 0x62, writeIndex: i++)); // DT=6, MT=2
        collector.Observe(Write(0x44, 0x15, writeIndex: i++)); // TL=21
        collector.Observe(Write(0x54, 0x96, writeIndex: i++)); // RS=2, AR=22
        collector.Observe(Write(0x64, 0x17, writeIndex: i++)); // DR=23
        collector.Observe(Write(0x74, 0x18, writeIndex: i++)); // SR=24
        collector.Observe(Write(0x84, 0x43, writeIndex: i++)); // SL=4, RR=3
        collector.Observe(Write(0x94, 0x05, writeIndex: i++)); // SSG=5

        // Op3
        collector.Observe(Write(0x38, 0x53, writeIndex: i++)); // DT=5, MT=3
        collector.Observe(Write(0x48, 0x1F, writeIndex: i++)); // TL=31
        collector.Observe(Write(0x58, 0xD9, writeIndex: i++)); // RS=3, AR=25
        collector.Observe(Write(0x68, 0x1A, writeIndex: i++)); // DR=26
        collector.Observe(Write(0x78, 0x1B, writeIndex: i++)); // SR=27
        collector.Observe(Write(0x88, 0x54, writeIndex: i++)); // SL=5, RR=4
        collector.Observe(Write(0x98, 0x06, writeIndex: i++)); // SSG=6

        // Op4
        collector.Observe(Write(0x3C, 0x34, writeIndex: i++)); // DT=3, MT=4
        collector.Observe(Write(0x4C, 0x29, writeIndex: i++)); // TL=41
        collector.Observe(Write(0x5C, 0x1C, writeIndex: i++)); // RS=0, AR=28
        collector.Observe(Write(0x6C, 0x1D, writeIndex: i++)); // DR=29
        collector.Observe(Write(0x7C, 0x1E, writeIndex: i++)); // SR=30
        collector.Observe(Write(0x8C, 0x65, writeIndex: i++)); // SL=6, RR=5
        collector.Observe(Write(0x9C, 0x07, writeIndex: i++)); // SSG=7
    }

    private static byte KeyOn(int channel, byte mask = 0x0F) =>
        (byte)(((mask & 0x0F) << 4) | (channel & 0x07));

    // --- Test 9: repeated identical key-on deduplicates ---
    [Fact]
    public void RepeatedKeyOn_SamePatch_Deduplicates()
    {
        var collector = new PlaybackAssetCollector();
        WriteGoldenInstrument(collector);
        for (int i = 0; i < 10; i++)
            collector.Observe(Write(0x28, KeyOn(0), writeIndex: 100 + i));

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(10, snapshot.Instruments[0].ObservationCount);
    }

    // --- Test 10: changed patch creates a new asset ---
    [Fact]
    public void ChangedPatch_CreatesSecondAsset()
    {
        var collector = new PlaybackAssetCollector();
        WriteGoldenInstrument(collector);
        collector.Observe(Write(0x28, KeyOn(0)));

        // Change Op2 total level (0x44) mid-sequence, then key-on again.
        collector.Observe(Write(0x44, 0x7F));
        collector.Observe(Write(0x28, KeyOn(0)));

        var snapshot = collector.Complete();
        Assert.Equal(2, snapshot.Instruments.Count);
        Assert.NotEqual(snapshot.Instruments[0].TfiBytes, snapshot.Instruments[1].TfiBytes);
    }

    // --- Test 11: irrelevant changes do not create a new asset ---
    [Fact]
    public void IrrelevantFieldChanges_DoNotCreateNewAsset()
    {
        var collector = new PlaybackAssetCollector();
        WriteGoldenInstrument(collector);
        collector.Observe(Write(0x28, KeyOn(0)));

        // Frequency registers (A4-A6), pan/AMS/FMS (B4), are not TFI fields.
        collector.Observe(Write(0xA4, 0x40));
        collector.Observe(Write(0xA5, 0x02));
        collector.Observe(Write(0xA6, 0x00));
        collector.Observe(Write(0xB4, 0xC0));
        collector.Observe(Write(0x28, KeyOn(0)));

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(2, snapshot.Instruments[0].ObservationCount);
    }

    // --- Test 12: Yamaha zero detune canonicalization (0 vs 4) ---
    [Fact]
    public void YamahaDetuneZero_And_Four_Deduplicate()
    {
        var collector = new PlaybackAssetCollector();
        WriteGoldenInstrument(collector);
        collector.Observe(Write(0x30, 0x00)); // Op1 detune register 0
        collector.Observe(Write(0x28, KeyOn(0)));

        WriteGoldenInstrument(collector);
        collector.Observe(Write(0x30, 0x40)); // Op1 detune register 4 (-0)
        collector.Observe(Write(0x28, KeyOn(0)));

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(2, snapshot.Instruments[0].ObservationCount);
    }

    // --- §25 synthetic write-sequence integration test ---
    [Fact]
    public void SyntheticWriteSequence_CapturesAndReacts()
    {
        var collector = new PlaybackAssetCollector();
        WriteGoldenInstrument(collector);
        collector.Observe(Write(0x28, KeyOn(0), writeIndex: 50));

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);
        // TfiBytes is the canonicalized export: carriers Op2/Op3/Op4
        // (TL 21/31/41) normalize to 0/10/20; modulators unchanged.
        Assert.Equal(CanonicalGoldenBytes, snapshot.Instruments[0].TfiBytes);
        Assert.Contains(ChipType.Ym2608, snapshot.Instruments[0].ChipTypesObserved);
        Assert.Contains(0, snapshot.Instruments[0].ChipInstancesObserved);
        Assert.Contains(0, snapshot.Instruments[0].ChannelsObserved);
        Assert.Equal(1, snapshot.Instruments[0].ObservationCount);
        Assert.Equal(21, snapshot.Instruments[0].MinimumObservedVolumeOffset);
        Assert.Equal(21, snapshot.Instruments[0].MaximumObservedVolumeOffset);

        // Repeat only key-on: still one file, observation count becomes 2.
        collector.Observe(Write(0x28, KeyOn(0), writeIndex: 51));
        var snapshot2 = collector.Complete();
        Assert.Single(snapshot2.Instruments);
        Assert.Equal(2, snapshot2.Instruments[0].ObservationCount);

        // Change 0x44 (logical operator 2 total level), key on again.
        collector.Observe(Write(0x44, 0x7F, writeIndex: 52));
        collector.Observe(Write(0x28, KeyOn(0), writeIndex: 53));
        var snapshot3 = collector.Complete();
        // Op2 is a carrier for algorithm 5, so changing its TL changes the
        // carrier balance -> a distinct instrument.
        Assert.Equal(2, snapshot3.Instruments.Count);

        // Second patch carriers are Op2=127, Op3=31, Op4=41; normalized to
        // Op2=96, Op3=0, Op4=10. First was 0/10/20. So the two files differ
        // in the carrier TL bytes (Op3 TL, Op2 TL, Op4 TL) and nothing else.
        byte[] first = snapshot3.Instruments[0].TfiBytes;
        byte[] second = snapshot3.Instruments[1].TfiBytes;
        for (int i = 0; i < 42; i++)
        {
            if (i is 0x0E or 0x18 or 0x22)
                Assert.NotEqual(first[i], second[i]);
            else
                Assert.Equal(first[i], second[i]);
        }
        Assert.Equal(96, second[0x18]); // Op2 canonical TL = 127 - 31
        Assert.Equal(10, second[0x22]); // Op4 canonical TL = 41 - 31
    }

    private static byte[] CanonicalGoldenBytes => new byte[]
    {
        5, 6,
        1, 0, 11, 1, 12, 13, 14, 2, 3, 4,   // Op1 (modulator) unchanged
        3, 2, 10, 3, 25, 26, 27, 4, 5, 6,   // Op3 TL = 31 - 21
        2, 1, 0, 2, 22, 23, 24, 3, 4, 5,    // Op2 TL = 21 - 21
        4, 6, 20, 0, 28, 29, 30, 5, 6, 7    // Op4 TL = 41 - 21
    };

    // --- Non-OPN chips are ignored ---
    [Fact]
    public void NonOpnChip_IsIgnored()
    {
        var collector = new PlaybackAssetCollector();
        collector.Observe(new ChipWriteEvent
        {
            ChipType = ChipType.Sn76489,
            ChipIndex = 0,
            Port = 0,
            Address = 0,
            Data = 0,
        });

        Assert.Empty(collector.Complete().Instruments);
    }
}
