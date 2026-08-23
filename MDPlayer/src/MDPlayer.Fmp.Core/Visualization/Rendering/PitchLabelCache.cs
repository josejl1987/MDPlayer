namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The one prepared pitch-label table used by both overlay renderers.
/// Formatting is deliberately bounded to the MIDI range and the existing
/// nearest-cent convention.
/// </summary>
internal static class PitchLabelCache
{
    private const int CentsPerNote = 201;
    private static readonly string[] PitchClassNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    internal static readonly string[] Labels = BuildLabels();

    internal static string Format(double actualMidi)
    {
        if (!double.IsFinite(actualMidi) || actualMidi < 0)
            return "";
        int nearestMidi = (int)Math.Round(actualMidi);
        if ((uint)nearestMidi >= 128u)
            return "";
        int cents = Math.Clamp((int)Math.Round((actualMidi - nearestMidi) * 100), -100, 100);
        return Labels[nearestMidi * CentsPerNote + cents + 100];
    }

    private static string[] BuildLabels()
    {
        var labels = new string[128 * CentsPerNote];
        for (int midi = 0; midi < 128; midi++)
        {
            string baseName = PitchClassNames[midi % 12] + (midi / 12 - 1);
            for (int cents = -100; cents <= 100; cents++)
            {
                labels[midi * CentsPerNote + cents + 100] = Math.Abs(cents) < 8
                    ? baseName
                    : baseName + " " + (cents > 0 ? "+" : "") + cents + "c";
            }
        }
        return labels;
    }
}

/// <summary>Presentation-only filtering for optional lane metadata.</summary>
internal static class PresentationMetadata
{
    internal static string OptionalLabel(string value)
        => string.IsNullOrWhiteSpace(value) || IsUnavailable(value.Trim()) ? null : value;

    internal static bool IsUnavailable(string value)
        => string.Equals(value, "TABLE UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "SMP UNKNOWN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "NO TABLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "---", StringComparison.Ordinal)
            || string.Equals(value, "?", StringComparison.Ordinal);
}

internal static class PolyphonyLabelCache
{
    private static readonly string[] Suffixes = BuildSuffixes();

    internal static string SuffixForAdditionalNotes(int additionalNotes)
    {
        if (additionalNotes <= 0)
            return "";
        return Suffixes[Math.Min(additionalNotes, Suffixes.Length - 1)];
    }

    private static string[] BuildSuffixes()
    {
        var suffixes = new string[33];
        for (int i = 1; i < suffixes.Length; i++)
            suffixes[i] = "+" + i;
        return suffixes;
    }
}
