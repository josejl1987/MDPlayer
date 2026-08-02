using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// The single PNG-conversion helper for the CLI. Converts a raw RGBA frame into
/// PNG bytes, discarding the (already opaque) alpha. Used by preview, review and
/// the shared session.
/// </summary>
internal static class PngFrameEncoder
{
    public static byte[] Encode(
        int width,
        int height,
        ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        if (rgba.Length < checked(width * height * 4))
        {
            throw new ArgumentException(
                "RGBA buffer is too small",
                nameof(rgba));
        }

        byte[] data = rgba[..checked(width * height * 4)].ToArray();

        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowOffset = y * width * 4;
                for (int x = 0; x < row.Length; x++)
                {
                    int offset = rowOffset + x * 4;
                    row[x] = new Rgba32(
                        data[offset],
                        data[offset + 1],
                        data[offset + 2],
                        data[offset + 3]);
                }
            }
        });

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        image.Dispose();
        return stream.ToArray();
    }
}