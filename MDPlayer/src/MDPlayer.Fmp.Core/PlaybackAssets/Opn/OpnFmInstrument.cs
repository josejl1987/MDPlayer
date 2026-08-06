namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// Chip-independent canonical in-memory model of one four-operator OPN FM
/// instrument. Suitable for TFI serialization, equality, deterministic
/// hashing, manifest generation and unit tests. Immutable: a later register
/// write must never mutate an already captured instrument.
/// </summary>
internal sealed record OpnFmInstrument
{
    public required byte Algorithm { get; init; }
    public required byte Feedback { get; init; }

    public required OpnFmOperator Op1 { get; init; }
    public required OpnFmOperator Op2 { get; init; }
    public required OpnFmOperator Op3 { get; init; }
    public required OpnFmOperator Op4 { get; init; }

    public OpnFmOperator GetOperator(OpnOperator op) => op switch
    {
        OpnOperator.Op1 => Op1,
        OpnOperator.Op2 => Op2,
        OpnOperator.Op3 => Op3,
        OpnOperator.Op4 => Op4,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };
}
