using Fmp.Core.PlaybackAssets.Opn;

namespace Fmp.Core.PlaybackAssets.Furnace;

/// <summary>
/// Serializes a canonical <see cref="OpnFmInstrument"/> to the exact 42-byte
/// TFI layout. Deterministic and free of any filesystem logic. Operator
/// serialization order is 1, 3, 2, 4 (the TFI format order), and Yamaha
/// detune registers are converted through the explicit eight-entry mapping.
/// </summary>
internal static class TfiInstrumentWriter
{
    private static readonly OpnOperator[] OperatorOrder =
    {
        OpnOperator.Op1,
        OpnOperator.Op3,
        OpnOperator.Op2,
        OpnOperator.Op4
    };

    /// <summary>
    /// Yamaha detune register value to linear TFI detune value. Indexed by the
    /// three-bit register value (0..7). Yamaha 0 and 4 both canonicalize to 0.
    /// </summary>
    private static readonly byte[] YamahaDetuneToTfi =
    {
        3, // 0:  0
        4, // 1: +1
        5, // 2: +2
        6, // 3: +3
        3, // 4: -0, canonicalized to 0
        2, // 5: -1
        1, // 6: -2
        0  // 7: -3
    };

    public static byte[] Write(OpnFmInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        Validate(instrument);

        byte[] output = new byte[TfiFormat.FileSize];
        int offset = 0;

        output[offset++] = instrument.Algorithm;
        output[offset++] = instrument.Feedback;

        foreach (OpnOperator op in OperatorOrder)
        {
            OpnFmOperator source = instrument.GetOperator(op);

            output[offset++] = source.Multiplier;
            output[offset++] = YamahaDetuneToTfi[source.DetuneRegister];
            output[offset++] = source.TotalLevel;
            output[offset++] = source.RateScaling;
            output[offset++] = source.AttackRate;
            output[offset++] = source.DecayRate;
            output[offset++] = source.SustainRate;
            output[offset++] = source.ReleaseRate;
            output[offset++] = source.SustainLevel;
            output[offset++] = source.SsgEg;
        }

        if (offset != TfiFormat.FileSize)
        {
            throw new InvalidOperationException(
                $"Internal TFI size error: wrote {offset} bytes.");
        }

        return output;
    }

    /// <summary>Converts a raw three-bit Yamaha detune register value to its
    /// linear TFI representation via the explicit mapping.</summary>
    internal static byte ConvertDetuneToTfi(byte yamahaDetune)
    {
        if (yamahaDetune > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(yamahaDetune));
        }

        return YamahaDetuneToTfi[yamahaDetune];
    }

    private static void Validate(OpnFmInstrument instrument)
    {
        RequireRange(instrument.Algorithm, 0, 7, nameof(instrument.Algorithm));
        RequireRange(instrument.Feedback, 0, 7, nameof(instrument.Feedback));

        ValidateOperator(instrument.Op1, nameof(instrument.Op1));
        ValidateOperator(instrument.Op2, nameof(instrument.Op2));
        ValidateOperator(instrument.Op3, nameof(instrument.Op3));
        ValidateOperator(instrument.Op4, nameof(instrument.Op4));
    }

    private static void ValidateOperator(OpnFmOperator op, string prefix)
    {
        RequireRange(op.Multiplier, 0, 15, $"{prefix}.Multiplier");
        RequireRange(op.DetuneRegister, 0, 7, $"{prefix}.DetuneRegister");
        RequireRange(op.TotalLevel, 0, 127, $"{prefix}.TotalLevel");
        RequireRange(op.RateScaling, 0, 3, $"{prefix}.RateScaling");
        RequireRange(op.AttackRate, 0, 31, $"{prefix}.AttackRate");
        RequireRange(op.DecayRate, 0, 31, $"{prefix}.DecayRate");
        RequireRange(op.SustainRate, 0, 31, $"{prefix}.SustainRate");
        RequireRange(op.ReleaseRate, 0, 15, $"{prefix}.ReleaseRate");
        RequireRange(op.SustainLevel, 0, 15, $"{prefix}.SustainLevel");
        RequireRange(op.SsgEg, 0, 15, $"{prefix}.SsgEg");
    }

    private static void RequireRange(byte value, byte minimum, byte maximum, string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Expected {minimum}..{maximum}.");
        }
    }
}
