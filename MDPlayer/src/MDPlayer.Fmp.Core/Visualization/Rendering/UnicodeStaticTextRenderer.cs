using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Text;

namespace Fmp.Core.Visualization.Rendering;

internal static class UnicodeStaticTextRenderer
{
    public static bool CanRender(
        VisualizationPresentation presentation,
        string configuredFontPath)
    {
        if (presentation == null)
            return false;

        List<Rune> required = CollectRunes(presentation);
        if (required.Count == 0)
            return false;
        bool hasNonAscii = required.Exists(rune => rune.Value > 0x7F);
        bool explicitFont = !string.IsNullOrWhiteSpace(configuredFontPath)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FMP_RENDER_FONT"));
        // ASCII-only metadata is deliberately allowed to use the deterministic
        // bitmap layer. Do not let an unrelated broken system CJK candidate
        // turn an ASCII render into a font failure.
        if (!hasNonAscii && !explicitFont)
            return false;
        string fontPath = CjkFontResolver.Resolve(configuredFontPath);
        if (string.IsNullOrEmpty(fontPath))
        {
            if (hasNonAscii)
            {
                Rune missing = required.Find(rune => rune.Value > 0x7F);
                throw MissingFont(missing);
            }
            return false;
        }

        CjkFontResolver.ValidateCoverage(fontPath, required);
        return true;
    }

    public static bool TryDraw(
        Span<byte> destination,
        int width,
        int height,
        OverlayLayout layout,
        VisualizationPresentation presentation,
        string configuredFontPath)
    {
        if (presentation == null
            || (string.IsNullOrWhiteSpace(presentation.Title)
                && string.IsNullOrWhiteSpace(presentation.Subtitle)
                && string.IsNullOrWhiteSpace(presentation.Credits)))
        {
            return false;
        }

        string fontPath = CjkFontResolver.Resolve(configuredFontPath);
        if (string.IsNullOrEmpty(fontPath))
            return false;

        FontFamily family;
        Font titleFont;
        Font secondaryFont;
        family = CjkFontResolver.LoadFamily(fontPath, CollectRunes(presentation));
        titleFont = family.CreateFont(28, FontStyle.Regular);
        secondaryFont = family.CreateFont(16, FontStyle.Regular);

        using var image = new Image<Rgba32>(width, height, Color.Transparent);
        image.Mutate(context =>
        {
            OverlayRect top = layout.TopBarRect;
            OverlayRect bottom = layout.BottomBarRect;

            if (!string.IsNullOrWhiteSpace(presentation.Title))
            {
                context.DrawText(
                    presentation.Title,
                    titleFont,
                    Color.FromRgb(222, 226, 238),
                    new PointF(top.X + 24, top.Y + 6));
            }

            if (!string.IsNullOrWhiteSpace(presentation.Subtitle))
            {
                context.DrawText(
                    presentation.Subtitle,
                    secondaryFont,
                    Color.FromRgb(139, 146, 167),
                    new PointF(top.X + 24, top.Y + 39));
            }

            if (!string.IsNullOrWhiteSpace(presentation.Credits))
            {
                context.DrawText(
                    presentation.Credits,
                    secondaryFont,
                    Color.FromRgb(139, 146, 167),
                    new PointF(bottom.X + 24, bottom.Y + 8));
            }
        });

        // ImageSharp's ProcessPixelRows takes a lambda, and C# forbids capturing
        // a ref-like Span<byte> inside it. Copy the rendered pixels into a
        // managed buffer first, then blend over that buffer directly.
        int pixelByteCount = width * height * 4;
        byte[] pixels = new byte[pixelByteCount];
        image.CopyPixelDataTo(pixels);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int srcOffset = rowOffset + x * 4;
                byte alpha = pixels[srcOffset + 3];
                if (alpha == 0)
                    continue;

                int dstOffset = srcOffset;
                int inverse = 255 - alpha;
                destination[dstOffset] = (byte)((pixels[srcOffset] * alpha + destination[dstOffset] * inverse + 127) / 255);
                destination[dstOffset + 1] = (byte)((pixels[srcOffset + 1] * alpha + destination[dstOffset + 1] * inverse + 127) / 255);
                destination[dstOffset + 2] = (byte)((pixels[srcOffset + 2] * alpha + destination[dstOffset + 2] * inverse + 127) / 255);
                destination[dstOffset + 3] = 255;
            }
        }

        return true;
    }

    private static List<Rune> CollectRunes(VisualizationPresentation presentation)
    {
        var result = new List<Rune>();
        AddRunes(result, presentation.Title);
        AddRunes(result, presentation.Subtitle);
        AddRunes(result, presentation.Credits);
        return result;
    }

    private static void AddRunes(List<Rune> destination, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!destination.Contains(rune))
                destination.Add(rune);
        }
    }

    private static InvalidOperationException MissingFont(Rune rune)
        => new(
            $"error: selected font cannot render U+{rune.Value:X4} '{rune}' in title\n" +
            "hint: pass --font PATH to a CJK-capable TrueType/OpenType font");
}
