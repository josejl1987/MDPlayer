namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Sequential single-pass frame session contract, backend-agnostic. The CPU
/// implementation restores only deterministic dynamic rectangles between
/// adjacent frames; the GPU implementation always redraws the full frame
/// (the GPU path performs no static caching by design).
/// </summary>
internal interface ISequentialCompositeSession
{
    void Initialize(Span<byte> destination);

    void RenderNext(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination);
}