namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Per-channel energy envelope derived from stem WAV files (Visualization 2.0
/// §6.4). One entry per output frame. Used for scope-border brightness, active
/// panel accent, note brightness modulation, and rhythm impact strength.
/// <para>
/// Envelopes are computed once after stem export and passed to the renderer;
/// they are never serialized to <c>timeline.json</c>.
/// </para>
/// </summary>
internal sealed class ChannelEnergyEnvelope
{
    public required string ChannelId { get; init; }

    /// <summary>
    /// Per-frame peak amplitude, normalized to [0, 1]. The maximum absolute
    /// sample value in the frame's audio range, divided by 32767.
    /// </summary>
    public required float[] FramePeak { get; init; }

    /// <summary>
    /// Per-frame RMS amplitude, normalized to [0, 1]. The square root of the
    /// mean squared sample value, divided by full-scale 16-bit amplitude
    /// (32767). Clamped to [0, 1] before perceptual normalization.
    /// </summary>
    public required float[] FrameRms { get; init; }

    /// <summary>Perceptual, smoothed activity in [0,1].</summary>
    public float[] FrameActivity { get; init; } = Array.Empty<float>();
}
