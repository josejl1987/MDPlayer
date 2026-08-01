namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// A single panel prepared for CPU overlay rendering. Notes and rhythm
/// events are pre-sorted with binary-searchable arrays; colors are
/// pre-resolved. Built once from the visualization timeline by
/// <see cref="OverlaySceneBuilder"/> and consumed read-only by
/// <see cref="PanelOverlayRenderer"/>.
/// </summary>
internal sealed class PreparedPanel
{
    public int Index { get; init; }
    public required string Id { get; init; }
    public required string Label { get; init; }
    public PreparedPanelKind Kind { get; init; }

    /// <summary>Main channel notes, sorted by StartSample.</summary>
    public required PreparedNote[] MainNotes { get; init; }

    /// <summary>Notes that introduce an instrument change, sorted by onset.</summary>
    public required PreparedNote[] InstrumentChanges { get; init; }

    /// <summary>FM3 operator notes (4 lanes). Empty for non-FM3 panels.</summary>
    public required PreparedNote[][] OperatorNotes { get; init; }

    /// <summary>Main plus operator notes used by the FM3 camera.</summary>
    public required PreparedNote[] CameraNotes { get; init; }

    /// <summary>Rhythm events, sorted by SamplePosition.</summary>
    public required PreparedRhythmEvent[] Rhythm { get; init; }

    /// <summary>Prepared PPZ8 sample events for the aggregate eight-lane panel.</summary>
    public required Ppz8Event[] Ppz8 { get; init; }

    /// <summary>Prepared YM2608 ADPCM-B sample events.</summary>
    public required AdpcmBEvent[] AdpcmB { get; init; }

    /// <summary>Visible pitch range (MIDI note values).</summary>
    public double MinMidi { get; init; }
    public double MaxMidi { get; init; }

    /// <summary>Pre-resolved accent color for this panel.</summary>
    public OverlayColor Accent { get; init; }

    /// <summary>
    /// True if the panel has any decodable track or sample events.
    /// Placeholder panels use this to distinguish silence/no data from an
    /// audio stem whose event semantics remain unknown.
    /// </summary>
    public bool HasTrackEvents { get; init; }
}
