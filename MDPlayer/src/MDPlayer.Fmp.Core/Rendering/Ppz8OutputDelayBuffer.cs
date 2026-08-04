namespace Fmp.Core.Rendering;

/// <summary>
/// Fixed-length delay line for the PPZ8 output stream. The native OPNA
/// device's resampler reports a fixed output latency (the frames of output in
/// flight); the PPZ8 render must be delayed by exactly that many frames so
/// both streams reach the mixer aligned. Pure fixed-capacity ring buffer with
/// no allocation after construction.
/// </summary>
internal sealed class Ppz8OutputDelayBuffer
{
    private readonly short[] _left;
    private readonly short[] _right;
    private readonly int _capacity;
    private int _writeIndex;

    public Ppz8OutputDelayBuffer(int latencyFrames)
    {
        if (latencyFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(latencyFrames));
        _capacity = Math.Max(1, latencyFrames);
        _left = new short[_capacity];
        _right = new short[_capacity];
        _writeIndex = 0;
    }

    public int Capacity => _capacity;

    /// <summary>
    /// Pushes one frame of PPZ8 output and returns the frame that exits the
    /// delay line (the value pushed <see cref="Capacity"/> frames ago, or
    /// silence while the line is still filling).
    /// </summary>
    public void Push(short left, short right, out short delayedLeft, out short delayedRight)
    {
        delayedLeft = _left[_writeIndex];
        delayedRight = _right[_writeIndex];
        _left[_writeIndex] = left;
        _right[_writeIndex] = right;
        _writeIndex = (_writeIndex + 1) % _capacity;
    }

    public void Reset()
    {
        Array.Clear(_left, 0, _left.Length);
        Array.Clear(_right, 0, _right.Length);
        _writeIndex = 0;
    }
}