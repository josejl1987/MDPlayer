using Fmp.Application.Contracts;
using Fmp.Core.Analysis;
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
    /// <summary>Captures then projects a fully resolved source in one call.</summary>
    public static PreparedVisualizationSource Prepare(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        VisualizationWorkspace workspace,
        VisualizationBackendResolution resolution,
        PreparedTrack? preparedFmpTrack,
        string? seedTimelinePath = null,
        string? timelineOutPath = null)
    {
        PreparedCapture capture = Capture(
            request, runtime, workspace, resolution, preparedFmpTrack,
            seedTimelinePath, timelineOutPath);
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
            if (timelineOutPath != null)
                VisualizationJsonWriter.Write(timelineOutPath, timeline);
        }

        // Layout is resolved once to decide scope geometry; the request-specific
        // projected source derives its own layout in BuildSource.
        ResolvedVisualizationLayout probeLayout = VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutModeMapper.FromComposition(request.Composition),
            request.ToLayoutSettings());

        // ---- Scope / stem generation ----
        VisualizationScopeArtifacts scopeArtifacts;
        try
        {
            scopeArtifacts = VisualizationScopeCoordinator.Render(
                resolution.Backend.Id,
                resolution.Input,
                workspace,
                request,
                runtime,
                timeline.Devices,
                timeline.Voices,
                Math.Max(1, timeline.EndSample - timeline.StartSample),
                preparedFmpTrack,
                scopesRequired: probeLayout.Geometry.HasScopes);
        }
        catch (VisualizationScopeException)
        {
            throw;
        }

        if (scopeArtifacts.Enabled && !scopeArtifacts.HasIsolatedStems)
        {
            scopeArtifacts = scopeArtifacts with
            {
                Result = ExpandMasterToPanels(
                    workspace, scopeArtifacts.Result, probeLayout.Geometry.PanelCount),
            };
        }

        return new PreparedCapture(
            timeline,
            scopeArtifacts,
            resolution.Backend.Id,
            workspace.MasterAudioPath);
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

        // Align the semantic timeline to the actual audio length.
        VisualizationTimeline videoTimeline = VisualizationSupport.AlignTimelineToAudio(
            capture.Timeline, scopeResult.MasterSamples);

        // ---- Energy analysis ----
        int totalFrames = (int)Math.Ceiling(scopeResult.MasterSamples
            * (double)output.FpsNumerator / scopeResult.SampleRate);
        ChannelEnergyEnvelope[] energy = ChannelEnergyAnalyzer.Analyze(
            scopeResult.Stems.Where(stem => stem.Success)
                .Select(stem => (stem.Name, stem.WavPath)).ToArray(),
            totalFrames, scopeResult.SampleRate,
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
            Scope: capture.Scope,
            Plan: plan,
            MasterAudioPath: capture.MasterAudioPath,
            BackendId: capture.BackendId,
            TimelinePath: workspace.TimelinePath);
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