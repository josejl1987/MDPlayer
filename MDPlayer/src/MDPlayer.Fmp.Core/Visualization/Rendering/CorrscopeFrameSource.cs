using System.Diagnostics;
using System.IO;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Reusable forward-reading scope frame source over a Corrscope raw-frame
/// bridge process. Frames are read sequentially and efficiently; a backward
/// seek (a frame index lower than the read position) simply restarts the
/// bridge process and reads forward again. This is correct (never throws on a
/// seek) at the cost of restart overhead on aggressive GUI scrubbing.
///
/// A single discard buffer is shared for skipped frames so consuming
/// everything up to a late frame never hoards per-frame allocations.
/// </summary>
internal sealed class CorrscopeFrameSource : IScopeFrameSource
{
    private readonly Func<Process> _startProcess;
    private readonly int _frameByteCount;

    private Process? _process;
    private Stream? _output;
    private Task<string>? _error;
    private int _nextFrame;
    private byte[]? _discardBuffer;
    private bool _disposed;

    public bool FramesAreOpaque => true;

    public CorrscopeFrameSource(
        Func<Process> startProcess,
        int frameByteCount)
    {
        _startProcess = startProcess
            ?? throw new ArgumentNullException(nameof(startProcess));
        if (frameByteCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameByteCount));
        _frameByteCount = frameByteCount;
        Start();
    }

    /// <summary>
    /// Reads the scope grid frame for <paramref name="frameIndex"/> into
    /// <paramref name="destination"/>. Reads forward from the current position;
    /// if the requested frame precedes the read position the bridge process is
    /// restarted and frames are read forward again.
    /// </summary>
    public void ReadFrame(int frameIndex, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (destination.Length < _frameByteCount)
            throw new ArgumentException(
                "scope frame destination is too small",
                nameof(destination));

        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        if (frameIndex < _nextFrame)
            Restart();

        while (_nextFrame <= frameIndex)
        {
            Span<byte> target =
                _nextFrame == frameIndex
                    ? destination[.._frameByteCount]
                    : GetDiscardBuffer();

            ReadExactly(target);
            _nextFrame++;
        }
    }

    private byte[] GetDiscardBuffer()
        => _discardBuffer ??= new byte[_frameByteCount];

    private void Restart()
    {
        Stop();
        Start();
    }

    private void Start()
    {
        _process = _startProcess();
        if (_process is null)
            throw new InvalidOperationException(
                "scope process factory returned null.");
        if (_process.StandardOutput is null)
            throw new InvalidOperationException(
                "scope process does not redirect standard output.");
        _output = _process.StandardOutput.BaseStream;
        _error = _process.StandardError.ReadToEndAsync();
        _nextFrame = 0;
    }

    private void Stop()
    {
        if (_process is null)
            return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort termination.
        }
        try
        {
            _process.StandardOutput.Close();
        }
        catch
        {
            // Already closed.
        }
        try
        {
            _process.Dispose();
        }
        catch
        {
            // Already disposed.
        }
        _process = null;
        _output = null;
        _error = null;
    }

    private void ReadExactly(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _output!.Read(destination.Slice(total));
            if (read <= 0)
            {
                string error = _error?.GetAwaiter().GetResult() ?? string.Empty;
                int? exit = SafeExitCode();
                throw new InvalidOperationException(
                    $"Corrscope frame bridge ended early at frame {_nextFrame}. " +
                    (exit is null
                        ? error.Trim()
                        : $"exit {exit}. {error.Trim()}"));
            }
            total += read;
        }
    }

    private int? SafeExitCode()
    {
        try
        {
            if (_process is { HasExited: true })
                return _process.ExitCode;
        }
        catch
        {
            // Unavailable.
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
