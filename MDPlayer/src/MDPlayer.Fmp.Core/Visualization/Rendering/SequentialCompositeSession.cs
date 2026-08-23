namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Sequential single-pass frame session. The static chrome is copied once;
/// each subsequent frame restores only the deterministic dynamic rectangles,
/// inserts the current Corrscope grid, and draws the current overlay.
/// </summary>
internal sealed class SequentialCompositeSession : ISequentialCompositeSession
{
    private readonly PanelOverlayRenderer _renderer;
    private readonly PanelOverlayRenderer.SequentialRenderState _state;
    private readonly bool _scopeFramesAreOpaque;
    private bool _initialized;

    internal SequentialCompositeSession(PanelOverlayRenderer renderer, bool scopeFramesAreOpaque = false)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _state = _renderer.CreateSequentialRenderState();
        _scopeFramesAreOpaque = scopeFramesAreOpaque;
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

        _renderer.RenderForSession(
            frameIndex, scopeGrid, destination, _state, _scopeFramesAreOpaque);
    }
}
