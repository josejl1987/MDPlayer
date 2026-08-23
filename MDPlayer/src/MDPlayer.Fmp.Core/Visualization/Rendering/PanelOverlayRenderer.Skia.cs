using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// SkiaSharp backend for the overlay renderer. The frame lives in an
/// <see cref="SKSurface"/>; every drawing primitive routes through
/// <see cref="Canvas"/> and the finished pixels are copied out to the
/// caller-owned RGBA buffer once per composite.
/// </summary>
internal sealed partial class PanelOverlayRenderer
{
    private SKSurface _surface;
    private GRSKBacking? _backing;

    private readonly object _skiaGate = new();
    private SKCanvas _canvas;

    // Reusable paints: Skia copies paint attributes at draw time, so the hot
    // path mutates Color/BlendMode on these fields instead of allocating a
    // fresh SKPaint per primitive (per-frame allocation is a hard contract,
    // §20.1).
    private readonly SKPaint _fillPaint = new() { IsAntialias = false };
    private readonly SKPaint _fillPaintAA = new() { IsAntialias = true };
    private readonly SKPaint _clearPaint = new() { IsAntialias = false, BlendMode = SKBlendMode.Src };
    private readonly SKPaint _blurLayerPaint = new() { IsAntialias = false };

    /// <summary>
    /// Layer paint for restoring dynamic regions from the cached static
    /// layer: each region is cleared inside a clipped saveLayer composited
    /// with BlendMode.Src, so semi-transparent static pixels replace the
    /// previous frame's ink verbatim instead of blending with it — matching
    /// what the full path produces over a cleared canvas.
    /// </summary>
    private readonly SKPaint _restoreLayerPaint = new()
    {
        IsAntialias = false,
        BlendMode = SKBlendMode.Src,
    };

    /// <summary>The active Skia canvas.</summary>
    private SKCanvas Canvas => _canvas;

    private SKColor ToSk(OverlayColor c) => new(c.R, c.G, c.B, c.A);

    private sealed class GRSKBacking : IDisposable
    {
        public byte[] Pixels;
        public System.Runtime.InteropServices.GCHandle Handle;
        public void Dispose()
        {
            if (Handle.IsAllocated) Handle.Free();
        }
    }

    private void InitSkia()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _backing = new GRSKBacking
        {
            Pixels = GC.AllocateArray<byte>(info.BytesSize, pinned: true),
            Handle = default,
        };
        _backing.Handle = System.Runtime.InteropServices.GCHandle.Alloc(_backing.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        _surface = SKSurface.Create(info);
        _canvas = _surface.Canvas;
        if (_surface is null)
            throw new InvalidOperationException("Failed to create Skia surface for the overlay renderer.");
    }

    private void DisposeSkia()
    {
        _fillPaint.Dispose();
        _fillPaintAA.Dispose();
        _clearPaint.Dispose();
        _blurLayerPaint.Dispose();
        _restoreLayerPaint.Dispose();
        _staticImage?.Dispose();
        _staticImage = null;
        _scopeImage?.Dispose();
        _scopeImage = null;
        _surface?.Dispose();
        _surface = null;
        _backing?.Dispose();
        _backing = null;
    }

    private SKImage _staticImage;
    private bool _staticImageValid;

    /// <summary>
    /// True when the canvas currently carries the cached static chrome (from a
    /// previous composite), so the next composite can skip the full-frame
    /// blit and restore only the dynamic regions. Set false by
    /// <see cref="RenderDynamicFrame"/>, which leaves only a transparent
    /// dynamic overlay on the canvas.
    /// </summary>
    private bool _surfaceHasStatic;

    private SKImage _scopeImage;
    private int _scopeImageBytes;

    /// <summary>Copies the finished Skia frame into the caller-owned RGBA buffer.</summary>
    private void CopySurfaceTo(Span<byte> destination)
    {
        Canvas.Flush();
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _surface.ReadPixels(info, _backing.Handle.AddrOfPinnedObject(), info.RowBytes, 0, 0);
        _backing.Pixels.AsSpan(0, Math.Min(_backing.Pixels.Length, destination.Length)).CopyTo(destination);
    }

    /// <summary>Reads the current surface pixels into a fresh buffer.</summary>
    private byte[] ReadSurfacePixels()
    {
        Canvas.Flush();
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var buffer = new byte[info.BytesSize];
        _surface.ReadPixels(info, System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), info.RowBytes, 0, 0);
        return buffer;
    }
}
