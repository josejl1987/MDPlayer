using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Row-level raster helpers for the overlay compositor. These exist because
/// the hot paths blend large rectangles whose destination is frequently
/// uniform; detecting that with vector compares and collapsing to a single
/// packed fill removes the bulk of per-pixel work while keeping the exact
/// integer blend equation
/// <c>(src*A + dst*(255-A) + 127) / 255</c>.
/// </summary>
internal static class SimdRowOps
{
    /// <summary>
    /// True when every byte in <paramref name="span"/> equals
    /// <paramref name="span"/>[0]. Vector compare + fold; falls back to a
    /// plain scan when 256-bit vectors are unavailable.
    /// </summary>
    public static bool IsUniform(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
            return true;
        byte first = span[0];
        ref byte head = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);
        int i = 0;
        if (Vector256.IsHardwareAccelerated && span.Length >= 32)
        {
            var broadcast = Vector256.Create(first);
            int limit = span.Length - 31;
            for (; i < limit; i += 32)
            {
                var chunk = Vector256.LoadUnsafe(ref head, (uint)i);
                if (chunk != broadcast)
                    return false;
            }
        }
        for (; i < span.Length; i++)
        {
            if (Unsafe.Add(ref head, i) != first)
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when every 4-byte pixel in <paramref name="pixels"/> equals the
    /// given RGBA byte pattern (frame layout R,G,B,A). Vector compare + fold.
    /// </summary>
    public static bool AllPixelsEqual(ReadOnlySpan<byte> pixels, byte r, byte g, byte b, byte a)
    {
        if (pixels.Length == 0 || (pixels.Length & 3) != 0)
            return false;
        ref byte head = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(pixels);
        int i = 0;
        if (Vector256.IsHardwareAccelerated && pixels.Length >= 32)
        {
            var pattern = Vector256.Create(
                r, g, b, a, r, g, b, a, r, g, b, a, r, g, b, a,
                r, g, b, a, r, g, b, a, r, g, b, a, r, g, b, a);
            int limit = pixels.Length - 31;
            for (; i < limit; i += 32)
            {
                var chunk = Vector256.LoadUnsafe(ref head, (uint)i);
                if (chunk != pattern)
                    return false;
            }
        }
        for (; i + 3 < pixels.Length; i += 4)
        {
            if (Unsafe.Add(ref head, i) != r ||
                Unsafe.Add(ref head, i + 1) != g ||
                Unsafe.Add(ref head, i + 2) != b ||
                Unsafe.Add(ref head, i + 3) != a)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Fills <paramref name="span"/> (length must be a multiple of 4) with the
    /// repeating little-endian RGBA pattern of <paramref name="packed"/>.
    /// </summary>
    public static void FillUInt32(Span<byte> span, uint packed)
    {
        ref byte head = ref MemoryMarshal.GetReference(span);
        int i = 0;
        if (Vector256.IsHardwareAccelerated && span.Length >= 32)
        {
            var v = Vector256.Create(packed).AsByte();
            int limit = span.Length - 31;
            for (; i < limit; i += 32)
                v.StoreUnsafe(ref head, (uint)i);
        }
        for (; i + 3 < span.Length; i += 4)
        {
            Unsafe.Add(ref head, i) = (byte)packed;
            Unsafe.Add(ref head, i + 1) = (byte)(packed >> 8);
            Unsafe.Add(ref head, i + 2) = (byte)(packed >> 16);
            Unsafe.Add(ref head, i + 3) = (byte)(packed >> 24);
        }
    }

    /// <summary>
    /// Resolves <paramref name="source"/> over a uniform opaque background in
    /// one step: identical to blending each pixel individually over
    /// <paramref name="background"/>, then filling opaquely with the result.
    /// Only valid when the caller verified uniformity.
    /// </summary>
    public static OverlayColor BlendOverUniform(OverlayColor source, OverlayColor background)
    {
        int a = source.A;
        if (a == 255)
            return source with { A = 255 };
        int inverse = 255 - a;
        return new OverlayColor(
            (byte)((source.R * a + background.R * inverse + 127) / 255),
            (byte)((source.G * a + background.G * inverse + 127) / 255),
            (byte)((source.B * a + background.B * inverse + 127) / 255),
            255);
    }
}
