namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Visual panel kind for the 3x4 panel grid.
/// </summary>
internal enum PreparedPanelKind
{
    /// <summary>Generic activity lane for an unsupported specialized presentation.</summary>
    Generic,
    /// <summary>FM pitched channel (panels 0,1,3,4,5).</summary>
    Pitched,
    /// <summary>FM3 special panel with operator lanes (panel 2).</summary>
    Fm3,
    /// <summary>SSG noise/tone channel (panels 6,7,8).</summary>
    Ssg,
    /// <summary>Rhythm/percussion lane (panel 9).</summary>
    Rhythm,
    /// <summary>Wavetable voice: pitched ribbon plus a cyclic waveform viewport.</summary>
    Wavetable,
    /// <summary>PCM sample voice: sample identity, envelope, playback cursor and
    /// loop region.</summary>
    PcmVoice,
    /// <summary>Dedicated noise voice: activity and spectral position without a
    /// chromatic pitch lane.</summary>
    Noise,
    /// <summary>Grouped event-driven voice rendered as a stable pad grid.</summary>
    Aggregate,
    /// <summary>True fallback: reserved voices, voices without decoder data, or a
    /// presentation kind no built-in renderer understands.</summary>
    Placeholder,
}
