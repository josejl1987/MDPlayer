namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Controls decorative active-note effects (Visualization 2.0 §9, §19.3).
/// <para>
/// <see cref="All"/> renders the 120-ms active flash and the 220-ms onset
/// ripple (density-limited per §9.3). <see cref="None"/> is a diagnostic/
/// performance option that removes only those decorative effects — note
/// ribbons, onset caps, end caps, pitch bend, the contact rail, and channel
/// textures all remain.
/// </para>
/// </summary>
internal enum EffectsMode
{
    /// <summary>Render active flash and onset ripples (default).</summary>
    All,

    /// <summary>
    /// Disable ripples, active-flash expansion, and energy-linked glow.
    /// Retains note ribbons, onset caps, end caps, pitch bend, the contact
    /// rail, and channel textures.
    /// </summary>
    None,
}
