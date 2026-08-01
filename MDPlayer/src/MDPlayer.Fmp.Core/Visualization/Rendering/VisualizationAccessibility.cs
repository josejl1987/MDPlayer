namespace Fmp.Core.Visualization.Rendering;

internal enum ColorVisionDeficiency
{
    None,
    Protanopia,
    Deuteranopia,
    Tritanopia,
}

/// <summary>Deterministic palette checks for non-hue-only state encoding.</summary>
internal static class VisualizationAccessibility
{
    public static double ContrastRatio(OverlayColor foreground, OverlayColor background)
    {
        double foregroundLuminance = Luminance(foreground);
        double backgroundLuminance = Luminance(background);
        double high = Math.Max(foregroundLuminance, backgroundLuminance);
        double low = Math.Min(foregroundLuminance, backgroundLuminance);
        return (high + 0.05) / (low + 0.05);
    }

    public static OverlayColor Simulate(
        OverlayColor color,
        ColorVisionDeficiency deficiency)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        (double rr, double gg, double bb) = deficiency switch
        {
            ColorVisionDeficiency.Protanopia => (
                0.567 * r + 0.433 * g,
                0.558 * r + 0.442 * g,
                0.242 * g + 0.758 * b),
            ColorVisionDeficiency.Deuteranopia => (
                0.625 * r + 0.375 * g,
                0.700 * r + 0.300 * g,
                0.300 * g + 0.700 * b),
            ColorVisionDeficiency.Tritanopia => (
                0.950 * r + 0.050 * g,
                0.433 * g + 0.567 * b,
                0.475 * g + 0.525 * b),
            _ => (r, g, b),
        };
        return new OverlayColor(
            (byte)Math.Clamp(Math.Round(rr * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(gg * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(bb * 255), 0, 255),
            color.A);
    }

    public static bool Distinguishable(
        OverlayColor first,
        OverlayColor second,
        ColorVisionDeficiency deficiency,
        double minimumDistance = 24)
    {
        OverlayColor a = Simulate(first, deficiency);
        OverlayColor b = Simulate(second, deficiency);
        double distance = Math.Sqrt(
            Math.Pow(a.R - b.R, 2)
            + Math.Pow(a.G - b.G, 2)
            + Math.Pow(a.B - b.B, 2));
        return distance >= minimumDistance;
    }

    private static double Luminance(OverlayColor color)
    {
        static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.03928
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R)
            + 0.7152 * Linear(color.G)
            + 0.0722 * Linear(color.B);
    }
}
