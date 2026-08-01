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
    public CompositionKind Composition { get; init; } = CompositionKind.Performance;

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

    /// <summary>Performance-only: show the compact signal strip (off by default).</summary>
    public bool PerformanceSignalStrip { get; init; }
}

/// <summary>Visual style: effects, note coloring and palette.</summary>
public sealed record StyleSettings
{
    public VisualEffects Effects { get; init; } = VisualEffects.Subtle;
    public NoteColorMode NoteColor { get; init; } = NoteColorMode.Instrument;
    public PaletteKind Palette { get; init; } = PaletteKind.Default;
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
}