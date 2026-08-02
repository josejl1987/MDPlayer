using Fmp.Application.Contracts;

namespace Fmp.Application.Preview;

/// <summary>Cooperative progress reported during preview operations.</summary>
public sealed record PreviewProgress(string? Stage, double? Fraction, string? Message);

/// <summary>
/// Highest preview stage that a change between two request snapshots affects.
/// Used by the GUI to choose whether to run frame-only or plan-and-frame work.
/// </summary>
public enum PreviewRequestImpact
{
    None = 0,
    ExportOnly = 1,
    Frame = 2,
    Plan = 3,
    TimelineCapture = 4,
}

/// <summary>
/// A preview session bound to one input. Retains reusable capture/analysis
/// data for the session lifetime and produces plans, still frames and motion
/// sequences from immutable request snapshots.
/// </summary>
public interface IVisualizationPreviewSession : IAsyncDisposable
{
    VisualizationInputInfo Input { get; }
    VisualizationSessionCapabilities Capabilities { get; }

    Task<VisualizationPlanResult> PlanAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken);

    Task<PreviewFrameResult> RenderFrameAsync(
        VisualizationRequest request,
        PreviewFrameRequest preview,
        CancellationToken cancellationToken);

    Task<MotionPreviewResult> RenderMotionAsync(
        VisualizationRequest request,
        MotionPreviewRequest preview,
        IProgress<PreviewProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Classifies the highest preview stage affected by a change from
    /// <paramref name="previous"/> to <paramref name="next"/>. The GUI uses this
    /// to schedule frame-only versus plan-and-frame work.
    /// </summary>
    PreviewRequestImpact ClassifyChange(
        VisualizationRequest previous,
        VisualizationRequest next);

    /// <summary>
    /// Publishes the session's current timeline, master WAV, scope metadata and
    /// successful stems as one validated, reusable bundle and hands back a lease
    /// over it. Implementing sessions must validate that <paramref name="request"/>
    /// belongs to the session, prepare the capture, then atomically write the
    /// bundle manifest. Export may consume the returned lease.
    /// </summary>
    Task<ReusableCaptureLease> AcquireReusableCaptureAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates preview sessions bound to an input. The desktop GUI uses a
/// persistent in-process preview session. The standalone CLI creates a
/// short-lived in-process session for explicit preview commands.
/// </summary>
public interface IVisualizationPreviewSessionFactory
{
    Task<IVisualizationPreviewSession> OpenAsync(string inputPath, CancellationToken cancellationToken);
}
