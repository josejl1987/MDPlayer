namespace Fmp.Core.Rendering;

/// <summary>
/// Thrown when a native FMP render loop makes no semantic progress during one
/// iteration: none of the authoritative machine cycles, the OPNA master clock,
/// the output-frame position or the driver state changed. The message carries
/// exactly those before/after values so the caller can diagnose the stuck
/// iteration. Never retried and never slept through — the loop must detect
/// no-progress immediately and stop.
/// </summary>
internal sealed class NativeFmpNoProgressException : InvalidOperationException
{
    public NativeFmpNoProgressException(string message)
        : base(message)
    {
    }
}