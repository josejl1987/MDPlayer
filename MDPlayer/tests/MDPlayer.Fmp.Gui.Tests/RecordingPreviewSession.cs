using Fmp.Application.Contracts;
using Fmp.Application.Preview;

namespace Fmp.Gui.Tests;

/// <summary>
/// A recording fake preview session/factory for headless VM tests so exact
/// PlanAsync/RenderFrameAsync call counts can drive the "output changes do not
/// refresh" and "rapid visual changes coalesce" assertions.
/// </summary>
internal sealed class RecordingPreviewFactory : IVisualizationPreviewSessionFactory
{
    private readonly List<RecordingPreviewSession> _sessions = new();

    public int PlanCalls => _sessions.Sum(s => s.PlanCalls);
    public int FrameCalls => _sessions.Sum(s => s.FrameCalls);
    public IReadOnlyList<RecordingPreviewSession> Sessions => _sessions;
    public RecordingPreviewSession LastSession => _sessions[^1];

    /// <summary>When set, the next OpenAsync call throws before any session is created.</summary>
    public Exception? FailNextOpen { get; set; }

    public VisualizationRequest? LastRequest => _sessions
        .SelectMany(s => s.Requests)
        .LastOrDefault();

    public Task<IVisualizationPreviewSession> OpenAsync(string inputPath, CancellationToken cancellationToken)
    {
        if (FailNextOpen is { } failure)
        {
            FailNextOpen = null;
            throw failure;
        }

        var session = new RecordingPreviewSession(inputPath);
        _sessions.Add(session);
        return Task.FromResult<IVisualizationPreviewSession>(session);
    }

    public void ResetCalls()
    {
        foreach (RecordingPreviewSession session in _sessions)
            session.ResetCalls();
    }
}

internal sealed class RecordingPreviewSession : IVisualizationPreviewSession
{
    private static readonly byte[] PreviewPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public RecordingPreviewSession(string inputPath)
    {
        Input = new VisualizationInputInfo
        {
            FullPath = inputPath,
            DisplayName = Path.GetFileName(inputPath),
            Format = Path.GetExtension(inputPath).TrimStart('.').ToUpperInvariant(),
            EstimatedDuration = TimeSpan.FromMinutes(3),
            Tracks = [new TrackCapabilityInfo { TrackId = "track-1", DisplayName = "Track 1" }],
        };
    }

    public int PlanCalls { get; private set; }
    public int FrameCalls { get; private set; }
    public List<VisualizationRequest> Requests { get; } = new();

    /// <summary>When set, PlanAsync blocks until the gate completes (simulates an in-flight refresh).</summary>
    public TaskCompletionSource? PlanGate { get; set; }

    public VisualizationInputInfo Input { get; }
    public VisualizationSessionCapabilities Capabilities { get; } = new()
    {
        SemanticCapture = true,
        HasCapturedTimeline = true,
    };

    public void ResetCalls()
    {
        PlanCalls = 0;
        FrameCalls = 0;
        Requests.Clear();
    }

    public Task<VisualizationPlanResult> PlanAsync(VisualizationRequest request, CancellationToken cancellationToken)
    {
        PlanCalls++;
        Requests.Add(request);
        if (PlanGate is { } gate)
            return PlanGatedAsync(gate.Task, cancellationToken);
        return Task.FromResult(new VisualizationPlanResult
        {
            ResolvedLayout = "unified",
            RequestedLayout = "auto",
            InputPath = request.InputPath,
            EstimatedDurationSeconds = 180,
            RepresentativePoints = [new RepresentativePoint { Kind = "start", TimeSeconds = 0, Label = "Start" }],
        });
    }

    private async Task<VisualizationPlanResult> PlanGatedAsync(Task gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new VisualizationPlanResult
        {
            ResolvedLayout = "unified",
            RequestedLayout = "auto",
            InputPath = "gated",
            EstimatedDurationSeconds = 180,
            RepresentativePoints = [new RepresentativePoint { Kind = "start", TimeSeconds = 0, Label = "Start" }],
        };
    }

    public Task<PreviewFrameResult> RenderFrameAsync(
        VisualizationRequest request,
        PreviewFrameRequest preview,
        CancellationToken cancellationToken)
    {
        FrameCalls++;
        return Task.FromResult(new PreviewFrameResult
        {
            Fidelity = preview.Fidelity,
            TimeSeconds = preview.TimeSeconds,
            Width = 1,
            Height = 1,
            PngBytes = PreviewPng,
        });
    }

    public Task<MotionPreviewResult> RenderMotionAsync(
        VisualizationRequest request,
        MotionPreviewRequest preview,
        IProgress<PreviewProgress>? progress,
        CancellationToken cancellationToken)
        => Task.FromResult(new MotionPreviewResult
        {
            FrameCount = 0,
            Fps = preview.Fps,
            Width = 1,
            Height = 1,
            FramesDirectory = Path.GetTempPath(),
        });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}