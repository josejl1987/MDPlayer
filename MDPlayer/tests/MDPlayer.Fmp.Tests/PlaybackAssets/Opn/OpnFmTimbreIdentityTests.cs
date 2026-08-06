using Fmp.Core.PlaybackAssets.Opn;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets.Opn;

/// <summary>
/// Low-level tests for the algorithm-aware carrier model and the normalized
/// timbre identity that replaces literal-TFI deduplication.
/// </summary>
public class OpnFmTimbreIdentityTests
{
    private static OpnFmOperator Op(byte tl, byte multi = 0, byte detune = 0,
        byte ar = 0, byte dr = 0, byte sr = 0, byte rr = 0, byte sl = 0, byte ssg = 0) => new()
    {
        Multiplier = multi,
        DetuneRegister = detune,
        TotalLevel = tl,
        RateScaling = 0,
        AttackRate = ar,
        DecayRate = dr,
        SustainRate = sr,
        ReleaseRate = rr,
        SustainLevel = sl,
        SsgEg = ssg,
    };

    private static OpnFmInstrument Make(
        byte algorithm, byte feedback,
        byte op1Tl, byte op2Tl, byte op3Tl, byte op4Tl) => new()
    {
        Algorithm = algorithm,
        Feedback = feedback,
        Op1 = Op(op1Tl),
        Op2 = Op(op2Tl),
        Op3 = Op(op3Tl),
        Op4 = Op(op4Tl),
    };

    // --- carrier maps ---
    [Theory]
    [InlineData(0, (byte)(1 << 3))]
    [InlineData(1, (byte)(1 << 3))]
    [InlineData(2, (byte)(1 << 3))]
    [InlineData(3, (byte)(1 << 3))]
    [InlineData(4, (byte)((1 << 1) | (1 << 3)))]
    [InlineData(5, (byte)((1 << 1) | (1 << 2) | (1 << 3)))]
    [InlineData(6, (byte)((1 << 1) | (1 << 2) | (1 << 3)))]
    [InlineData(7, (byte)0x0F)]
    public void GetCarrierMask_MatchesOpnCarrierMap(byte algorithm, byte expected)
    {
        Assert.Equal(expected, OpnFmAlgorithm.GetCarrierMask(algorithm));
    }

    [Fact]
    public void GetCarrierMask_RejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OpnFmAlgorithm.GetCarrierMask(8));
    }

    // --- algorithm 7 feedback guard ---
    [Fact]
    public void Algorithm7_ZeroFeedback_NormalizesOp1()
    {
        byte mask = OpnFmAlgorithm.GetVolumeNormalizableCarrierMask(7, 0);
        Assert.Equal((byte)0x0F, mask);
    }

    [Fact]
    public void Algorithm7_NonzeroFeedback_PreservesOp1()
    {
        byte mask = OpnFmAlgorithm.GetVolumeNormalizableCarrierMask(7, 5);
        // Carriers for alg7 = {1,2,3,4}; Op1 removed -> {2,3,4} = 0x0E.
        Assert.Equal((byte)0x0E, mask);
        Assert.Equal(0, mask & (1 << 0));
    }

    // --- single-carrier normalization: Op4 loudest carrier -> 0 ---
    [Fact]
    public void SingleCarrier_Normalizes_LoudestToZero()
    {
        var instrument = Make(0, 0, 10, 10, 10, 20);
        byte[] normalized = OpnFmAlgorithm.GetNormalizedTotalLevels(instrument);
        // Op4 is the only carrier; its own TL is the loudest-carrier TL.
        Assert.Equal(0, normalized[3]);
        // Modulators stay literal.
        Assert.Equal(10, normalized[0]);
        Assert.Equal(10, normalized[1]);
        Assert.Equal(10, normalized[2]);
    }

    // --- multi-carrier: uniform shift collapses, balance preserved ---
    [Fact]
    public void MultiCarrier_UniformShift_ProducesSameNormalization()
    {
        var a = Make(4, 0, 0, 20, 0, 30); // carriers Op2=20, Op4=30
        var b = Make(4, 0, 0, 35, 0, 45); // carriers Op2=35, Op4=45

        byte[] na = OpnFmAlgorithm.GetNormalizedTotalLevels(a);
        byte[] nb = OpnFmAlgorithm.GetNormalizedTotalLevels(b);

        Assert.Equal(na, nb);
        Assert.Equal(0, na[1]); // loudest carrier Op2
        Assert.Equal(10, na[3]); // Op4 - 20
    }

    [Fact]
    public void MultiCarrier_DifferentBalance_ProducesDifferentNormalization()
    {
        var a = Make(4, 0, 0, 20, 0, 30); // (0, 10)
        var b = Make(4, 0, 0, 20, 0, 40); // (0, 20)

        byte[] na = OpnFmAlgorithm.GetNormalizedTotalLevels(a);
        byte[] nb = OpnFmAlgorithm.GetNormalizedTotalLevels(b);

        Assert.NotEqual(na, nb);
        Assert.Equal(0, na[1]);
        Assert.Equal(10, na[3]);
        Assert.Equal(0, nb[1]);
        Assert.Equal(20, nb[3]);
    }

    // --- volume offset = loudest carrier (op-level TL) ---
    [Fact]
    public void VolumeOffset_IsLoudestNormalizableCarrierTl()
    {
        Assert.Equal(20, OpnFmAlgorithm.GetVolumeOffset(Make(4, 0, 0, 20, 0, 30)));
        Assert.Equal(35, OpnFmAlgorithm.GetVolumeOffset(Make(4, 0, 0, 35, 0, 45)));
    }

    // --- algorithm 7 feedback: Op1 TL stays literal in identity ---
    [Fact]
    public void Algorithm7_Feedback_TreatsOp1AsTimbreSignificant()
    {
        var a = Make(7, 5, 20, 0, 0, 0);
        var b = Make(7, 5, 30, 0, 0, 0);

        byte[] ia = OpnFmTimbreIdentityWriter.Write(a);
        byte[] ib = OpnFmTimbreIdentityWriter.Write(b);

        // Different Op1 TL -> different identity (must not merge).
        Assert.NotEqual(ia, ib);
    }

    [Fact]
    public void Algorithm7_ZeroFeedback_Op1VolumeNormalized()
    {
        var a = Make(7, 0, 20, 10, 15, 5);
        var b = Make(7, 0, 35, 25, 30, 20);

        // With zero feedback, all four carriers are normalizable, so a uniform
        // 15-level shift yields the same identity.
        Assert.Equal(
            OpnFmTimbreIdentityWriter.Write(a),
            OpnFmTimbreIdentityWriter.Write(b));
    }

    // --- identity writer keeps all non-TL parameters significant ---
    [Fact]
    public void CarrierNonTlParamChange_YieldsDifferentIdentity()
    {
        // Algorithm 0: Op4 is the sole carrier. Changing its multiplier (a
        // non-TL parameter) while keeping its TL constant must change identity.
        var a = new OpnFmInstrument
        {
            Algorithm = 0, Feedback = 0,
            Op1 = Op(0), Op2 = Op(0), Op3 = Op(0), Op4 = Op(30, multi: 1),
        };
        var b = new OpnFmInstrument
        {
            Algorithm = 0, Feedback = 0,
            Op1 = Op(0), Op2 = Op(0), Op3 = Op(0), Op4 = Op(30, multi: 2),
        };

        Assert.NotEqual(
            OpnFmTimbreIdentityWriter.Write(a),
            OpnFmTimbreIdentityWriter.Write(b));
    }

    // --- canonicalization keeps modulators and preserves carrier balance ---
    [Fact]
    public void Canonicalize_MovesLoudestCarrierToZero()
    {
        var instrument = Make(4, 0, 7, 20, 9, 30);
        OpnFmInstrument canonical = OpnFmAlgorithm.Canonicalize(instrument);

        Assert.Equal(0, canonical.Op2.TotalLevel); // loudest carrier
        Assert.Equal(10, canonical.Op4.TotalLevel); // 30 - 20
        Assert.Equal(7, canonical.Op1.TotalLevel);  // modulator untouched
        Assert.Equal(9, canonical.Op3.TotalLevel);  // modulator untouched
    }

    [Fact]
    public void Canonicalize_Algorithm7Feedback_KeepsOp1Literal()
    {
        var instrument = Make(7, 5, 40, 20, 15, 10);
        OpnFmInstrument canonical = OpnFmAlgorithm.Canonicalize(instrument);

        // Op1 preserved verbatim; Op2/3/4 normalized among themselves
        // (loudest normalizable carrier Op4=10 -> 0, so Op2=10, Op3=5).
        Assert.Equal(40, canonical.Op1.TotalLevel);
        Assert.Equal(10, canonical.Op2.TotalLevel);
        Assert.Equal(5, canonical.Op3.TotalLevel);
        Assert.Equal(0, canonical.Op4.TotalLevel);
    }
}
