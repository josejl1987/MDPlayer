namespace Fmp.Application.Contracts;

/// <summary>
/// What a preview session can reuse and which capture paths are available for
/// the loaded input (retained across the session lifecycle).
/// </summary>
public sealed record VisualizationSessionCapabilities
{
    public bool SemanticCapture { get; init; }
    public bool ScopeCapture { get; init; }
    public bool AnalysisAvailable { get; init; }
    public bool HasCapturedTimeline { get; init; }
    public bool HasAnalysisResult { get; init; }
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = Array.Empty<ValidationIssue>();
}

/// <summary>Generic long-running operation state (not one global busy flag).</summary>
public sealed record OperationState
{
    public OperationStatus Status { get; init; } = OperationStatus.Idle;
    public string? Stage { get; init; }
    public double? Progress { get; init; }
    public string? Message { get; init; }
    public ValidationIssue? Failure { get; init; }
}
