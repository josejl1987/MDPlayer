#nullable enable

using System;
using OpenTK.Graphics.OpenGL;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Non-blocking, delayed GPU frame-time measurement for the OpenGL renderer.
///
/// Ganesh defers and batches GPU command execution until <c>Flush</c>/<c>Submit</c>,
/// so <see cref="OpenTK.Graphics.OpenGL.GL_TIME_ELAPSED"/> brackets drawn around
/// individual Skia phase calls (grid, scopes, notes, text) measure only the CPU
/// recording side — not GPU execution. The single honest whole-frame GPU figure is
/// a <see cref="QueryCounterTarget.Timestamp"/> pair bracketing [flush/submit ..
/// readback enqueue]. That delta is real GPU time to render + copy the frame, and
/// it is exactly the "is the renderer GPU-bound or readback-bound?" number the
/// architecture review needs.
///
/// Timestamps are enqueued into the GL command stream and read back several
/// frames later, so retrieving them never stalls the GPU (the whole point of a
/// delayed ring). Retrieval is guarded by <c>QueryResultAvailable</c> so a result
/// that has not landed yet is simply skipped rather than blocking.
/// </summary>
internal sealed class GpuFrameTimerRing
{
    // Depth frames of in-flight timestamps: enough for the GPU to have consumed a
    // frame's command stream well before we read its results back.
    private const int Depth = 8;

    private readonly int[] _startQuery = new int[Depth];
    private readonly int[] _endQuery = new int[Depth];
    private readonly bool[] _pending = new bool[Depth];
    private int _head;
    private bool _initialized;
    private bool _supported = true;
    private long _frameNanos;

    /// <summary>Total GPU frame time accumulated so far (nanoseconds, best-effort).</summary>
    public long FrameNanos => _frameNanos;

    private void EnsureInitialized()
    {
        if (_initialized)
            return;
        _initialized = true;
        try
        {
            GL.GenQueries(Depth, _startQuery);
            GL.GenQueries(Depth, _endQuery);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: GPU timestamp queries unavailable, disabling GPU timers: {ex.Message}");
            _supported = false;
        }
    }

    /// <summary>
    /// Starts the current frame's GPU timing window. Must be called with the GL
    /// context current, immediately before the frame's flush/submit. As a side
    /// effect it retires the oldest in-flight slot (Depth frames ago).
    /// </summary>
    public void BeginFrame()
    {
        if (!_supported)
            return;
        EnsureInitialized();
        if (!_supported)
            return;

        int write = _head;
        TryRetire(write);
        // A stale pending flag here means a previous frame never called EndFrame
        // (e.g. a benchmark mode that skipped readback). Clear it so we don't
        // inherit a half-open window.
        _pending[write] = false;

        GL.QueryCounter(_startQuery[write], QueryCounterTarget.Timestamp);
        _pending[write] = true;
        _head = (write + 1) % Depth;
    }

    /// <summary>
    /// Closes the current frame's GPU timing window. Must be called with the GL
    /// context current, after the frame's submit/readback-enqueue has been issued
    /// so the end timestamp executes after the GPU finishes the frame's work.
    /// No-op when the previous <see cref="BeginFrame"/> never recorded.
    /// </summary>
    public void EndFrame()
    {
        if (!_supported)
            return;
        int prev = (_head - 1 + Depth) % Depth;
        if (!_pending[prev])
            return;
        GL.QueryCounter(_endQuery[prev], QueryCounterTarget.Timestamp);
    }

    /// <summary>
    /// Returns the total nanos accumulated so far and resets the accumulator.
    /// Slots are retired from <see cref="BeginFrame"/> (which runs with the GL
    /// context current), so this never touches GL and may be called after the
    /// context has been released.
    /// </summary>
    public long DrainFrameNanos()
    {
        long n = _frameNanos;
        _frameNanos = 0;
        return n;
    }

    private void TryRetire(int slot)
    {
        if (!_pending[slot])
            return;

        GL.GetQueryObject(_startQuery[slot], GetQueryObjectParam.QueryResultAvailable, out int availStart);
        GL.GetQueryObject(_endQuery[slot], GetQueryObjectParam.QueryResultAvailable, out int availEnd);
        if (availStart == 0 || availEnd == 0)
            return; // GPU has not reached both timestamps yet; leave pending.

        GL.GetQueryObject(_startQuery[slot], GetQueryObjectParam.QueryResult, out long t0);
        GL.GetQueryObject(_endQuery[slot], GetQueryObjectParam.QueryResult, out long t1);
        _frameNanos += Math.Max(0, t1 - t0);
        _pending[slot] = false;
    }

    public void Dispose()
    {
        if (!_initialized || !_supported)
            return;
        try
        {
            GL.DeleteQueries(Depth, _startQuery);
            GL.DeleteQueries(Depth, _endQuery);
        }
        catch
        {
            // Teardown is best-effort.
        }
    }
}