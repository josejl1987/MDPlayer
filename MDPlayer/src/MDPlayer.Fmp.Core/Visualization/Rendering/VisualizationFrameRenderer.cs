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
    private readonly PanelOverlayRenderer _overlay;
    private readonly IScopeFrameSource? _scopeFrames;
    private readonly byte[]? _scopeBuffer;
    private bool _disposed;

    public VisualizationFrameRenderer(
        PanelOverlayRenderer overlay,
        IScopeFrameSource? scopeFrames)
    {
        _overlay = overlay
            ?? throw new ArgumentNullException(nameof(overlay));
        _scopeFrames = scopeFrames;

        if (_scopeFrames is not null)
            _scopeBuffer = new byte[_overlay.ScopeFrameByteCount];
    }

    public int Width => _overlay.Width;
    public int Height => _overlay.Height;
    public int FrameByteCount => _overlay.FrameByteCount;
    public long TotalFrames => _overlay.TotalFrames;
    public int OverlayFpsNumerator => _overlay.FpsNumerator;
    public int OverlayFpsDenominator => _overlay.FpsDenominator;

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

        _scopeFrames.ReadFrame(
            checked((int)frameIndex),
            _scopeBuffer!);

        _overlay.RenderCompositeFrame(
            frameIndex,
            _scopeBuffer!,
            destination);
    }

    public byte[] RenderFrame(long frameIndex)
    {
        byte[] frame = new byte[FrameByteCount];
        RenderFrame(frameIndex, frame);
        return frame;
    }

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
}