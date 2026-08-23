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

/// <summary>
/// Optional pipelined extension: the session retains up to <see cref="MaxInFlight"/>
/// frames and publishes them after their GPU readback completes. This lets the
/// render thread keep the GPU busy (draw N+1 while readback of N is in flight)
/// instead of blocking on every frame. CPU sessions return <c>false</c> for
/// <see cref="SupportsDeferred"/> and are used synchronously.
/// </summary>
internal interface IDeferredCompletionSession : ISequentialCompositeSession
{
    bool SupportsDeferred => true;

    int MaxInFlight { get; }

    int PendingCount { get; }

    void Submit(SinglePassComposer.FrameSlot slot, long frameIndex, SinglePassComposer.PipelineMetrics metrics);

    bool TryDequeueCompleted(SinglePassComposer.PipelineMetrics metrics, out SinglePassComposer.FrameSlot slot);

    SinglePassComposer.FrameSlot WaitForOldest(SinglePassComposer.PipelineMetrics metrics);

    void CompleteAll(SinglePassComposer.PipelineMetrics metrics);
}