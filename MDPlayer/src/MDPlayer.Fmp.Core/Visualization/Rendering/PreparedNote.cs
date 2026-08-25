using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// A note prepared for CPU overlay rendering. Colors are pre-resolved at
/// preparation time so the per-frame hot path performs no color calculation
/// or dictionary lookup; pitch points are sorted and ready for binary search.
/// </summary>
internal sealed class PreparedNote
{
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public double InitialMidiNote { get; init; }

    /// <summary>Pitch state at the note attack, prepared once for rasterization.</summary>
    public double StartMidiNote { get; init; } = double.NaN;

    /// <summary>Pitch state at the note end, prepared once for rasterization.</summary>
    public double EndMidiNote { get; init; } = double.NaN;

    public VisualizationNoteMode Mode { get; init; }

    /// <summary>Instrument ID, retained for header/instrument formatting.</summary>
    public required string InstrumentId { get; init; }

    /// <summary>
    /// Stable sample identity for pitched sample playback (BRR source on an
    /// S-DSP voice). Metadata attached to the pitched note — it never
    /// determines vertical position.
    /// </summary>
    public string? SampleId { get; init; }

    /// <summary>
    /// Compact display label for <see cref="SampleId"/> (e.g. "BRR 02df5"),
    /// pre-resolved so the per-frame hot path performs no dictionary lookup.
    /// Null when the note carries no sample identity.
    /// </summary>
    public string? SampleDisplayLabel { get; init; }

    /// <summary>Prepared header strings; populated by the scene builder.</summary>
    public PreparedInstrumentText Text { get; init; } = PreparedInstrumentText.Empty;

    /// <summary>True if this note is a retrigger of a still-sounding pitch.</summary>
    public bool IsRetrigger { get; init; }

    /// <summary>
    /// True when this note introduces an instrument change (§13.2): its
    /// InstrumentId differs from the previous note on the same channel.
    /// </summary>
    public bool HasInstrumentChange { get; init; }

    /// <summary>
    /// Sample position at which the instrument changed (§13.2), or 0 when
    /// this note does not introduce a change. The renderer shows the
    /// ALG/FB/AMS/PMS overlay + operator bars for 800 ms after this sample.
    /// </summary>
    public long InstrumentChangeSample { get; init; }

    /// <summary>
    /// True when the panel's onset density around this note's start exceeds
    /// the §9.3 threshold (more than 8 onsets within 100 ms). Dense onsets
    /// suppress ripple alpha and the active-flash size enlargement but never
    /// the onset caps. Resolved once at preparation time.
    /// </summary>
    public bool IsDenseOnset { get; init; }

    /// <summary>
    /// Release style (§8.6), resolved at preparation time from the successor
    /// note on the same channel. Hard key-offs render a flat end cap instead
    /// of the normal release taper.
    /// </summary>
    public NoteReleaseStyle ReleaseStyle { get; init; } = NoteReleaseStyle.Normal;

    /// <summary>Pre-resolved fill color for the normal (non-active) state.</summary>
    public required OverlayColor Fill { get; init; }

    /// <summary>Pre-resolved fill color for the active (playhead) state.</summary>
    public required OverlayColor ActiveFill { get; init; }

    /// <summary>
    /// Pre-resolved bright onset/end-cap fill (§8.5): brighter than the body,
    /// always full opacity, one-pixel accent border applied by the renderer.
    /// </summary>
    public required OverlayColor CapFill { get; init; }

    /// <summary>Pre-resolved accent border color.</summary>
    public required OverlayColor Accent { get; init; }

    /// <summary>Sorted pitch contour points.</summary>
    public required PreparedPitchPoint[] Pitch { get; init; }
}
