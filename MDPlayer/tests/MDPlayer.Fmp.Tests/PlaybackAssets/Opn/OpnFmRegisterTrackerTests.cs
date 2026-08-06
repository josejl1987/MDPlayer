using Fmp.Core.PlaybackAssets.Furnace;
using Fmp.Core.PlaybackAssets.Opn;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Opn;

public class OpnFmRegisterTrackerTests
{
    private static OpnChipCapabilities Cap6 =>
        new(FmChannelCount: 6, RegisterPortCount: 2, SupportsSsgEg: true);

    private static OpnChipCapabilities Cap3 =>
        new(FmChannelCount: 3, RegisterPortCount: 1, SupportsSsgEg: true);

    /// <summary>Builds the 0x28 key-on data byte for a channel and operator mask.</summary>
    private static byte KeyOn(byte channel, byte mask = 0x0F) =>
        (byte)(((mask & 0x0F) << 4) | (channel & 0x07));

    // --- Test 5: register operator decoding and TFI order ---
    [Fact]
    public void OperatorAddresses_DecodeToLogicalOperators_AndTfiOrder()
    {
        var tracker = new OpnFmRegisterTracker(Cap6);

        // Distinct multipliers through the operator slot addresses.
        tracker.HandleWrite(0, 0x30, 0x01); // Op1
        tracker.HandleWrite(0, 0x34, 0x02); // Op2
        tracker.HandleWrite(0, 0x38, 0x03); // Op3
        tracker.HandleWrite(0, 0x3C, 0x04); // Op4
        tracker.HandleWrite(0, 0xB0, 3);    // algorithm 3, feedback 0

        OpnFmInstrument? snapshot = tracker.HandleWrite(0, 0x28, KeyOn(0));

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.Op1.Multiplier);
        Assert.Equal(2, snapshot.Op2.Multiplier);
        Assert.Equal(3, snapshot.Op3.Multiplier);
        Assert.Equal(4, snapshot.Op4.Multiplier);
        Assert.Equal(3, snapshot.Algorithm);

        // TFI serialization reorders to Op1, Op3, Op2, Op4.
        byte[] tfi = TfiInstrumentWriter.Write(snapshot);
        Assert.Equal(1, tfi[0x02]); // Op1
        Assert.Equal(3, tfi[0x0C]); // Op3
        Assert.Equal(2, tfi[0x16]); // Op2
        Assert.Equal(4, tfi[0x20]); // Op4
    }

    // --- Test 6: port/channel decoding for a six-channel chip ---
    [Fact]
    public void PortChannelDecoding_IsIsolatedPerChannel_Ym2608()
    {
        var tracker = new OpnFmRegisterTracker(Cap6);

        // port 0, B0 -> channel 0 ; port 0, B2 -> channel 2
        tracker.HandleWrite(0, 0xB0, 0); // ch0 alg 0
        tracker.HandleWrite(0, 0xB2, 2); // ch2 alg 2
        // port 1, B0 -> channel 3 ; port 1, B2 -> channel 5
        tracker.HandleWrite(1, 0xB0, 3); // ch3 alg 3
        tracker.HandleWrite(1, 0xB2, 5); // ch5 alg 5

        OpnFmInstrument? s0 = tracker.HandleWrite(0, 0x28, KeyOn(0));
        OpnFmInstrument? s2 = tracker.HandleWrite(0, 0x28, KeyOn(2));
        OpnFmInstrument? s3 = tracker.HandleWrite(0, 0x28, KeyOn(4)); // key code 4 = ch3
        OpnFmInstrument? s5 = tracker.HandleWrite(0, 0x28, KeyOn(6)); // key code 6 = ch5

        Assert.Equal(0, s0.Algorithm);
        Assert.Equal(2, s2.Algorithm);
        Assert.Equal(3, s3.Algorithm);
        Assert.Equal(5, s5.Algorithm);

        // Port 1 state must not leak into port 0 channels: ch0/2 stay as set.
        tracker.HandleWrite(0, 0xB0, 0);
        OpnFmInstrument? s0Again = tracker.HandleWrite(0, 0x28, KeyOn(0));
        Assert.Equal(0, s0Again.Algorithm);
    }

    // --- Test 7: key-on channel decoding (valid + reserved) ---
    [Fact]
    public void KeyOnChannelDecoding_MapsAndIgnoresReserved()
    {
        var tracker = new OpnFmRegisterTracker(Cap6);

        // Give each channel a distinct algorithm; the returned snapshot for a
        // key-on must reflect the channel selected by the low three bits.
        for (int ch = 0; ch < 6; ch++)
        {
            int port = ch < 3 ? 0 : 1;
            int addr = ch < 3 ? 0xB0 + ch : 0xB0 + (ch - 3);
            tracker.HandleWrite(port, addr, (byte)ch);
        }

        int[] keyCodes = { 0, 1, 2, 4, 5, 6 };
        for (int i = 0; i < keyCodes.Length; i++)
        {
            OpnFmInstrument? s = tracker.HandleWrite(0, 0x28, KeyOn((byte)keyCodes[i]));
            Assert.Equal(i, s.Algorithm);
        }

        // 3 and 7 are reserved/invalid: no capture.
        Assert.Null(tracker.HandleWrite(0, 0x28, KeyOn(3)));
        Assert.Null(tracker.HandleWrite(0, 0x28, KeyOn(7)));
    }

    // --- Test 8: key-off does not capture ---
    [Fact]
    public void KeyOff_DoesNotCapture()
    {
        var tracker = new OpnFmRegisterTracker(Cap6);
        tracker.HandleWrite(0, 0xB0, 4);

        // Upper nibble 0 -> pure key-off.
        Assert.Null(tracker.HandleWrite(0, 0x28, (byte)(0x00 | 0)));
        Assert.Null(tracker.HandleWrite(0, 0x28, (byte)(0x00 | 2)));
    }

    // --- Three-channel chip: port 1 must not create channels 3..5 ---
    [Fact]
    public void ThreeChannelChip_IgnoresPortOneFmChannels()
    {
        var tracker = new OpnFmRegisterTracker(Cap3);

        // Writing port 1 channel state must not affect any of the 3 channels.
        tracker.HandleWrite(1, 0xB0, 7);
        tracker.HandleWrite(1, 0x30, 0x03);

        OpnFmInstrument? s0 = tracker.HandleWrite(0, 0x28, KeyOn(0));
        Assert.Equal(0, s0.Algorithm);
        Assert.Equal(0, s0.Op1.Multiplier);
    }

    // --- YM2612 (no SSG-EG): SSG-EG register leaves value at zero ---
    [Fact]
    public void ChipWithoutSsgEg_KeepsSsgEgZero()
    {
        var capabilities = new OpnChipCapabilities(6, 2, SupportsSsgEg: false);
        var tracker = new OpnFmRegisterTracker(capabilities);

        tracker.HandleWrite(0, 0x90, 0x03); // SSG-EG would be 3, but unsupported
        tracker.HandleWrite(0, 0xB0, 1);

        OpnFmInstrument? s = tracker.HandleWrite(0, 0x28, KeyOn(0));
        Assert.Equal(0, s.Op1.SsgEg);
    }
}
