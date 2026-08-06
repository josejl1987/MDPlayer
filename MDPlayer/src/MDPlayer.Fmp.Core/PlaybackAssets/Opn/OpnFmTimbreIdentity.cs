namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// TL-normalized identity of a single operator, used for deduplication. Every
/// non-TL parameter is copied literally; only the total level may be rewritten
/// by the algorithm-aware carrier normalization.
/// </summary>
internal sealed record OpnFmOperatorIdentity
{
    public required byte Multiplier { get; init; }
    public required byte DetuneRegister { get; init; }
    public required byte TotalLevel { get; init; }
    public required byte RateScaling { get; init; }
    public required byte AttackRate { get; init; }
    public required byte DecayRate { get; init; }
    public required byte SustainRate { get; init; }
    public required byte ReleaseRate { get; init; }
    public required byte SustainLevel { get; init; }
    public required byte SsgEg { get; init; }
}

/// <summary>
/// A normalized timbre-identity for one four-operator OPN FM instrument. It is
/// deliberately distinct from the exported <see cref="OpnFmInstrument"/> / TFI
/// bytes: carrier total level has been normalized to the common attenuation
/// offset shared by the volume-normalizable carriers (so a uniform level shift
/// dedupes to the same identity) while preserving their relative balance and
/// every modulator TL. Used only as the deduplication key, never for export.
/// </summary>
internal sealed record OpnFmTimbreIdentity
{
    public required byte Algorithm { get; init; }
    public required byte Feedback { get; init; }

    public required OpnFmOperatorIdentity Op1 { get; init; }
    public required OpnFmOperatorIdentity Op2 { get; init; }
    public required OpnFmOperatorIdentity Op3 { get; init; }
    public required OpnFmOperatorIdentity Op4 { get; init; }

    /// <summary>Retrieves an operator identity by logical operator.</summary>
    public OpnFmOperatorIdentity Get(OpnOperator op) => op switch
    {
        OpnOperator.Op1 => Op1,
        OpnOperator.Op2 => Op2,
        OpnOperator.Op3 => Op3,
        OpnOperator.Op4 => Op4,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };
}
