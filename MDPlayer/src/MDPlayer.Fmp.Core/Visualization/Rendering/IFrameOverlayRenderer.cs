namespace Fmp.Core.Visualization.Rendering;

#nullable enable

/// <summary>
/// The overlay renderer surface consumed by the production frame pipeline
/// (<see cref="VisualizationFrameRenderer"/> and the scope frame sources).
/// Both the CPU raster renderer (<see cref="PanelOverlayRenderer"/>) and the
/// GPU renderer (<c>GpuPanelRenderer</c>) implement this contract, so every
/// consumer (final video, GUI preview, visual review) stays renderer-agnostic.
///
/// The frame contract is identical for both backends: the renderer produces
/// full 1920×1080-style RGBA bytes (alpha-baked) for the FFmpeg frame slot.
/// GPU rendering differs only in how the surface is produced (hardware
/// rasterized, read back once per frame) — never in the information it shows.
/// </summary>
internal interface IFrameOverlayRenderer : IDisposable
{
    /// <summary>Canvas width in pixels.</summary>
    int Width { get; }

    /// <summary>Canvas height in pixels.</summary>
    int Height { get; }

    /// <summary>Size of one RGBA frame: <c>Width * Height * 4</c>.</summary>
    int FrameByteCount { get; }

    /// <summary>Size of one Corrscope RGBA scope grid frame.</summary>
    int ScopeFrameByteCount { get; }

    int FpsNumerator { get; }

    int FpsDenominator { get; }

    /// <summary>Total output frames covering the timeline.</summary>
    long TotalFrames { get; }

    /// <summary>Resolved panel/scope geometry shared with Corrscope.</summary>
    OverlayLayout Layout { get; }

    /// <summary>Optional accumulated renderer work counters.</summary>
    RenderPerformanceSnapshot Performance { get; }

    /// <summary>
    /// Renders a full single-layer frame (static chrome + dynamic content)
    /// without a scope grid.
    /// </summary>
    void RenderFrame(long frameIndex, Span<byte> destination);

    /// <summary>
    /// Renders the full frame into <paramref name="destination"/>: canvas
    /// background, scope grid (when provided), dynamic content and text. GPU
    /// implementations always redraw the full frame; no static caching.
    /// </summary>
    void RenderCompositeFrame(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination,
        bool scopeFramesAreOpaque = false);

    /// <summary>Writes a static/layout-only frame (no dynamic content).</summary>
    void WriteStaticFrame(Span<byte> destination);

    /// <summary>Creates the sequential export session for this renderer.</summary>
    ISequentialCompositeSession CreateSequentialSession(bool scopeFramesAreOpaque = false);

    /// <summary>
    /// True when output frames are opaque (alpha is always 255), so
    /// premultiplied and straight RGBA are identical. Consumers (the FFmpeg
    /// frame writer) then skip the full-frame unpremultiply pass. The CPU
    /// raster renderer keeps premultiplied Skia storage and reports false.
    /// </summary>
    bool ProducesOpaqueFrames => false;
}