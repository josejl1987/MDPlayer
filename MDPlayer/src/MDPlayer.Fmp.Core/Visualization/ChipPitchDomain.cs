namespace Fmp.Core.Visualization;

/// <summary>
/// Classifies whether a decoded chip pitch is a representable musical pitch that
/// may become a MIDI note (INV5 classifier). A pitch is NOT representable when it
/// is not finite, when the tone generator is disabled or in its initialization
/// state (period &lt;= 0, decoded as the Unpitched sentinel), or when it decodes to
/// an ultrasonic frequency — the "meaningless source state" class that must never
/// become an audible MIDI note (e.g. AY8910 period 1..11 or K051649 period 1..2
/// decode above MIDI note 127, beyond the MIDI pitch domain).
/// </summary>
internal static class ChipPitchDomain
{
    /// <summary>
    /// Frequency ceiling of a representable MIDI pitch: MIDI 127 (≈13.29 kHz).
    /// Higher decoded pitches cannot be represented by the MIDI note domain and
    /// are initialization/ultrasonic states for this export pipeline.
    /// </summary>
    public const double MaxFrequencyHz = 13_289.754117744523;

    /// <summary>
    /// True when the decoded pitch is a stable, enabled, representable musical
    /// pitch: finite, a positive frequency, within the audible ceiling, and not
    /// the Unpitched sentinel. Notes whose pitch fails this test must NOT be
    /// created by a chip decoder.
    /// </summary>
    public static bool IsRepresentable(double frequencyHz, double midiNote) =>
        double.IsFinite(midiNote)
        && double.IsFinite(frequencyHz)
        && frequencyHz > 0
        && frequencyHz <= MaxFrequencyHz;
}
