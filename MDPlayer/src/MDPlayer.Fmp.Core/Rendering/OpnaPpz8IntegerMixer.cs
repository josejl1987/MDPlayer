namespace Fmp.Core.Rendering;

/// <summary>
/// Pure-integer final mixer for the native LLE path: one OPNA frame (drained
/// from the native device, already in 16-bit range) plus one PPZ8 frame
/// (rendered by the shared MDSound PPZ8 renderer), both after the fixed output
/// latency. The arithmetic is defined exactly — no floating point, no
/// platform-dependent rounding:
///
///   left  = Saturate( opnaL + ((ppz8L * Gain) &gt;&gt; 16) + RoundBias )
///   right = Saturate( opnaR + ((ppz8R * Gain) &gt;&gt; 16) + RoundBias )
///
/// with <c>RoundBias = 0x8000</c> giving round-half-up on the 16-bit gain
/// multiply and <c>Saturate</c> clamping to [-32768, 32767].
/// </summary>
internal sealed class OpnaPpz8IntegerMixer
{
    private const int MinSample = -32768;
    private const int MaxSample = 32767;

    /// <summary>
    /// PPZ8 gain applied as <c>(ppz8 * Gain) &gt;&gt; 16</c>. 65536 means unity.
    /// </summary>
    public int Ppz8Gain { get; }

    public OpnaPpz8IntegerMixer(int ppz8Gain = 65536)
    {
        if (ppz8Gain < 0)
            throw new ArgumentOutOfRangeException(nameof(ppz8Gain));
        Ppz8Gain = ppz8Gain;
    }

    /// <summary>
    /// Mixes one stereo frame. OPNA input is already in 16-bit range; PPZ8
    /// input is the raw integer output of the PPZ8 renderer.
    /// </summary>
    public void Mix(int opnaLeft, int opnaRight, int ppz8Left, int ppz8Right, out short left, out short right)
    {
        left = Saturate(opnaLeft + Scale(ppz8Left));
        right = Saturate(opnaRight + Scale(ppz8Right));
    }

    private int Scale(int value)
    {
        long scaled = (long)value * Ppz8Gain;
        // round-half-up at bit 15, then arithmetic shift right by 16.
        long rounded = (scaled + 0x8000) >> 16;
        return (int)Math.Clamp(rounded, MinSample, MaxSample);
    }

    private static short Saturate(int value) => (short)Math.Clamp(value, MinSample, MaxSample);
}