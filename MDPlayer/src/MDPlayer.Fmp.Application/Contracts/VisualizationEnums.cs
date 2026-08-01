namespace Fmp.Application.Contracts;

/// <summary>
/// Canonical publishing preset. These values are the single source of truth;
/// the CLI and GUI MUST both resolve defaults through
/// <see cref="Presets.VisualizationPresetCatalog"/>. "Custom" is a GUI-only
/// display state (a request whose values differ from any named preset); it is
/// never persisted as a preset selector value.
/// </summary>
public enum VisualizationPreset
{
    Preview,
    Balanced,
    Final,
    Diagnostic,
}

/// <summary>
/// Layout family requested by the user. <see cref="Auto"/> resolves to a
/// concrete layout from the captured timeline; every other value is explicit.
/// <see cref="Performance"/>, <see cref="ScopeStage"/> and <see cref="Diagnostic"/>
/// are the three canonical publishing compositions; the remaining values are
/// legacy aliases retained for back-compatibility.
/// </summary>
public enum VisualizationLayout
{
    Auto,
    UnifiedRoll,
    SplitRoll,
    Scopes,
    Hybrid,
    Diagnostic,
    LegacyDiagnostic,
    Performance,
    ScopeStage,
}

/// <summary>
/// Channel/track inclusion policy. <see cref="Custom"/> uses the explicit
/// included/excluded track id lists from the request.
/// </summary>
public enum ChannelSelectionMode
{
    Active,
    Audible,
    Semantic,
    All,
    Custom,
}

/// <summary>Dynamic effect preset (visual density of overlays).</summary>
public enum VisualizationEffects
{
    None,
    Minimal,
    Diagnostic,
    Cinematic,
}

/// <summary>Note coloring strategy.</summary>
public enum NoteColorMode
{
    Instrument,
    Channel,
    PitchClass,
}

/// <summary>Scope wall placement inside the composition.</summary>
public enum ScopePosition
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Panel grouping policy for channel panels.</summary>
public enum TrackGroupingMode
{
    None,
    Device,
    Family,
}

/// <summary>Time-grid source for the composition.</summary>
public enum TimeGridMode
{
    None,
    Automatic,
    Authoritative,
    Analytical,
}

/// <summary>Symbolic analysis depth.</summary>
public enum AnalysisDetail
{
    Minimal,
    Standard,
    Full,
}

/// <summary>Analysis overlay density.</summary>
public enum AnalysisOverlayMode
{
    None,
    Minimal,
    Standard,
    Full,
}

/// <summary>Video encoder selection policy.</summary>
public enum VideoEncoder
{
    Auto,
    LibX264,
    Nvenc,
}

/// <summary>Playback backend preference.</summary>
public enum BackendPreference
{
    Auto,
    Fmp,
    Mdplayer,
}

/// <summary>Generic backend scope mode.</summary>
public enum ScopeMode
{
    Auto,
    Master,
    Device,
    Channel,
    Off,
}

/// <summary>SPC diagnostic pitch handling.</summary>
public enum SpcPitchMode
{
    Estimate,
    Relative,
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
