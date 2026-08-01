namespace Fmp.Application.Contracts;

/// <summary>
/// Result of planning a request against a captured timeline: the concrete
/// layout, selected tracks, panel regions, tool requirements, validation
/// issues and preview helpers. Produced by the CLI `plan` command and by the
/// in-process planner.
/// </summary>
public sealed record VisualizationPlanResult
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Concrete layout selected from the explicit composition.</summary>
    public required string ResolvedLayout { get; init; }

    /// <summary>The user-requested composition name.</summary>
    public required string RequestedLayout { get; init; }

    public required string InputPath { get; init; }

    public IReadOnlyList<TrackSelectionInfo> Tracks { get; init; } = Array.Empty<TrackSelectionInfo>();
    public IReadOnlyList<string> ExcludedTrackIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IncludedTrackIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PanelRegionInfo> Regions { get; init; } = Array.Empty<PanelRegionInfo>();

    public IReadOnlyList<ToolRequirement> ToolRequirements { get; init; } = Array.Empty<ToolRequirement>();
    public IReadOnlyList<ValidationIssue> ValidationIssues { get; init; } = Array.Empty<ValidationIssue>();
    public IReadOnlyList<RepresentativePoint> RepresentativePoints { get; init; } = Array.Empty<RepresentativePoint>();

    public double? EstimatedDurationSeconds { get; init; }
    public long? EstimatedFrameCount { get; init; }

    public PreviewCapabilities Capabilities { get; init; } = new();

    /// <summary>Absolute path of the captured/loaded timeline used for this plan, if retained.</summary>
    public string? TimelinePath { get; init; }
}

/// <summary>Track selection summary shown in the channel list.</summary>
public sealed record TrackSelectionInfo
{
    public required string TrackId { get; init; }
    public required string DisplayName { get; init; }
    public string? DeviceFamily { get; init; }
    public string? SemanticType { get; init; }
    public bool ScopeAvailable { get; init; }
    public bool Selected { get; init; }
    public bool ActivityDetected { get; init; }
    public bool DataIncomplete { get; init; }
    public string? ColorHex { get; init; }
}

/// <summary>A prepared panel region within the composition (layout preview geometry).</summary>
public sealed record PanelRegionInfo
{
    public required string PanelId { get; init; }
    public required string Kind { get; init; }
    public string? DisplayName { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public IReadOnlyList<string> TrackIds { get; init; } = Array.Empty<string>();
}

/// <summary>What a preview may render for this input/request combination.</summary>
public sealed record PreviewCapabilities
{
    public bool Semantic { get; init; }
    public bool Scope { get; init; }
    public bool Analysis { get; init; }
    public bool Waveform { get; init; }
}

/// <summary>Representative scrub points for the timeline transport.</summary>
public sealed record RepresentativePoint
{
    public required string Kind { get; init; }
    public required double TimeSeconds { get; init; }
    public string? Label { get; init; }
}
