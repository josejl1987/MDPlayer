namespace Fmp.Application.Contracts;

/// <summary>
/// The public publishing composition. Currently exactly one: Diagnostic
/// (technical inspection output: a semantic channel grid). Future layouts can
/// be added back as additional values; the request contract carries the
/// composition explicitly and never auto-resolves it.
/// </summary>
public enum CompositionKind
{
    /// <summary>Technical inspection output: a semantic channel grid.</summary>
    Diagnostic,
}

/// <summary>Output quality profile. Quality selects resolution/fps/encoding, never the composition.</summary>
public enum RenderQuality
{
    Draft,
    Standard,
    Final,
}

/// <summary>Track selection policy (active by default).</summary>
public enum TrackSelectionMode
{
    Active,
    All,
    Custom,
}

/// <summary>Structural-analysis overlay policy.</summary>
public enum StructureOverlayMode
{
    Off,
    Automatic,
}

/// <summary>Visual-effect intensity. Subtle is the default for Performance.</summary>
public enum VisualEffects
{
    Off,
    Subtle,
    Cinematic,
}

/// <summary>Note coloring strategy (retained concept from the old contract).</summary>
public enum NoteColorMode
{
    Instrument,
    Channel,
    PitchClass,
}

/// <summary>Time grid source for the composition.</summary>
public enum TimeGridMode
{
    None,
    Automatic,
    Authoritative,
    Analytical,
}

/// <summary>Visualization palette family.</summary>
public enum PaletteKind
{
    Default,
    Accessible,
    Monochrome,
}

/// <summary>Video encoder selection policy.</summary>
public enum VideoEncoder
{
    Auto,
    LibX264,
    Nvenc,
}

/// <summary>
/// Preview fidelity levels. Layout preview is cheapest; accurate still uses
/// the final renderer at the selected frame; motion preview is a reduced
/// resolution/frame-rate looping sequence.
/// </summary>
public enum PreviewFidelity
{
    Layout,
    AccurateStill,
    Motion,
}

/// <summary>Severity of a validation issue.</summary>
public enum ValidationSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>Operation status for long-running operations.</summary>
public enum OperationStatus
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,
}