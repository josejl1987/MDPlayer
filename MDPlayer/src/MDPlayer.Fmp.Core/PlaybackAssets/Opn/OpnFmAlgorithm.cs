namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// Provides the algorithm-aware carrier model for OPN FM four-operator
/// synthesis, used to normalize carrier total level during deduplication.
///
/// Yamaha OPN algorithm 0..3 have a single audible carrier (Op4); algorithms
/// 4..7 progressively expose more operators as carriers. A carrier's total
/// level (TL) largely sets that operator's output loudness, so a driver that
/// implements channel volume by rewriting carrier TL produces patches that
/// differ only by a common attenuation offset. We normalize that offset away
/// while preserving each carrier's attenuation relative to the loudest
/// carrier, so such observations dedupe to one instrument.
///
/// Modulator TL is never normalized: it changes harmonic content, not just
/// level. Operator 1 of algorithm 7 is treated conservatively because it owns
/// the feedback loop: when feedback is nonzero, changing Op1 TL may alter the
/// self-feedback waveform, so it is excluded from volume normalization.
/// </summary>
internal static class OpnFmAlgorithm
{
    private const byte Op1Mask = 1 << 0;
    private const byte Op2Mask = 1 << 1;
    private const byte Op3Mask = 1 << 2;
    private const byte Op4Mask = 1 << 3;

    /// <summary>Bitset of audible carrier operators, indexed by algorithm.</summary>
    private static readonly byte[] CarrierMasks =
    {
        Op4Mask,                         // Algorithm 0
        Op4Mask,                         // Algorithm 1
        Op4Mask,                         // Algorithm 2
        Op4Mask,                         // Algorithm 3
        Op2Mask | Op4Mask,               // Algorithm 4
        Op2Mask | Op3Mask | Op4Mask,     // Algorithm 5
        Op2Mask | Op3Mask | Op4Mask,     // Algorithm 6
        Op1Mask | Op2Mask | Op3Mask | Op4Mask // Algorithm 7
    };

    /// <summary>Returns the bitset of audible carrier operators for an algorithm.</summary>
    public static byte GetCarrierMask(byte algorithm)
    {
        if (algorithm > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        return CarrierMasks[algorithm];
    }

    /// <summary>
    /// Returns the bitset of carriers whose TL may be treated as pure volume
    /// (and therefore normalized away during deduplication). For algorithm 7
    /// with nonzero feedback, Op1 is removed because it owns the feedback loop.
    /// </summary>
    public static byte GetVolumeNormalizableCarrierMask(byte algorithm, byte feedback)
    {
        byte mask = GetCarrierMask(algorithm);

        if (algorithm == 7 && feedback != 0)
        {
            mask &= unchecked((byte)~Op1Mask);
        }

        return mask;
    }

    /// <summary>
    /// The common attenuation offset among the volume-normalizable carriers of
    /// an instrument: the loudest (lowest-TL) such carrier's total level. Lower
    /// Yamaha TL is louder. This is the value normalized away from every
    /// eligible carrier, and recorded as the observation's volume offset.
    /// </summary>
    public static byte GetVolumeOffset(OpnFmInstrument instrument)
    {
        byte mask = GetVolumeNormalizableCarrierMask(instrument.Algorithm, instrument.Feedback);

        byte[] source =
        {
            instrument.Op1.TotalLevel,
            instrument.Op2.TotalLevel,
            instrument.Op3.TotalLevel,
            instrument.Op4.TotalLevel
        };

        byte loudest = byte.MaxValue;
        for (int op = 0; op < 4; op++)
        {
            if ((mask & (1 << op)) != 0)
            {
                loudest = Math.Min(loudest, source[op]);
            }
        }

        if (loudest == byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Algorithm {instrument.Algorithm} has no volume-normalizable carriers.");
        }

        return loudest;
    }

    /// <summary>
    /// Returns TL per logical operator (index 0..3 = Op1..Op4) normalized for
    /// deduplication. Volume-normalizable carriers have the common offset the
    /// loudest carrier down to TL=0 while preserving their relative attenuation;
    /// every other operator (modulators, and Op1 when feedback-preserved) keeps
    /// its literal TL.
    /// </summary>
    public static byte[] GetNormalizedTotalLevels(OpnFmInstrument instrument)
    {
        byte mask = GetVolumeNormalizableCarrierMask(instrument.Algorithm, instrument.Feedback);

        byte[] source =
        {
            instrument.Op1.TotalLevel,
            instrument.Op2.TotalLevel,
            instrument.Op3.TotalLevel,
            instrument.Op4.TotalLevel
        };

        byte loudestCarrierTl = byte.MaxValue;

        for (int op = 0; op < 4; op++)
        {
            if ((mask & (1 << op)) != 0)
            {
                loudestCarrierTl = Math.Min(loudestCarrierTl, source[op]);
            }
        }

        if (loudestCarrierTl == byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Algorithm {instrument.Algorithm} has no carriers.");
        }

        byte[] normalized = (byte[])source.Clone();

        for (int op = 0; op < 4; op++)
        {
            if ((mask & (1 << op)) != 0)
            {
                normalized[op] = (byte)(source[op] - loudestCarrierTl);
            }
        }

        return normalized;
    }

    /// <summary>
    /// Produces the canonical exported instrument for an observed snapshot:
    /// volume-normalizable carriers are moved down by the common attenuation
    /// offset (loudest carrier to TL=0), preserving their relative balance and
    /// leaving every modulator TL untouched. For algorithm 7 with feedback,
    /// Op1 keeps its observed TL. Modulators are never rewritten.
    /// </summary>
    public static OpnFmInstrument Canonicalize(OpnFmInstrument instrument)
    {
        byte mask = GetVolumeNormalizableCarrierMask(instrument.Algorithm, instrument.Feedback);
        byte[] normalizedTl = GetNormalizedTotalLevels(instrument);

        return new OpnFmInstrument
        {
            Algorithm = instrument.Algorithm,
            Feedback = instrument.Feedback,
            Op1 = RebuildOperator(instrument.Op1, normalizedTl[(int)OpnOperator.Op1], IsIn(mask, OpnOperator.Op1)),
            Op2 = RebuildOperator(instrument.Op2, normalizedTl[(int)OpnOperator.Op2], IsIn(mask, OpnOperator.Op2)),
            Op3 = RebuildOperator(instrument.Op3, normalizedTl[(int)OpnOperator.Op3], IsIn(mask, OpnOperator.Op3)),
            Op4 = RebuildOperator(instrument.Op4, normalizedTl[(int)OpnOperator.Op4], IsIn(mask, OpnOperator.Op4)),
        };
    }

    private static OpnFmOperator RebuildOperator(
        OpnFmOperator source,
        byte normalizedTl,
        bool isVolumeNormalizable)
    {
        return new OpnFmOperator
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
