namespace Fmp.Application.Contracts;

/// <summary>
/// Result of inspecting an input file without capturing the full timeline.
/// Detected (not user-overridden) metadata and probe-level capabilities.
/// </summary>
public sealed record VisualizationInputInfo
{
    public required string FullPath { get; init; }
    public required string DisplayName { get; init; }
    public required string Format { get; init; }

    public string? Title { get; init; }
    public string? Game { get; init; }
    public string? System { get; init; }
    public string? Composer { get; init; }

    public TimeSpan? DeclaredDuration { get; init; }
    public TimeSpan? EstimatedDuration { get; init; }

    public IReadOnlyList<DeviceInfo> Devices { get; init; } = Array.Empty<DeviceInfo>();
    public IReadOnlyList<TrackCapabilityInfo> Tracks { get; init; } = Array.Empty<TrackCapabilityInfo>();

    public bool SupportsSemanticCapture { get; init; }
    public bool SupportsScopeCapture { get; init; }
    public bool SupportsAnalysis { get; init; }

    public IReadOnlyList<ValidationIssue> Issues { get; init; } = Array.Empty<ValidationIssue>();
}

/// <summary>Probe-level device description.</summary>
public sealed record DeviceInfo
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public int Instance { get; init; }
    public string? NoteSupport { get; init; }
    public string? ScopeSupport { get; init; }
}

/// <summary>
/// Track-level capability row shown in the GUI channel list. Populated after
/// the first capture/plan; before that only probe-level data exists.
/// </summary>
public sealed record TrackCapabilityInfo
{
    public required string TrackId { get; init; }
    public required string DisplayName { get; init; }
    public string? DeviceFamily { get; init; }
    public string? SemanticType { get; init; }
    public bool ScopeAvailable { get; init; }
    public bool ActivityDetected { get; init; }
    public bool DataIncomplete { get; init; }
    public string? ColorHex { get; init; }
}
