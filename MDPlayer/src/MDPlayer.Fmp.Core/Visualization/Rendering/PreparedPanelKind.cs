namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Visual panel kind for the 3x4 panel grid.
/// </summary>
internal enum PreparedPanelKind
{
    /// <summary>FM pitched channel (panels 0,1,3,4,5).</summary>
    Pitched,
    /// <summary>FM3 special panel with operator lanes (panel 2).</summary>
    Fm3,
    /// <summary>SSG noise/tone channel (panels 6,7,8).</summary>
    Ssg,
    /// <summary>Rhythm/percussion lane (panel 9).</summary>
    Rhythm,
    /// <summary>ADPCM or placeholder panel (panels 10,11).</summary>
    Placeholder,
}
