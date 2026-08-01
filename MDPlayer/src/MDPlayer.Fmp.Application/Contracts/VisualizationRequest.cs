namespace Fmp.Application.Contracts;

/// <summary>
/// The one authoritative, immutable request contract shared by the CLI and the
/// GUI. Every UI edit produces a new snapshot; long-running operations retain
/// the snapshot with which they started. All enums serialize as strings so the
/// JSON is stable and human-readable.
/// </summary>
public sealed record VisualizationRequest
{
    /// <summary>Bumps whenever the JSON shape changes incompatibly.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Absolute (or project-relative) path of the source music file.</summary>
    public required string InputPath { get; init; }

    /// <summary>Absolute (or project-relative) path of the final video.</summary>
    public required string OutputPath { get; init; }

    // ---- Basic ----
    public VisualizationPreset Preset { get; init; } = VisualizationPreset.Balanced;
    public VisualizationLayout Layout { get; init; } = VisualizationLayout.Auto;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int FpsNumerator { get; init; } = 60;
    public int FpsDenominator { get; init; } = 1;

    // ---- Content ----
    public ChannelSelectionMode ChannelSelection { get; init; } = ChannelSelectionMode.Active;
    public IReadOnlyList<string> IncludedTrackIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedTrackIds { get; init; } = Array.Empty<string>();

    public double PastSeconds { get; init; } = 0.75;
    public double FutureSeconds { get; init; } = 2.25;

    // ---- Style ----
    public VisualizationEffects Effects { get; init; } = VisualizationEffects.Minimal;
    public NoteColorMode NoteColor { get; init; } = NoteColorMode.Instrument;
    public double? ScopeRatio { get; init; }
    public ScopePosition ScopePosition { get; init; } = ScopePosition.Bottom;
    public TrackGroupingMode Grouping { get; init; } = TrackGroupingMode.None;
    public TimeGridMode TimeGrid { get; init; } = TimeGridMode.Automatic;
    public double RollZoom { get; init; } = 1.0;

    // ---- Analysis ----
    public bool AnalysisEnabled { get; init; }
    public AnalysisDetail AnalysisDetail { get; init; } = AnalysisDetail.Standard;

    /// <summary>Default matches the CLI/Balanced preset value (minimal).</summary>
    public AnalysisOverlayMode AnalysisOverlay { get; init; } = AnalysisOverlayMode.Minimal;

    // ---- Metadata ----
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Credits { get; init; }
    public string? FontPath { get; init; }

    // ---- Playback / capture ----
    public int LoopCount { get; init; } = 2;
    public double FadeSeconds { get; init; } = 5.0;
    public double TailSeconds { get; init; } = 0.5;
    public double? MaximumDurationSeconds { get; init; } = 300.0;
    public double? TimeoutSeconds { get; init; }
    public int SampleRate { get; init; } = 44_100;
    public double SsgGainDb { get; init; }
    public SpcPitchMode SpcPitch { get; init; } = SpcPitchMode.Estimate;

    // ---- Export ----
    public VideoEncoder Encoder { get; init; } = VideoEncoder.Auto;
    public BackendPreference Backend { get; init; } = BackendPreference.Auto;
    public ScopeMode ScopeMode { get; init; } = ScopeMode.Auto;
    public bool FinalQuality { get; init; }
    public bool StemsOnly { get; init; }
    public bool Overwrite { get; init; }

    /// <summary>Tool-path overrides and timeouts. Null leaves CLI defaults.</summary>
    public ToolOverrides Tools { get; init; } = new();

    public VisualizationRequest WithPathResolved(string inputPath, string outputPath) => this with
    {
        InputPath = inputPath,
        OutputPath = outputPath,
    };
}

/// <summary>Tool overrides for a single request (all nullable = use defaults).</summary>
public sealed record ToolOverrides
{
    public string? FmpComPath { get; init; }
    public string? AssetsDir { get; init; }
    public IReadOnlyList<string> SearchPaths { get; init; } = Array.Empty<string>();
    public string? CorrscopePath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? AnalysisPython { get; init; }
    public string? AnalysisCache { get; init; }
    public string? AnalysisOutput { get; init; }
    public bool AnalysisForce { get; init; }
    public int? AnalysisTimeoutMinutes { get; init; }
    public int? ToolTimeoutMinutes { get; init; }
    public string? CorrscopeVideoTemplate { get; init; }
}
