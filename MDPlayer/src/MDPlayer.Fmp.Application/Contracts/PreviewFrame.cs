namespace Fmp.Application.Contracts;

/// <summary>Request for a single still preview frame.</summary>
public sealed record PreviewFrameRequest
{
    public required double TimeSeconds { get; init; }
    public PreviewFidelity Fidelity { get; init; } = PreviewFidelity.AccurateStill;

    /// <summary>Preview dimensions; null = request dimensions capped to preview max.</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
}

/// <summary>Result of rendering a still preview frame (PNG bytes).</summary>
public sealed record PreviewFrameResult
{
    public required PreviewFidelity Fidelity { get; init; }
    public required double TimeSeconds { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>PNG-encoded frame.</summary>
    public required byte[] PngBytes { get; init; }

    /// <summary>True when any layer was approximated or omitted vs final output.</summary>
    public bool HasApproximations { get; init; }
    public IReadOnlyList<string> ApproximationNotes { get; init; } = Array.Empty<string>();

    /// <summary>Non-null when the renderer reported a failure but kept a valid frame.</summary>
    public ValidationIssue? Warning { get; init; }
}

/// <summary>Request for a short looping motion preview sequence.</summary>
public sealed record MotionPreviewRequest
{
    public double StartSeconds { get; init; }
    public double DurationSeconds { get; init; } = 3.0;
    public int Fps { get; init; } = 15;
    public int MaxWidth { get; init; } = 960;
    public int MaxHeight { get; init; } = 540;
}

/// <summary>Result of a motion preview render (PNG frames + manifest).</summary>
public sealed record MotionPreviewResult
{
    public required int FrameCount { get; init; }
    public required int Fps { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string FramesDirectory { get; init; }
    public IReadOnlyList<string> FramePaths { get; init; } = Array.Empty<string>();
    public bool HasApproximations { get; init; }
    public IReadOnlyList<string> ApproximationNotes { get; init; } = Array.Empty<string>();
}
