namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// Canonical decoded state of a single four-operator OPN FM operator.
/// <see cref="DetuneRegister"/> stores the raw three-bit Yamaha detune
/// register value (not the linear TFI representation). All other fields
/// store decoded numeric values, not whole packed register bytes.
/// </summary>
internal sealed record OpnFmOperator
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

    /// <summary>A fully zeroed operator, as produced by the hardware reset state.</summary>
    public static OpnFmOperator Empty => new()
    {
        Multiplier = 0,
        DetuneRegister = 0,
        TotalLevel = 0,
        RateScaling = 0,
        AttackRate = 0,
        DecayRate = 0,
        SustainRate = 0,
        ReleaseRate = 0,
        SustainLevel = 0,
        SsgEg = 0,
    };
}
