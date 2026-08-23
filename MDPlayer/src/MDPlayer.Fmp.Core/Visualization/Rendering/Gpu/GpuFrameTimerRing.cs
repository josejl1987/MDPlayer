#nullable enable

using System;
using OpenTK.Graphics.OpenGL;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Non-blocking, delayed GPU frame-time measurement for the OpenGL renderer.
/// Ganesh defers command execution until flush/submit, so whole-frame query
/// timestamps are the honest GPU measurement without introducing a readback.
/// </summary>
internal sealed class GpuFrameTimerRing
{
    private const int Depth = 8;

    private readonly int[] _startQuery = new int[Depth];
    private readonly int[] _endQuery = new int[Depth];
    private readonly bool[] _pending = new bool[Depth];
    private int _head;
    private bool _initialized;
    private bool _supported = true;
    private long _frameNanos;

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

    public void BeginFrame()
    {
        if (!_supported)
            return;
        EnsureInitialized();
        if (!_supported)
            return;

        int write = _head;
        TryRetire(write);
        _pending[write] = false;
        GL.QueryCounter(_startQuery[write], QueryCounterTarget.Timestamp);
        _pending[write] = true;
        _head = (write + 1) % Depth;
    }

    public void EndFrame()
    {
        if (!_supported)
            return;
        int previous = (_head - 1 + Depth) % Depth;
        if (_pending[previous])
            GL.QueryCounter(_endQuery[previous], QueryCounterTarget.Timestamp);
    }

    public long DrainFrameNanos()
    {
        long nanos = _frameNanos;
        _frameNanos = 0;
        return nanos;
    }

    private void TryRetire(int slot)
    {
        if (!_pending[slot])
            return;

        GL.GetQueryObject(_startQuery[slot], GetQueryObjectParam.QueryResultAvailable, out int startAvailable);
        GL.GetQueryObject(_endQuery[slot], GetQueryObjectParam.QueryResultAvailable, out int endAvailable);
        if (startAvailable == 0 || endAvailable == 0)
            return;

        GL.GetQueryObject(_startQuery[slot], GetQueryObjectParam.QueryResult, out long start);
        GL.GetQueryObject(_endQuery[slot], GetQueryObjectParam.QueryResult, out long end);
        _frameNanos += Math.Max(0, end - start);
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
            // Teardown is best effort.
        }
    }
}
