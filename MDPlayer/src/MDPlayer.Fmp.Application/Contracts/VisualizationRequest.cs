namespace Fmp.Application.Contracts;

/// <summary>
/// The one authoritative, immutable request contract shared by the CLI and the
/// GUI (final greenfield schema 2). Every UI edit produces a new snapshot;
/// long-running operations retain the snapshot with which they started. All
/// enums serialize as strings so the JSON is stable and human-readable.
/// </summary>
public sealed record VisualizationRequest
{
    /// <summary>Bumps whenever the JSON shape changes incompatibly.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Absolute (or project-relative) path of the source music file.</summary>
    public required string InputPath { get; init; }

    /// <summary>Absolute (or project-relative) path of the final video.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Explicit composition. Never auto-resolved; CLI never silently substitutes.</summary>
    public CompositionKind Composition { get; init; } = CompositionKind.Diagnostic;

    public OutputSettings Output { get; init; } = new();
    public TrackSettings Tracks { get; init; } = new();
    public ViewSettings View { get; init; } = new();
    public StyleSettings Style { get; init; } = new();
    public PresentationSettings Presentation { get; init; } = new();
    public PlaybackSettings Playback { get; init; } = new();

    public VisualizationRequest WithPathResolved(string inputPath, string outputPath) => this with
    {
        InputPath = inputPath,
        OutputPath = outputPath,
    };

    /// <summary>
    /// True when this input is FMP-family (OVI/OPI/MVI/MZI/OZS/OZI/MPI) and
    /// therefore exposes the YM2608 (OPNA) audio backend. Used to gate the
    /// backend selector in the UI and the lazy native-availability check.
    /// </summary>
    public bool IsFmpLike()
    {
        string ext = Path.GetExtension(InputPath) ?? "";
        return ext.Contains("ovi", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("opi", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("mvi", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("mzi", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("ozs", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("ozi", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("mpi", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Output resolution, frame rate and encoding.</summary>
public sealed record OutputSettings
{
    public RenderQuality Quality { get; init; } = RenderQuality.Standard;

    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;

    public int FpsNumerator { get; init; } = 60;
    public int FpsDenominator { get; init; } = 1;

    public VideoEncoder Encoder { get; init; } = VideoEncoder.Auto;
    public bool Overwrite { get; init; }
}

/// <summary>Which tracks participate and how inactive diagnostic tracks are handled.</summary>
public sealed record TrackSettings
{
    public TrackSelectionMode Selection { get; init; } = TrackSelectionMode.Active;

    public IReadOnlyList<string> IncludedIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedIds { get; init; } = Array.Empty<string>();

    public bool IncludeInactiveDiagnosticTracks { get; init; }
}

/// <summary>Time window, grid and structural overlays.</summary>
public sealed record ViewSettings
{
    public double PastSeconds { get; init; } = 0.8;
    public double FutureSeconds { get; init; } = 3.2;

    public TimeGridMode TimeGrid { get; init; } = TimeGridMode.Automatic;

    public StructureOverlayMode Structure { get; init; } = StructureOverlayMode.Automatic;

    /// <summary>
    /// Scope render cadence in frames per second (plan §5.1). Null (default)
    /// auto-resolves to <c>min(outputFps, 30)</c>; an explicit value is
    /// clamped to the output frame rate (1:1 when scopeFps >= outputFps). The
    /// scope YAML <c>fps:</c> and the output-frame→scope-frame mapping both
    /// derive from this value, so the bridge emits scopeFps × duration frames
    /// and the compositor reuses each scope frame for the matching output
    /// frames.
    /// </summary>
    public double? ScopeFps { get; init; }
}

/// <summary>Visual style: effects, note coloring and palette.</summary>
public sealed record StyleSettings
{
    public VisualEffects Effects { get; init; } = VisualEffects.Subtle;
    public NoteColorMode NoteColor { get; init; } = NoteColorMode.Instrument;
    public PaletteKind Palette { get; init; } = PaletteKind.Default;

    /// <summary>
    /// Opacity of the scope waveform layer over the painted panel body
    /// (0.05..1.0, default 1.0). The scope frame's alpha channel is a
    /// per-pixel mask multiplied by this value and baked into RGB, because the
    /// encode path (RGBA → yuv420p) drops alpha.
    /// </summary>
    public double ScopeOpacity { get; init; } = 1.0;
}

/// <summary>Optional publication text and font.</summary>
public sealed record PresentationSettings
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Credits { get; init; }
    public string? FontPath { get; init; }
}

/// <summary>Playback / capture behavior. Runtime tool paths are intentionally absent.</summary>
public sealed record PlaybackSettings
{
    public int LoopCount { get; init; } = 2;
    public double FadeSeconds { get; init; } = 5;
    public double TailSeconds { get; init; } = 0.5;
    public double? MaximumDurationSeconds { get; init; } = 300;
    public int SampleRate { get; init; } = 48_000;
    public double SsgGainDb { get; init; }
    public SpcPitchInterpretation SpcPitch { get; init; } = SpcPitchInterpretation.Estimate;

    /// <summary>
    /// YM2608 audio backend for FMP-family sources. The one authoritative value
    /// for a render: preview and export both read it from the shared request.
    /// MDSound is the default; native audio never falls back.
    /// </summary>
    public FmpOpnaBackend OpnaBackend { get; init; } = FmpOpnaBackend.Mdsound;
}

public enum SpcPitchInterpretation
{
    Estimate,
    Relative,
}
