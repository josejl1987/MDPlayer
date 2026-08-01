namespace Fmp.Core.Visualization.Rendering;

/// <summary>Controls restrained versus diagnostic/cinematic attack effects.</summary>
internal enum EffectsMode
{
    /// <summary>Onset cap and active pitch marker only.</summary>
    Minimal,

    /// <summary>Diagnostic attack/release semantics without cinematic bloom.</summary>
    Diagnostic,

    /// <summary>One restrained contact effect in addition to the musical marker.</summary>
    Cinematic,

    /// <summary>Legacy opt-in for the original double-ripple effect.</summary>
    All,

    /// <summary>
    /// Disable ripples, active-flash expansion, and energy-linked glow.
    /// Retains note ribbons, onset caps, end caps, pitch bend, the contact
    /// rail, and channel textures.
    /// </summary>
    None,
}
