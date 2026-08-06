using Fmp.Application.Contracts;
using Fmp.Core.Analysis;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Immutable result of the shared PR4 preparation stages (capture, scope/stem
/// generation, layout resolution, presentation, energy). Holds no processes,
/// disposable renderers or mutable caches — the caller constructs those on
/// demand via <see cref="VisualizationFrameRendererFactory"/>.
/// </summary>
internal sealed record PreparedVisualizationSource(
    VisualizationRequest Request,
    VisualizationTimeline Timeline,
    ResolvedVisualizationLayout Layout,
    VisualizationPresentation Presentation,
    AnalysisOverlayScene Analysis,
    IReadOnlyList<ChannelEnergyEnvelope> Energy,
    VisualizationScopeArtifacts Scope,
    IReadOnlyList<ProjectedScopeChannel> ScopeChannels,
    VisualizationPlanResult Plan,
    string MasterAudioPath,
    string BackendId,
    string TimelinePath = "");