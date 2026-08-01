namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Note colour assignment mode (§12.1). Determines how each note's fill colour
/// is resolved at preparation time. The renderer never switches modes per frame.
/// </summary>
internal enum NoteColorMode
{
    /// <summary>Default (§12.2): hue derived from InstrumentDefinition.Id.</summary>
    Instrument,
    /// <summary>§12.3: 12 stable pitch-class colours; octaves share hue.</summary>
    Pitch,
    /// <summary>§12.4: every panel uses one stable accent colour.</summary>
    Channel,
}
