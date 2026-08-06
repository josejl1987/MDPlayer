using Fmp.Core.PlaybackAssets.Furnace;

namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// Builds and serializes the normalized timbre identity of an OPN FM instrument
/// snapshot. The identity is distinct from the exported TFI: carrier total
/// level is normalized by algorithm (see <see cref="OpnFmAlgorithm"/>) so that a
/// uniform carrier-level shift dedupes to one instrument while preserving
/// carrier balance and every modulator TL. The serialized bytes are the
/// deterministic input to the deduplication hash.
/// </summary>
internal static class OpnFmTimbreIdentityWriter
{
    /// <summary>Operator serialization order in the identity byte stream. Kept
    /// identical to the TFI writer (1, 3, 2, 4) for symmetry with the exported
    /// file even though the identity is only ever hashed.</summary>
    private static readonly OpnOperator[] OperatorOrder =
    {
        OpnOperator.Op1,
        OpnOperator.Op3,
        OpnOperator.Op2,
        OpnOperator.Op4
    };

    /// <summary>Builds the structured identity for a snapshot.</summary>
    public static OpnFmTimbreIdentity Build(OpnFmInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        byte[] normalizedTl = OpnFmAlgorithm.GetNormalizedTotalLevels(instrument);
        byte normalizableMask = OpnFmAlgorithm.GetVolumeNormalizableCarrierMask(
            instrument.Algorithm, instrument.Feedback);

        return new OpnFmTimbreIdentity
        {
            Algorithm = instrument.Algorithm,
            Feedback = instrument.Feedback,
            Op1 = BuildOperator(instrument.Op1, normalizedTl[(int)OpnOperator.Op1], IsIn(normalizableMask, OpnOperator.Op1)),
            Op2 = BuildOperator(instrument.Op2, normalizedTl[(int)OpnOperator.Op2], IsIn(normalizableMask, OpnOperator.Op2)),
            Op3 = BuildOperator(instrument.Op3, normalizedTl[(int)OpnOperator.Op3], IsIn(normalizableMask, OpnOperator.Op3)),
            Op4 = BuildOperator(instrument.Op4, normalizedTl[(int)OpnOperator.Op4], IsIn(normalizableMask, OpnOperator.Op4)),
        };
    }

    /// <summary>Serializes a snapshot's normalized timbre identity to a fixed
    /// byte stream suitable for hashing.</summary>
    public static byte[] Write(OpnFmInstrument instrument)
    {
        OpnFmTimbreIdentity identity = Build(instrument);

        byte[] output = new byte[TfiFormat.FileSize];
        int offset = 0;

        output[offset++] = identity.Algorithm;
        output[offset++] = identity.Feedback;

        foreach (OpnOperator op in OperatorOrder)
        {
            OpnFmOperatorIdentity source = identity.Get(op);

            output[offset++] = source.Multiplier;
            output[offset++] = TfiInstrumentWriter.ConvertDetuneToTfi(source.DetuneRegister);
            output[offset++] = source.TotalLevel;
            output[offset++] = source.RateScaling;
            output[offset++] = source.AttackRate;
            output[offset++] = source.DecayRate;
            output[offset++] = source.SustainRate;
            output[offset++] = source.ReleaseRate;
            output[offset++] = source.SustainLevel;
            output[offset++] = source.SsgEg;
        }

        return output;
    }

    private static OpnFmOperatorIdentity BuildOperator(
        OpnFmOperator source,
        byte normalizedTl,
        bool isVolumeNormalizable)
    {
        return new OpnFmOperatorIdentity
        {
            Multiplier = source.Multiplier,
            DetuneRegister = source.DetuneRegister,
            TotalLevel = isVolumeNormalizable ? normalizedTl : source.TotalLevel,
            RateScaling = source.RateScaling,
            AttackRate = source.AttackRate,
            DecayRate = source.DecayRate,
            SustainRate = source.SustainRate,
            ReleaseRate = source.ReleaseRate,
            SustainLevel = source.SustainLevel,
            SsgEg = source.SsgEg,
        };
    }

    private static bool IsIn(byte mask, OpnOperator op) =>
        (mask & (1 << (int)op)) != 0;
}
