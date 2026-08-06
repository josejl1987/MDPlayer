using Fmp.Application.Contracts;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Lightweight prepared source built only from the captured semantic timeline.
/// It deliberately carries none of the heavier prepared artifacts — no isolated
/// stem list, no energy envelopes, no Corrscope configuration — because
/// constructing any of those requires rendered audio. A timeline-only frame
/// renderer and the planning path consume only this, so the first usable frame
/// can be produced without waiting for stem synthesis, energy analysis or
/// full renderer construction.
/// </summary>
internal sealed record PreparedTimelineSource(
    VisualizationRequest Request,
    VisualizationTimeline Timeline,
    ResolvedVisualizationLayout Layout,
    VisualizationPresentation Presentation,
    VisualizationPlanResult Plan,
    string MasterAudioPath,
    bool MasterAudioProduced,
    string BackendId,
    string TimelinePath);
