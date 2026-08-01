namespace Fmp.Core.Visualization.Composition.Contracts;

/// <summary>
/// Composition-independent RGBA color (greenfield reset §8.1). The new
/// composition layer uses this color type instead of the internal
/// <c>Rendering.OverlayColor</c> so generic drawing stays decoupled from the
/// legacy renderer.
/// </summary>
internal readonly record struct OverlayColor(byte R, byte G, byte B, byte A = 255)
{
    internal static readonly OverlayColor Transparent = new(0, 0, 0, 0);
    internal static readonly OverlayColor Black = new(0, 0, 0);
    internal static readonly OverlayColor White = new(255, 255, 255);

    internal OverlayColor WithAlpha(byte alpha) => new(R, G, B, alpha);

    internal OverlayColor Lighten(double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new OverlayColor(
            (byte)Math.Clamp(Math.Round(R + (255 - R) * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(G + (255 - G) * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(B + (255 - B) * amount), 0, 255),
            A);
    }
}
