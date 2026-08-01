namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// How a note's ending is rendered (Visualization 2.0 §8.6). Resolved once at
/// preparation time so the per-frame hot path performs no classification.
/// </summary>
internal enum NoteReleaseStyle
{
    /// <summary>Taper the final 40–80 ms of the ribbon.</summary>
    Normal,

    /// <summary>
    /// Flat high-contrast end cap: the note was keyed off abruptly because the
    /// next note on the same channel begins within one output frame.
    /// </summary>
    HardKeyOff,
}
