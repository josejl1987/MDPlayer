#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The single canonical frame↔sample clock for every visualization consumer.
///
/// Video frame N corresponds to the audio sample
/// <c>round(N * sampleRate * fpsDenominator / fpsNumerator)</c> relative to
/// the timeline start, computed in exact decimal arithmetic so fractional
/// frame rates (e.g. 60000/1001 ≈ 59.94) never accumulate floating-point
/// rounding error.
///
/// Note contact, rhythm contact, pitch evaluation, animation age, waveform
/// lookup, pitch-camera state and total frame count must all derive from this
/// one mapping. Nothing in the render path may keep a parallel seconds-based
/// approximation of the clock: a note whose StartSample equals
/// <see cref="SampleAtFrame"/>(f) must touch the playhead exactly at frame f,
/// which is what keeps the visual stream locked to the master WAV.
/// </summary>
internal static class FrameSampleClock
{
    /// <summary>
    /// Audio sample (relative to the timeline start) played at frame
    /// <paramref name="frameIndex"/>.
    /// </summary>
    public static long SampleAtFrame(long frameIndex, int sampleRate, int fpsNumerator, int fpsDenominator)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fpsNumerator <= 0 || fpsDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNumerator));

        decimal sample = (decimal)frameIndex * sampleRate * fpsDenominator / fpsNumerator;
        return (long)decimal.Round(sample, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Total number of frames needed to cover <paramref name="totalSamples"/>
    /// (ceiling), using the same exact arithmetic as <see cref="SampleAtFrame"/>
    /// so the last frame never overshoots the audio into a phantom range.
    /// </summary>
    public static long FrameCount(long totalSamples, int sampleRate, int fpsNumerator, int fpsDenominator)
    {
        if (totalSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(totalSamples));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fpsNumerator <= 0 || fpsDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNumerator));

        decimal frames = (decimal)totalSamples * fpsNumerator / ((decimal)sampleRate * fpsDenominator);
        return (long)decimal.Ceiling(frames);
    }
}
