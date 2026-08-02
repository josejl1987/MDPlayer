using System.Threading;

namespace Fmp.Application.Preview;

/// <summary>
/// A held lease over a published reusable preview capture bundle. While a lease
/// is held the owning session keeps the capture workspace alive; releasing the
/// last lease lets the session delete it during disposal.
/// </summary>
public sealed class ReusableCaptureLease : IAsyncDisposable
{
    private readonly Action _release;
    private int _released;

    public ReusableCaptureLease(string directoryPath, string captureKey, Action release)
    {
        DirectoryPath = directoryPath;
        CaptureKey = captureKey;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>Absolute path to the published bundle root.</summary>
    public string DirectoryPath { get; }

    /// <summary>Deterministic capture key of the bundle.</summary>
    public string CaptureKey { get; }

    /// <summary>
    /// Releases the lease. The release callback runs at most once even if this
    /// method is called repeatedly or from concurrent callers.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _release();
        return ValueTask.CompletedTask;
    }
}