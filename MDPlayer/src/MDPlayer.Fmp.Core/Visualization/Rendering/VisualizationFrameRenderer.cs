using System.Diagnostics;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Small composition boundary between semantic drawing and the external scope
/// layer. It owns exactly one <see cref="PanelOverlayRenderer"/> and (when
/// scopes are enabled) one <see cref="CorrscopeFrameSource"/>.
///
/// This is the ONE production frame renderer: final video, GUI preview and
/// visual-review generation all call <see cref="RenderFrame"/> and only differ
/// in what they do with the returned RGBA bytes. Callers never need to know
/// whether the source is FMP, VGM or SPC, how Corrscope was launched, or how
/// raw scope frames are streamed.
/// </summary>
internal sealed class VisualizationFrameRenderer : IDisposable
{
    private readonly IFrameOverlayRenderer _overlay;
    private readonly IScopeFrameSource? _scopeFrames;
    private readonly byte[]? _scopeBuffer;
    private readonly byte[]? _lastScopeFrame;
    private readonly double _scopeFps;
    private long _lastScopeFrameIndex = -1;
    private long _scopeFrameReadTicks;
    private bool _disposed;

    public VisualizationFrameRenderer(
        IFrameOverlayRenderer overlay,
        IScopeFrameSource? scopeFrames,
        double? scopeFps = null)
    {
        _overlay = overlay
            ?? throw new ArgumentNullException(nameof(overlay));
        _scopeFrames = scopeFrames;

        if (_scopeFrames is not null)
        {
            _scopeBuffer = new byte[_overlay.ScopeFrameByteCount];
            // One extra grid for the reuse cache: CorrscopeFrameSource restarts
            // its bridge process on any backward read, so re-reading the same
            // mapped scope frame for consecutive output frames would restart
            // per duplicate. The cache serves repeats from memory (plan §5.2).
            _lastScopeFrame = new byte[_overlay.ScopeFrameByteCount];
        }

        _scopeFps = ScopeFrameMapping.Resolve(
            scopeFps,
            _overlay.FpsNumerator / (double)_overlay.FpsDenominator);
    }

    public int Width => _overlay.Width;
    public int Height => _overlay.Height;
    public int FrameByteCount => _overlay.FrameByteCount;
    public int ScopeFrameByteCount => _overlay.ScopeFrameByteCount;
    public long TotalFrames => _overlay.TotalFrames;
    public int OverlayFpsNumerator => _overlay.FpsNumerator;
    public int OverlayFpsDenominator => _overlay.FpsDenominator;
    internal RenderPerformanceSnapshot Performance => _overlay.Performance;
    internal double ScopeFrameReadSeconds =>
        _scopeFrameReadTicks / (double)Stopwatch.Frequency;

    internal void ReadScopeFrame(long frameIndex, Span<byte> destination)
    {
        if (_scopeFrames is null)
            return;
        if (destination.Length < ScopeFrameByteCount)
            throw new ArgumentException(
                $"Destination requires at least {ScopeFrameByteCount} bytes.",
                nameof(destination));

        long mapped = ScopeFrameMapping.Map(
            frameIndex, _scopeFps, _overlay.FpsNumerator / (double)_overlay.FpsDenominator);
        long readStart = Stopwatch.GetTimestamp();
        if (mapped == _lastScopeFrameIndex)
        {
            // Cache hit: the same scope frame serves consecutive output
            // frames. Re-reading it from the source would be a backward read
            // (CorrscopeFrameSource restarts its bridge process).
            _lastScopeFrame!.AsSpan().CopyTo(destination);
        }
        else
        {
            _scopeFrames.ReadFrame(checked((int)mapped), destination);
            destination[..ScopeFrameByteCount].CopyTo(_lastScopeFrame!);
            _lastScopeFrameIndex = mapped;
        }
        _scopeFrameReadTicks += Stopwatch.GetTimestamp() - readStart;
    }

    /// <summary>
    /// True when a scope frame source (Corrscope bridge or the internal
    /// master-waveform fallback) is present, so scope viewports render real
    /// content instead of transparent holes. With the internal fallback this
    /// is true whenever scopes are enabled and a master WAV exists, keeping
    /// final, preview and review identical even without Corrscope.
    /// </summary>
    public bool HasScopeSource => _scopeFrames is not null;

    /// <summary>
    /// True when the active scope source is the in-process random-access
    /// channel reader (which approximates Corrscope's triggering) rather than
    /// the production Corrscope bridge or master-waveform fallback.
    /// </summary>
    public bool UsesApproximatedScopeSource =>
        _scopeFrames is InteractiveWaveformFrameSource;

    /// <summary>
    /// For an interactive scope source, the count of mapped scope channels whose
    /// stem WAV could not be opened (their panels render transparent). Zero for
    /// any other source.
    /// </summary>
    public int InteractiveScopeUnavailableChannelCount =>
        _scopeFrames is InteractiveWaveformFrameSource interactive
            ? interactive.UnavailableChannelCount
            : 0;

    /// <summary>
    /// Produces the finished RGBA frame for <paramref name="frameIndex"/>:
    /// semantic panels, layout background, scopes (or the transparent scope
    /// holes when no isolated scope source exists), title/credits, analysis
    /// overlays, energy effects and intro/outro presentation.
    ///
    /// This is the single method every consumer uses for an accurate frame.
    /// </summary>
    public void RenderFrame(long frameIndex, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.Length < FrameByteCount)
        {
            throw new ArgumentException(
                "destination is smaller than one RGBA frame",
                nameof(destination));
        }

        frameIndex = Math.Clamp(
            frameIndex,
            0,
            Math.Max(0, TotalFrames - 1));

        if (_scopeFrames is null)
        {
            _overlay.RenderFrame(frameIndex, destination);
            return;
        }

        ReadScopeFrame(frameIndex, _scopeBuffer!);

        _overlay.RenderCompositeFrame(
            frameIndex,
            _scopeBuffer!,
            destination,
            _scopeFrames.FramesAreOpaque);
    }

    public byte[] RenderFrame(long frameIndex)
    {
        byte[] frame = new byte[FrameByteCount];
        RenderFrame(frameIndex, frame);
        return frame;
    }

    /// <summary>
    /// Creates the sequential export session. Unlike random-access
    /// <see cref="RenderFrame(long, Span{byte})"/>, this preserves the static
    /// surface and restores only the dynamic regions between adjacent frames.
    /// </summary>
    internal SequentialSession CreateSequentialSession()
        => new(this, _overlay.CreateSequentialSession(_scopeFrames?.FramesAreOpaque == true));

    /// <summary>
    /// Renders the static/layout-only path (chrome, metadata bars, title and
    /// credits) without any dynamic content or scopes. Used for the explicitly
    /// approximate <c>layout</c> preview fidelity.
    /// </summary>
    public void RenderStaticFrame(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _overlay.WriteStaticFrame(destination);
    }

    public byte[] RenderStaticFrame()
    {
        byte[] frame = new byte[FrameByteCount];
        RenderStaticFrame(frame);
        return frame;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _scopeFrames?.Dispose();
        _overlay.Dispose();
    }

    internal sealed class SequentialSession
    {
        private readonly VisualizationFrameRenderer _owner;
        private readonly ISequentialCompositeSession _overlay;

        internal SequentialSession(
            VisualizationFrameRenderer owner,
            ISequentialCompositeSession overlay)
        {
            _owner = owner;
            _overlay = overlay;
        }

        internal void Initialize(Span<byte> destination)
            => _overlay.Initialize(destination);

        internal void RenderNext(long frameIndex, Span<byte> destination)
            => RenderNext(frameIndex, ReadOnlySpan<byte>.Empty, destination);

        internal void RenderNext(
            long frameIndex,
            ReadOnlySpan<byte> scopeGrid,
            Span<byte> destination)
        {
            if (_owner._scopeFrames is null)
            {
                _overlay.RenderNext(frameIndex, ReadOnlySpan<byte>.Empty, destination);
                return;
            }

            if (scopeGrid.IsEmpty)
            {
                _owner.ReadScopeFrame(frameIndex, _owner._scopeBuffer!);
                scopeGrid = _owner._scopeBuffer;
            }
            else if (scopeGrid.Length < _owner.ScopeFrameByteCount)
            {
                throw new ArgumentException(
                    $"Scope grid requires at least {_owner.ScopeFrameByteCount} bytes.",
                    nameof(scopeGrid));
            }

            _overlay.RenderNext(frameIndex, scopeGrid, destination);
        }
    }
}
