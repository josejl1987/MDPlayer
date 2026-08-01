using Fmp.Core.Audio;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Tests for MaskedChipSink register-level channel muting.
/// Uses a recording inner sink to verify which writes are passed through,
/// which are modified, and which are suppressed.
/// </summary>
public class ChannelMaskTests
{
    /// <summary>
    /// A recording IFmpChipSink that captures writes for inspection.
    /// </summary>
    private class RecordingSink : IFmpChipSink
    {
        public List<(int chipId, int port, int address, int value, long sample)> Ym2608Writes = new();
        public List<(int bank, int mode, int entryCount, long sample)> Ppz8Loads = new();
        public List<(int port, int address, int value, long sample)> Ppz8Writes = new();

        public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
            => Ym2608Writes.Add((chipId, port, address, value, samplePosition));

        public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
            => Ppz8Loads.Add((bank, mode, samples?.Length ?? 0, samplePosition));

        public void WritePpz8(int port, int address, int value, long samplePosition)
            => Ppz8Writes.Add((port, address, value, samplePosition));
    }

    [Fact]
    public void Unmasked_AllChannels_PassesThrough()
    {
        var inner = new RecordingSink();
        var mask = new MaskedChipSink(inner, ChannelGroup.All);

        mask.WriteYm2608(0, 0, 0x40, 0x20, 0);
        mask.WriteYm2608(0, 0, 0x28, 0x80, 0);
        mask.WriteYm2608(0, 0, 0x08, 0x0F, 0);

        Assert.Equal(3, inner.Ym2608Writes.Count);
        // TL values unchanged
        Assert.Equal(0x20, inner.Ym2608Writes[0].value);
        // Key-on unchanged
        Assert.Equal(0x80, inner.Ym2608Writes[1].value);
        // SSG amplitude unchanged
        Assert.Equal(0x0F, inner.Ym2608Writes[2].value);
    }

    [Fact]
    public void FmChannel_TL_MutedToMaxAttenuation()
    {
        var inner = new RecordingSink();
        // Only FM1 active
        var mask = new MaskedChipSink(inner, ChannelGroup.Fm1);

        // Write TL for FM1 (port 0, address 0x40 = channel 0, operator 0 TL)
        mask.WriteYm2608(0, 0, 0x40, 0x10, 0);
        // Write TL for FM2 (port 0, address 0x41 = channel 1, operator 0 TL)
        mask.WriteYm2608(0, 0, 0x41, 0x10, 0);

        Assert.Equal(2, inner.Ym2608Writes.Count);
        // FM1 (active) — TL unchanged
        Assert.Equal(0x10, inner.Ym2608Writes[0].value);
        // FM2 (muted) — TL OR'd with 0x7F (max attenuation)
        Assert.Equal(0x10 | 0x7F, inner.Ym2608Writes[1].value);
    }

    [Fact]
    public void FmChannel_KeyOn_SuppressedForMutedChannel()
    {
        var inner = new RecordingSink();
        // Only FM1 active
        var mask = new MaskedChipSink(inner, ChannelGroup.Fm1);

        // Key-on FM1 (port 0, address 0x28, value 0x80 | 0 = channel 0)
        mask.WriteYm2608(0, 0, 0x28, 0x80 | 0, 0);
        // Key-on FM2 (port 0, address 0x28, value 0x80 | 1 = channel 1)
        mask.WriteYm2608(0, 0, 0x28, 0x80 | 1, 0);

        Assert.Equal(2, inner.Ym2608Writes.Count);
        // FM1 key-on — passed through
        Assert.Equal(0x80, inner.Ym2608Writes[0].value);
        // FM2 key-on — bit 7 cleared (key-off), channel preserved
        Assert.Equal(0x01, inner.Ym2608Writes[1].value);
    }

    [Fact]
    public void KeyOff_AlwaysPassesThrough()
    {
        var inner = new RecordingSink();
        // Only FM1 active
        var mask = new MaskedChipSink(inner, ChannelGroup.Fm1);

        // Key-off on muted FM2 — should still pass through (key-off is essential for proper chip state)
        mask.WriteYm2608(0, 0, 0x28, 0x00 | 1, 0);

        Assert.Equal(1, inner.Ym2608Writes.Count);
        // Key-off value has bit 7 = 0 already, so clearing bit 7 is a no-op
        Assert.Equal(0x01, inner.Ym2608Writes[0].value);
    }

    [Fact]
    public void SsgAmplitude_MutedChannel_Zeroed()
    {
        var inner = new RecordingSink();
        // Only SSG1 active
        var mask = new MaskedChipSink(inner, ChannelGroup.Ssg1);

        // SSG1 amplitude (port 0, address 0x08)
        mask.WriteYm2608(0, 0, 0x08, 0x0F, 0);
        // SSG2 amplitude (port 0, address 0x09)
        mask.WriteYm2608(0, 0, 0x09, 0x0F, 0);

        Assert.Equal(2, inner.Ym2608Writes.Count);
        // SSG1 (active) — unchanged
        Assert.Equal(0x0F, inner.Ym2608Writes[0].value);
        // SSG2 (muted) — lower 5 bits zeroed (fixed mode, volume 0 = silence)
        Assert.Equal(0x00, inner.Ym2608Writes[1].value);
    }

    [Fact]
    public void Port1_FmChannels_MappedCorrectly()
    {
        var inner = new RecordingSink();
        // Only FM4 active (port 1, channel 3)
        var mask = new MaskedChipSink(inner, ChannelGroup.Fm4);

        // Write TL for FM4 (port 1, address 0x40 = channel 3, operator 0 TL)
        mask.WriteYm2608(0, 1, 0x40, 0x20, 0);
        // Write TL for FM5 (port 1, address 0x41 = channel 4, operator 0 TL)
        mask.WriteYm2608(0, 1, 0x41, 0x20, 0);

        Assert.Equal(2, inner.Ym2608Writes.Count);
        // FM4 (active) — unchanged
        Assert.Equal(0x20, inner.Ym2608Writes[0].value);
        // FM5 (muted) — TL maxed
        Assert.Equal(0x20 | 0x7F, inner.Ym2608Writes[1].value);
    }

    [Fact]
    public void RhythmGroup_PassesThroughUnchanged()
    {
        var inner = new RecordingSink();
        // Only Rhythm active
        var mask = new MaskedChipSink(inner, ChannelGroup.Rhythm);

        // Rhythm writes are handled by group volume (via MDSound SetVolume),
        // not register interception. They should pass through unchanged.
        mask.WriteYm2608(0, 0, 0x10, 0x01, 0); // Rhythm key-on (load + trigger)

        Assert.Equal(1, inner.Ym2608Writes.Count);
        Assert.Equal(0x01, inner.Ym2608Writes[0].value);
    }

    [Fact]
    public void Ppz8_PassesThroughUnchanged()
    {
        var inner = new RecordingSink();
        var mask = new MaskedChipSink(inner, ChannelGroup.Ppz8);

        byte[] bank = new byte[256];
        mask.LoadPpz8Bank(0, 0, new[] { new ReadOnlyMemory<byte>(bank) }, 0);
        mask.WritePpz8(0, 0, 0x80, 0);

        Assert.Equal(1, inner.Ppz8Loads.Count);
        Assert.Equal(1, inner.Ppz8Writes.Count);
        Assert.Equal(0x80, inner.Ppz8Writes[0].value);
    }

    [Fact]
    public void ChannelGroup_Flags_CoverAllStems()
    {
        // Verify every stem in DefaultStems has a unique non-zero flag
        // and that each non-master stem uses exactly one flag.
        foreach (var stem in DefaultStems.All)
        {
            Assert.NotEqual(ChannelGroup.None, stem.Channels);
            Assert.False(string.IsNullOrEmpty(stem.Name));
            Assert.False(string.IsNullOrEmpty(stem.Label));

            // Non-master stems must use exactly one flag (single channel or group)
            if (stem.Name != "master")
            {
                // Count set bits — must be exactly 1
                long bits = (long)stem.Channels;
                Assert.True(bits > 0 && (bits & (bits - 1)) == 0,
                    $"{stem.Name} uses multiple flags (0x{bits:X}) — expected a single channel");
            }
        }

        // Verify master includes everything
        Assert.Equal(ChannelGroup.All, DefaultStems.All[0].Channels);

        // Verify all FM channels covered
        var allFm = ChannelGroup.None;
        foreach (var s in DefaultStems.All)
        {
            if (s.Name.StartsWith("ym2608-fm"))
                allFm |= s.Channels;
        }
        Assert.Equal(ChannelGroup.AllFm, allFm);

        // Verify all SSG channels covered
        var allSsg = ChannelGroup.None;
        foreach (var s in DefaultStems.All)
        {
            if (s.Name.StartsWith("ym2608-ssg"))
                allSsg |= s.Channels;
        }
        Assert.Equal(ChannelGroup.AllSsg, allSsg);
    }
}
