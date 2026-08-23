using System;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Conversions between premultiplied and straight (unpremultiplied) RGBA, the
/// two pixel encodings that meet at the renderer's byte-frame boundary.
/// </summary>
/// <remarks>
/// The Skia surface stores premultiplied pixels (RGB scaled by alpha), which
/// is the correct encoding for compositing. Byte consumers that follow the
/// pre-Skia straight-alpha contract (PNG/rawvideo encoders) must convert
/// before write. Conversion is a lossy round-trip by nature; alpha 0 and 255
/// pixels are identical in both encodings and pass through unchanged.
/// </remarks>
internal static class RgbaConversions
{
    /// <summary>Converts a premultiplied RGBA buffer to straight in place.</summary>
    public static void UnpremultiplyInPlace(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 0 || a == 255)
                continue;
            rgba[i] = (byte)Math.Min(255, (rgba[i] * 255 + a / 2) / a);
            rgba[i + 1] = (byte)Math.Min(255, (rgba[i + 1] * 255 + a / 2) / a);
            rgba[i + 2] = (byte)Math.Min(255, (rgba[i + 2] * 255 + a / 2) / a);
        }
    }
}