namespace Fmp.Core.Visualization;

/// <summary>
/// Classifies whether a decoded chip pitch is a representable musical pitch that
/// may become a MIDI note (INV5 classifier). A pitch is NOT representable when it
/// is not finite, when the tone generator is disabled or in its initialization
/// state (period &lt;= 0, decoded as the Unpitched sentinel), or when it decodes to
/// an ultrasonic frequency — the "meaningless source state" class that must never
/// become an audible MIDI note (e.g. AY8910 period 1..11 or K051649 period 1..2
/// decode to 20 kHz+, far beyond the top of the MIDI pitch domain).
/// </summary>
internal static class ChipPitchDomain
{
    /// <summary>
    /// Frequency ceiling of a representable musical pitch: ~20 kHz (≈ MIDI 135.1),
    /// the top of the audible range. Periods that decode above it are ultrasonic
    /// initialization / tone-disabled states (e.g. AY8910 period 1-11 at 3.58 MHz),
    /// never stable musical pitches. The exporter's own [0, 127] guard
    /// (<see cref="Fmp.Core.Midi.VoiceStateNormalizationStage"/>) remains the
    /// authoritative MIDI-domain bound; this ceiling is the source-side classifier
    /// that keeps such states out of the note timeline entirely.
    /// </summary>
    public const double MaxFrequencyHz = 20_000.0;

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
