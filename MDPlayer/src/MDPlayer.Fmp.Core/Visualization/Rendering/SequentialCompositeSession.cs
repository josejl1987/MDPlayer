namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Sequential single-pass frame session. The static chrome is copied once;
/// each subsequent frame restores only the deterministic dynamic rectangles,
/// inserts the current Corrscope grid, and draws the current overlay.
/// </summary>
internal sealed class SequentialCompositeSession
{
    private readonly PanelOverlayRenderer _renderer;
    private bool _initialized;

    internal SequentialCompositeSession(PanelOverlayRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public void Initialize(Span<byte> destination)
    {
        if (destination.Length < _renderer.FrameByteCount)
            throw new ArgumentException(
                $"Destination requires at least {_renderer.FrameByteCount} bytes.",
                nameof(destination));

        _renderer.WriteStaticFrame(destination);
        _initialized = true;
    }

    public void RenderNext(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination)
    {
        if (!_initialized)
            throw new InvalidOperationException("The sequential session must be initialized first.");

        _renderer.ValidateFrameForSession(frameIndex, destination);
        _renderer.RestoreDynamicRegions(destination);
        _renderer.PlaceScopeRowsForSession(scopeGrid, destination);
        _renderer.DrawDynamicForSession(frameIndex, destination);
    }
}
