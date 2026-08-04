namespace Fmp.Core.Rendering;

/// <summary>
/// Final integer mix for the native-audio path: one OPNA frame plus one PPZ8
/// frame into one signed 16-bit stereo frame. Plain signed 32-bit accumulation
/// clamped once to signed 16-bit — no averaging, no gain, no floating point.
/// </summary>
internal sealed class NativeAudioIntegerMixer
{
    public const int MinSample = -32768;
    public const int MaxSample = 32767;

    public void Mix(short opnaLeft, short opnaRight, short ppz8Left, short ppz8Right,
        out short left, out short right)
    {
        left = (short)Math.Clamp((int)opnaLeft + ppz8Left, MinSample, MaxSample);
        right = (short)Math.Clamp((int)opnaRight + ppz8Right, MinSample, MaxSample);
    }
}
