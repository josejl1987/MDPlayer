using Fmp.Application.Contracts;
using Fmp.Core.Analysis;
using Fmp.Core.Audio;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Immutable result of the capture stage: the captured timeline and scope/stem
/// artifacts, durable master audio path and backend identity. This is what a
/// preview session retains so it can re-use capture across
/// style/layout/dimension changes without recapturing the song.
/// </summary>
internal sealed record PreparedCapture(
    VisualizationTimeline Timeline,
    VisualizationScopeArtifacts Scope,
    string BackendId,
    string MasterAudioPath);

/// <summary>
/// Lightweight result of the semantic capture stage only: the captured timeline,
/// backend identity and the durable master-audio path. Used by the planning path
/// which does not need scope/stem artifacts.
/// </summary>
internal sealed record PreparedTimeline(
    VisualizationTimeline Timeline,
    string BackendId,
    string MasterAudioPath);

/// <summary>
/// Shared PR4 preparation stages for the render pipeline. It centralizes
/// backend resolution, semantic capture, scope/stem generation, layout
/// resolution, presentation and energy analysis.
///
/// Capture (<see cref="Capture"/>) produces the immutable per-input artifacts.
/// <see cref="BuildSource"/> projects those into a request-specific
/// <see cref="PreparedVisualizationSource"/> (layout, plan, energy, aligned
/// timeline) from which every consumer (final composition, GUI preview, visual
/// review) builds its frame renderer.
///
/// This is the ONLY place the pipeline prepares a source; planning and frame
/// rendering are pure projections over the result.
/// </summary>
internal static class VisualizationPrepareCoordinator
{
    /// <summary>
    /// Captures (or seeds) the semantic timeline and persists the canonical
    /// timeline to the workspace. Lightweight: it performs no scope/stem
    /// synthesis, no energy analysis and no layout construction, so a planning
    /// path can consume it without pulling audio capabilities in.
    /// </summary>
    public static PreparedTimeline CaptureTimeline(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationBackendResolution resolution,
        PreparedTrack? preparedFmpTrack,
        string? seedTimelinePath = null,
        string? timelineOutPath = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(resolution);

        // ---- Semantic capture ----
        VisualizationTimeline timeline;
        if (seedTimelinePath != null && File.Exists(seedTimelinePath))
        {
            timeline = VisualizationJsonWriter.Read(seedTimelinePath);
        }
        else
        {
            VisualizationCaptureArtifacts capture = VisualizationCaptureCoordinator.Capture(
                resolution, request, runtime, workspace, preparedFmpTrack);
            timeline = capture.Timeline;
            if (!VisualizationContentAvailability.HasRenderableContent(timeline))
                throw new VisualizationExecutionException(
                    "visualization capture contains neither semantic events nor waveform activity",
                    10);
        }

        // The workspace owns its canonical timeline; it is always persisted.
        VisualizationJsonWriter.Write(workspace.TimelinePath, timeline);
        if (timelineOutPath is not null
            && !Path.GetFullPath(timelineOutPath)
                .Equals(
                    Path.GetFullPath(workspace.TimelinePath),
                    StringComparison.Ordinal))
        {
            VisualizationJsonWriter.Write(timelineOutPath, timeline);
        }

        return new PreparedTimeline(
            timeline, resolution.Backend.Id, workspace.MasterAudioPath);
    }

    /// <summary>
    /// Resolves the scope/stem artifacts for the captured timeline and projects
    /// them (with energy, layout, plan) into a fully prepared source. This is
    /// the heavier half of preparation — it is needed by final rendering and
    /// preview, but never by the pure planning path.
    /// </summary>
    public static PreparedVisualizationSource PrepareRenderAssets(
        PreparedTimeline timeline,
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationBackendResolution resolution,
        PreparedTrack? preparedFmpTrack)
    {
        PreparedCapture capture = RenderScopeArtifacts(timeline, request, runtime, workspace, resolution, preparedFmpTrack);
        return BuildSource(capture, request, workspace);
    }

    /// <summary>
    /// Resolves the backend input, captures (or seeds) the semantic timeline and
    /// renders the scope/stem artifacts into the durable workspace. Returns the
    /// immutable capture result retained for the session lifetime.
    /// </summary>
    public static PreparedCapture Capture(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationBackendResolution resolution,
        PreparedTrack? preparedFmpTrack,
        string? seedTimelinePath = null,
        string? timelineOutPath = null)
    {
        PreparedTimeline timeline = CaptureTimeline(
            request, runtime, workspace, resolution, preparedFmpTrack,
            seedTimelinePath, timelineOutPath);
        return RenderScopeArtifacts(timeline, request, runtime, workspace, resolution, preparedFmpTrack);
    }

    /// <summary>
    /// Renders the scope/stem artifacts for an already-captured timeline. Kept
    /// separate so the planning path can skip it entirely.
    /// </summary>
    private static PreparedCapture RenderScopeArtifacts(
        PreparedTimeline timeline,
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationBackendResolution resolution,
        PreparedTrack? preparedFmpTrack)
    {
        VisualizationTimeline semanticTimeline = timeline.Timeline;

        // ---- Scope / stem generation ----
        // Capture renders the full stem set the backend can produce so the raw
        // reusable assets (isolated stems + master) are independent of any one
        // composition. The request-specific projection to the resolved layout
        // (filtering, ordering, master fallback expansion) happens in
        // <see cref="BuildSource"/> so later layout changes do not reuse a scope
        // result that was baked to the first composition.
        VisualizationScopeArtifacts scopeArtifacts;
        try
        {
            scopeArtifacts = VisualizationScopeCoordinator.Render(
                resolution.Backend.Id,
                resolution.Input,
                workspace,
                request,
                runtime,
                semanticTimeline.Devices,
                semanticTimeline.Voices,
                Math.Max(1, semanticTimeline.EndSample - semanticTimeline.StartSample),
                preparedFmpTrack,
                scopesRequired: true);
        }
        catch (VisualizationScopeException)
        {
            throw;
        }

        return new PreparedCapture(
            semanticTimeline,
            scopeArtifacts,
            timeline.BackendId,
            timeline.MasterAudioPath);
    }

    /// <summary>
    /// Projects a captured timeline + scope into the request-specific source:
    /// resolves the layout, presentation, energy envelopes, aligned timeline and
    /// plan. Inexpensive relative to capture, so it runs for every render-key
    /// change while the capture is reused.
    /// </summary>
    public static PreparedVisualizationSource BuildSource(
        PreparedCapture capture,
        VisualizationRequest request,
        VisualizationWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);

        OutputSettings output = request.Output;

        ResolvedVisualizationLayout layout = VisualizationLayoutBuilder.Build(
            capture.Timeline,
            VisualizationLayoutModeMapper.FromComposition(request.Composition),
            request.ToLayoutSettings());

        ScopeRenderer.ScopeResult scopeResult = capture.Scope.Result;
        VisualizationPresentation presentation =
            VisualizationSupport.ResolvePresentation(request, new FileInfo(request.InputPath));

        // Project the reusable captured scope assets to the resolved layout:
        // filter/order isolated stems by the current topology's panel order and
        // expand the master fallback to the current panel count. This keeps the
        // capture reusable across compositions/track selections instead of
        // baking it to the first resolved layout.
        ScopeRenderer.ScopeResult projectedScope =
            ProjectScopes(scopeResult, layout, workspace);

        // Align the semantic timeline to the actual audio length.
        VisualizationTimeline videoTimeline = VisualizationSupport.AlignTimelineToAudio(
            capture.Timeline, projectedScope.MasterSamples);

        // ---- Energy analysis ----
        int totalFrames = (int)Math.Ceiling(projectedScope.MasterSamples
            * (double)output.FpsNumerator / projectedScope.SampleRate);
        ChannelEnergyEnvelope[] energy = ChannelEnergyAnalyzer.Analyze(
            projectedScope.Stems.Where(stem => stem.Success)
                .Select(stem => (stem.Name, stem.WavPath)).ToArray(),
            totalFrames, projectedScope.SampleRate,
            output.FpsNumerator, output.FpsDenominator);

        VisualizationPlanResult plan = VisualizationPlanBuilder.Build(
            request, capture.Timeline, layout, workspace.TimelinePath);

        return new PreparedVisualizationSource(
            Request: request,
            Timeline: videoTimeline,
            Layout: layout,
            Presentation: presentation,
            Analysis: AnalysisOverlayScene.Empty,
            Energy: energy,
            Scope: capture.Scope with { Result = projectedScope },
            Plan: plan,
            MasterAudioPath: capture.MasterAudioPath,
            BackendId: capture.BackendId,
            TimelinePath: workspace.TimelinePath);
    }

    /// <summary>
    /// Projects reusable captured scope assets onto the resolved layout. It
    /// filters and orders isolated stems to follow the current topology's panel
    /// order (master first), and when no isolated stem survives the projection
    /// it fills the grid with one master channel per panel at the current panel
    /// count. Moving this projection here instead of baking it into capture
    /// means later layout/track-selection changes reuse the raw stems and re-derive
    /// the request-specific scope channel set.
    /// </summary>
    private static ScopeRenderer.ScopeResult ProjectScopes(
        ScopeRenderer.ScopeResult captured,
        ResolvedVisualizationLayout layout,
        VisualizationWorkspace workspace)
    {
        if (!layout.Geometry.HasScopes)
            return captured;

        // Name -> presentation track id from the canonical stem catalog, so a
        // STEM result can be matched to the topology panels that carry that voice.
        var stemTrackId = DefaultStems.All.ToDictionary(
            stem => stem.Name, stem => stem.PresentationTrackId,
            StringComparer.Ordinal);

        var master = captured.Stems.FirstOrDefault(s => s.Name == "master");
        var isolated = captured.Stems
            .Where(s => s.Name != "master" && s.Success)
            .ToList();

        // Ordered panels of the current topology.
        IReadOnlyList<VisualizationPanel> panels = layout.Topology.Panels;
        var panelIndexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < panels.Count; i++)
        {
            foreach (string voiceId in panels[i].VoiceIds)
                panelIndexOf.TryAdd(voiceId, i);
            panelIndexOf.TryAdd(panels[i].Id, i);
        }

        var projected = new ScopeRenderer.ScopeResult
        {
            Success = true,
            InputPath = captured.InputPath,
            OutputDir = captured.OutputDir,
            MasterSamples = captured.MasterSamples,
            SampleRate = captured.SampleRate,
            CompletionReason = captured.CompletionReason,
        };

        if (master is not null)
            projected.Stems.Add(master);

        if (isolated.Count == 0)
        {
            // No isolated stem was rendered at all: fall back to one master
            // channel per panel, expanded to the current panel count.
            return ExpandMasterToPanels(workspace, captured, panels.Count);
        }

        // Order isolated stems to follow the current topology's panel order
        // (stable order as a tie-break). The full synchronized stem set is
        // retained — final/scoped backends validate all synchronized stems, and
        // Corrscope maps the channel list onto the current grid geometry.
        var ordered = isolated.Select(stem =>
        {
            string trackId = stemTrackId.TryGetValue(stem.Name, out string? id) ? id : stem.Name;
            int panelOrder = panelIndexOf.TryGetValue(trackId, out int p) ? p : int.MaxValue;
            return (Stem: stem, PanelOrder: panelOrder);
        })
        .OrderBy(pair => pair.PanelOrder)
        .ThenBy(pair => pair.Stem.StableOrder)
        .Select(pair => pair.Stem)
        .ToList();

        projected.Stems.AddRange(ordered);
        return projected;
    }

    /// <summary>
    /// Master-fallback grid filling: when no isolated stems exist, publish one
    /// master channel per panel so every scope grid cell carries the master
    /// waveform and the overlay scope cells stay aligned.
    /// </summary>
    private static ScopeRenderer.ScopeResult ExpandMasterToPanels(
        VisualizationWorkspace workspace,
        ScopeRenderer.ScopeResult scopeResult,
        int panelCount)
    {
        var result = new ScopeRenderer.ScopeResult
        {
            Success = true,
            InputPath = scopeResult.InputPath,
            OutputDir = workspace.ScopeDir,
            MasterSamples = scopeResult.MasterSamples,
            SampleRate = scopeResult.SampleRate,
            CompletionReason = scopeResult.CompletionReason,
        };
        for (int panel = 0; panel < panelCount; panel++)
        {
            result.Stems.Add(new ScopeRenderer.StemResult
            {
                Name = "master",
                Label = "Master",
                WavPath = workspace.MasterAudioPath,
                RenderedSamples = scopeResult.MasterSamples,
                Channels = 2,
                Success = true,
            });
        }
        return result;
    }
}