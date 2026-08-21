namespace Fmp.Application.Contracts;

/// <summary>
/// The public publishing composition. The request carries the composition
/// explicitly and never auto-resolves it.
/// </summary>
public enum CompositionKind
{
    /// <summary>Technical inspection output: a semantic channel grid.</summary>
    Diagnostic,

    /// <summary>
    /// Native performance visualization: the semantic timeline rendered
    /// in-process (note ribbons, rhythm lane, PCM events) on a balanced
    /// active-panel layout. Previously named <c>MidiTrail</c>; that spelling
    /// remains accepted as a CLI/JSON compatibility alias.
    /// </summary>
    Performance,
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
/// YM2608 (OPNA) audio backend for FMP-family capture. Serialized as the
/// lowercase strings "mdsound" / "native-audio". The default (0) is MDSound, so
/// old requests/configuration without the field keep selecting MDSound. There
/// is deliberately no "auto": the value is always explicit and native audio
/// never falls back to MDSound.
/// </summary>
public enum FmpOpnaBackend
{
    /// <summary>Existing trace-driven MDSound host-PCM capture (default, unchanged byte-for-byte).</summary>
    Mdsound = 0,

    /// <summary>Trace-driven native YM2608 audio replay; never falls back.</summary>
    NativeAudio = 1,
}

/// <summary>
/// Preview fidelity levels. Layout preview is cheapest; timeline still is a
/// dynamic semantic frame built only from the captured timeline; interactive
/// still is a random-access still optimized for timeline scrubbing; accurate
/// still uses the final renderer at the selected frame; motion preview is a
/// reduced resolution/frame-rate looping sequence.
/// </summary>
public enum PreviewFidelity
{
    Layout,

    /// <summary>
    /// Dynamic semantic frame built from the captured timeline only. Channel
    /// stems and channel-energy analysis are not required.
    /// </summary>
    TimelineStill,

    /// <summary>
    /// Random-access still optimized for interactive timeline movement.
    /// Semantic rendering is exact; scope triggering is approximated from the
    /// captured channel WAVs.
    /// </summary>
    InteractiveStill,

    /// <summary>
    /// Uses the same stateful scope and frame path as production output.
    /// May be expensive for arbitrary seeks.
    /// </summary>
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