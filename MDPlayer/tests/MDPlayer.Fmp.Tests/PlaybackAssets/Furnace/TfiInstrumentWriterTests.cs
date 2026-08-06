using Fmp.Core.PlaybackAssets.Furnace;
using Fmp.Core.PlaybackAssets.Opn;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Furnace;

public class TfiInstrumentWriterTests
{
    private static OpnFmInstrument Empty() => new()
    {
        Algorithm = 0,
        Feedback = 0,
        Op1 = OpnFmOperator.Empty,
        Op2 = OpnFmOperator.Empty,
        Op3 = OpnFmOperator.Empty,
        Op4 = OpnFmOperator.Empty,
    };

    private static OpnFmOperator Op(params byte[] values) => new()
    {
        Multiplier = values[0],
        DetuneRegister = values[1],
        TotalLevel = values[2],
        RateScaling = values[3],
        AttackRate = values[4],
        DecayRate = values[5],
        SustainRate = values[6],
        ReleaseRate = values[7],
        SustainLevel = values[8],
        SsgEg = values[9],
    };

    // --- Test 13: exact file size applied to every writer test ---
    private static void AssertExactSize(byte[] bytes) =>
        Assert.Equal(42, bytes.Length);

    // --- Test 1: empty / zero instrument ---
    [Fact]
    public void EmptyInstrument_Yields_42_Bytes_WithZeroDetune_AsTfi3()
    {
        byte[] bytes = TfiInstrumentWriter.Write(Empty());
        AssertExactSize(bytes);

        Assert.Equal(0, bytes[0]); // algorithm
        Assert.Equal(0, bytes[1]); // feedback

        // Every operator detune byte becomes 3 (Yamaha 0 -> TFI 0 detune).
        int[] detuneOffsets = { 0x03, 0x0D, 0x17, 0x21 };
        foreach (int offset in detuneOffsets)
            Assert.Equal(3, bytes[offset]);

        // Every other operator byte is 0.
        for (int i = TfiFormat.GlobalSize; i < TfiFormat.FileSize; i++)
        {
            if (Array.IndexOf(detuneOffsets, i) >= 0)
                continue;
            Assert.Equal(0, bytes[i]);
        }
    }

    // --- Test 2: maximum values ---
    [Fact]
    public void MaxValues_Serialize_Exactly()
    {
        OpnFmOperator max = Op(15, 3, 127, 3, 31, 31, 31, 15, 15, 15);
        var instrument = new OpnFmInstrument
        {
            Algorithm = 7,
            Feedback = 7,
            Op1 = max,
            Op2 = max,
            Op3 = max,
            Op4 = max,
        };

        byte[] bytes = TfiInstrumentWriter.Write(instrument);
        AssertExactSize(bytes);

        Assert.Equal(7, bytes[0]);
        Assert.Equal(7, bytes[1]);

        foreach (int baseOffset in new[] { 0x02, 0x0C, 0x16, 0x20 })
        {
            Assert.Equal(15, bytes[baseOffset + 0]); // multiplier
            Assert.Equal(6, bytes[baseOffset + 1]);  // detune register 3 -> +3 -> 6
            Assert.Equal(127, bytes[baseOffset + 2]); // total level
            Assert.Equal(3, bytes[baseOffset + 3]);  // rate scaling
            Assert.Equal(31, bytes[baseOffset + 4]); // attack
            Assert.Equal(31, bytes[baseOffset + 5]); // decay
            Assert.Equal(31, bytes[baseOffset + 6]); // sustain rate
            Assert.Equal(15, bytes[baseOffset + 7]); // release
            Assert.Equal(15, bytes[baseOffset + 8]); // sustain level
            Assert.Equal(15, bytes[baseOffset + 9]); // SSG-EG
        }
    }

    // --- Test 3: detune mapping ---
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 4)]
    [InlineData(2, 5)]
    [InlineData(3, 6)]
    [InlineData(4, 3)]
    [InlineData(5, 2)]
    [InlineData(6, 1)]
    [InlineData(7, 0)]
    public void ConvertsYamahaDetuneToTfi(byte registerValue, byte expectedTfiValue)
    {
        Assert.Equal(
            expectedTfiValue,
            TfiInstrumentWriter.ConvertDetuneToTfi(registerValue));
    }

    // --- Test 4: operator serialization order (1,3,2,4) ---
    [Fact]
    public void OperatorSerializationOrder_Is_1_3_2_4()
    {
        var instrument = new OpnFmInstrument
        {
            Algorithm = 0,
            Feedback = 0,
            Op1 = Op(1, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Op2 = Op(2, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Op3 = Op(3, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Op4 = Op(4, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        byte[] bytes = TfiInstrumentWriter.Write(instrument);
        AssertExactSize(bytes);

        Assert.Equal(1, bytes[0x02]); // Op1
        Assert.Equal(3, bytes[0x0C]); // Op3
        Assert.Equal(2, bytes[0x16]); // Op2
        Assert.Equal(4, bytes[0x20]); // Op4
    }

    // --- Test 14 / Golden byte test ---
    // Fully explicit expected bytes; no calculated output.
    [Fact]
    public void GoldenByteFixture_Matches_LiteralExpectedBytes()
    {
        var instrument = new OpnFmInstrument
        {
            Algorithm = 5,
            Feedback = 6,

            Op1 = Op(1, 7, 11, 1, 12, 13, 14, 2, 3, 4),
            Op2 = Op(2, 6, 21, 2, 22, 23, 24, 3, 4, 5),
            Op3 = Op(3, 5, 31, 3, 25, 26, 27, 4, 5, 6),
            Op4 = Op(4, 3, 41, 0, 28, 29, 30, 5, 6, 7),
        };

        byte[] expected =
        {
            // Global
            5, 6,

            // Op1
            1, 0, 11, 1, 12, 13, 14, 2, 3, 4,

            // Op3
            3, 2, 31, 3, 25, 26, 27, 4, 5, 6,

            // Op2
            2, 1, 21, 2, 22, 23, 24, 3, 4, 5,

            // Op4
            4, 6, 41, 0, 28, 29, 30, 5, 6, 7
        };

        Assert.Equal(42, expected.Length);

        byte[] bytes = TfiInstrumentWriter.Write(instrument);
        AssertExactSize(bytes);
        Assert.Equal(expected, bytes);
    }

    // --- Literal checked-in fixture test ---
    [Fact]
    public void WriterOutput_Matches_CheckedInTfiFixture()
    {
        // A known-good 42-byte TFI fixture (manually imported into Furnace in
        // development). Read from the test output directory where the csproj
        // copies it, and compare byte-for-byte.
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory, "PlaybackAssets", "golden_instrument.tfi");
        Assert.True(File.Exists(fixturePath), $"fixture not found: {fixturePath}");

        byte[] fixture = File.ReadAllBytes(fixturePath);
        AssertExactSize(fixture);

        var instrument = new OpnFmInstrument
        {
            Algorithm = 5,
            Feedback = 6,
            Op1 = Op(1, 7, 11, 1, 12, 13, 14, 2, 3, 4),
            Op2 = Op(2, 6, 21, 2, 22, 23, 24, 3, 4, 5),
            Op3 = Op(3, 5, 31, 3, 25, 26, 27, 4, 5, 6),
            Op4 = Op(4, 3, 41, 0, 28, 29, 30, 5, 6, 7),
        };

        Assert.Equal(fixture, TfiInstrumentWriter.Write(instrument));
    }
}
