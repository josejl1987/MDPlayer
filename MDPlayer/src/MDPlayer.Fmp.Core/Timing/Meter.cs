#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// A musical time signature (meter). Beats per bar and the beat unit are
/// independent of BPM; meter is used only to interpret downbeats/bar
/// boundaries for the conductor track and never to derive tempo.
/// </summary>
internal sealed record Meter(
    int Numerator,
    int Denominator)
{
    /// <summary>The number of quarter-note durations in one bar in this meter.</summary>
    public double QuartersPerBar => Numerator * (4.0 / Denominator);

    public override string ToString() => $"{Numerator}/{Denominator}";

    /// <summary>
    /// Parses a meter expressed like "4/4" or "3/4". Returns null when
    /// malformed or when numerator/denominator are not positive.
    /// </summary>
    public static Meter? TryParse(string? text)
    {
        if (text is null)
            return null;
        string[] parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return null;
        if (!int.TryParse(parts[0], out int numerator) || numerator <= 0)
            return null;
        if (!int.TryParse(parts[1], out int denominator) || denominator <= 0)
            return null;
        return new Meter(numerator, denominator);
    }
}
