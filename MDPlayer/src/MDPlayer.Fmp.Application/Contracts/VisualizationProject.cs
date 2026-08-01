namespace Fmp.Application.Contracts;

/// <summary>
/// Serializable visualization project (.mdpviz.json). Paths may be relative to
/// the project file directory when they share a practical common root.
/// </summary>
public sealed record VisualizationProject
{
    public int SchemaVersion { get; init; } = 1;
    public required VisualizationRequest Request { get; init; }

    public string? RelativeInputPath { get; init; }
    public string? RelativeOutputPath { get; init; }

    public double LastPreviewTimeSeconds { get; init; }
    public PreviewFidelity LastPreviewFidelity { get; init; }

    public DateTimeOffset LastSavedUtc { get; init; }

    /// <summary>Absolute path this project was loaded from / saved to (never serialized).</summary>
    public string? FilePath { get; init; }
}

/// <summary>Contents of the crash-recovery file (superset of the project).</summary>
public sealed record RecoveryState
{
    public int SchemaVersion { get; init; } = 1;
    public required VisualizationProject Project { get; init; }
    public bool ProjectFileExists { get; init; }
    public DateTimeOffset SavedUtc { get; init; }
}
