using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Application.Validation;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Shared planning pipeline for the <c>plan</c> and <c>preview</c> commands:
/// prepares the track, captures (or reuses) the timeline, resolves the
/// concrete layout, and builds the <see cref="VisualizationPlanResult"/>
/// contract plus the renderable pieces the preview commands need.
/// </summary>
internal static class VisualizationPlanning
{
    internal sealed record PlanOutput(
        VisualizationRequest Request,
        VisualizationPlanResult Plan,
        VisualizationTimeline Timeline,
        ResolvedVisualizationLayout Layout,
        string MasterAudioPath);

    public static PlanOutput Prepare(
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        string timelinePath,
        string timelineOutPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        PreparedTrack? track = null;
        VisualizationTimeline timeline;
        string masterAudioPath = string.Empty;
        if (timelinePath != null && File.Exists(timelinePath))
        {
            timeline = VisualizationJsonWriter.Read(timelinePath);
        }
        else
        {
            VisualizationBackendResolution resolution = VisualizationBackendResolver.Resolve(request, runtime);

            if (string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal))
            {
                // Legacy FMP emulation capture path.
                track = TrackPreparation.Prepare(
                    request.InputPath, runtime.FmpCom, runtime.AssetsDir, runtime.SearchPaths);
                var capture = new VisualizationPipeline(
                    track.Assets,
                    track.FileSystem,
                    request.Playback.SampleRate).Capture(
                        track.Data,
                        track.Input.FullName,
                        new VisualizationPipeline.Options
                        {
                            LoopCount = request.Playback.LoopCount,
                            FadeSeconds = request.Playback.FadeSeconds,
                            TailSeconds = request.Playback.TailSeconds,
                            MaxDurationSeconds = request.Playback.MaximumDurationSeconds ?? 300,
                            TimeoutSeconds = runtime.ToolTimeoutMinutes * 60,
                        });
                if (!capture.Success || capture.Timeline == null)
                    throw new InvalidOperationException(
                        $"visualization capture failed: {capture.LastError}");
                timeline = capture.Timeline;
            }
            else
            {
                // Generic register-log backend (VGM/VGZ/MID/SPC/S98/XGM/...).
                int timelineSampleRate = resolution.Probe.NativeSampleRate > 0
                    ? resolution.Probe.NativeSampleRate
                    : request.Playback.SampleRate;
                var eventSink = new TimelineDecoderEventSink(timelineSampleRate);
                string audioDir = Path.Combine(
                    Path.GetTempPath(), "mdplayer-plan", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(audioDir);
                string audioPath = Path.Combine(audioDir, "master.wav");
                try
                {
                    using IPlaybackCaptureSession session = resolution.Backend.Open(
                        resolution.Input,
                        new PlaybackOptions(
                            request.Playback.LoopCount,
                            request.Playback.FadeSeconds,
                            request.Playback.TailSeconds,
                            request.Playback.MaximumDurationSeconds ?? 300,
                            audioPath,
                            request.Playback.SampleRate,
                            true,
                            MapSpcPitch(request.Playback.SpcPitch),
                            request.Playback.SsgGainDb),
                        eventSink);
                    session.Run();
                    masterAudioPath = audioPath;
                    timeline = eventSink.Complete(
                        session.SamplePosition,
                        "completed",
                        new TrackMetadata(
                            resolution.Input.Extension.TrimStart('.').ToLowerInvariant(),
                            Path.GetFileNameWithoutExtension(resolution.Input.Name),
                            resolution.Backend.Id,
                            resolution.Input.Name));
                }
                finally
                {
                    try { Directory.Delete(audioDir, recursive: true); } catch { /* temp */ }
                }
            }
        }

        if (timelineOutPath != null)
            VisualizationJsonWriter.Write(timelineOutPath, timeline);

        VisualizationLayoutMode mode = VisualizationLayoutModeMapper.FromComposition(
            request.Composition);
        ResolvedVisualizationLayout layout = VisualizationLayoutBuilder.Build(
            timeline,
            mode,
            request.ToLayoutSettings());
        VisualizationTopology topology = layout.Topology;
        OverlayLayout geometry = layout.Geometry;

        var issues = new List<ValidationIssue>(VisualizationRequestValidator.Validate(request));

        IReadOnlyList<ToolRequirement> toolRequirements = ToolRequirementResolver.Resolve(request);
        IReadOnlyList<RepresentativePoint> points = RepresentativePointAnalyzer.Compute(timeline, 0.75);

        double duration = timeline.SampleRate > 0
            ? (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate
            : 0.0;
        long frameCount = duration > 0
            ? (long)Math.Ceiling(duration * request.Output.FpsNumerator / (double)request.Output.FpsDenominator)
            : 0;

        var plan = new VisualizationPlanResult
        {
            ResolvedLayout = VisualizationLayoutNames.ToCliName(layout.Mode),
            RequestedLayout = VisualizationLayoutNames.ToCliName(layout.Mode),
            InputPath = Path.GetFullPath(request.InputPath),
            Tracks = BuildTracks(timeline, request),
            ExcludedTrackIds = request.Tracks.ExcludedIds.ToArray(),
            IncludedTrackIds = request.Tracks.IncludedIds.ToArray(),
            Regions = BuildRegions(topology, geometry),
            ToolRequirements = toolRequirements,
            ValidationIssues = issues,
            RepresentativePoints = points,
            EstimatedDurationSeconds = duration,
            EstimatedFrameCount = frameCount,
            Capabilities = BuildCapabilities(timeline, request),
            TimelinePath = timelineOutPath,
        };

        return new PlanOutput(request, plan, timeline, layout, masterAudioPath);
    }

    /// <summary>
    /// Builds the panel overlay renderer exactly as <see cref="VisualizationRunner"/>
    /// does, so previews match the published composition (minus scope/analysis
    /// layers, which are approximated in the preview output).
    /// </summary>
    internal static PanelOverlayRenderer BuildPanelRenderer(
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        PanelOverlayRenderer.Options rendering)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rendering);
        return new PanelOverlayRenderer(
            timeline,
            layout,
            rendering);
    }

    internal static PanelOverlayRenderer.Options CreatePreviewRendererOptions(
        VisualizationRequest request,
        VisualizationPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(request);
        OutputSettings output = request.Output;
        ViewSettings view = request.View;
        StyleSettings style = request.Style;
        return new PanelOverlayRenderer.Options
        {
            FpsNumerator = output.FpsNumerator,
            FpsDenominator = output.FpsDenominator,
            TimeGrid = MapTimeGrid(view.TimeGrid),
            Presentation = presentation,
            FontPath = request.Presentation.FontPath,
            PreferAntialiasedText = output.Quality != RenderQuality.Draft,
            Effects = MapEffects(style.Effects),
            NoteColor = MapNoteColor(style.NoteColor),
            IntroSeconds = 0.75,
            OutroSeconds = Math.Min(0.45, request.Playback.TailSeconds),
            AnalysisOverlay = Fmp.Core.Analysis.AnalysisOverlayScene.Empty,
            Energy = null,
        };
    }

    private static IReadOnlyList<TrackSelectionInfo> BuildTracks(
        VisualizationTimeline timeline,
        VisualizationRequest request)
    {
        var excluded = new HashSet<string>(request.Tracks.ExcludedIds, StringComparer.Ordinal);
        return timeline.Voices.Select(voice =>
        {
            string id = voice.Id.ToString();
            return new TrackSelectionInfo
            {
                TrackId = id,
                DisplayName = voice.DisplayName,
                DeviceFamily = voice.DeviceId.Type.ToString(),
                SemanticType = SemanticTypeName(voice.Presentation),
                ScopeAvailable = timeline.Devices.Any(device =>
                    device.Id == voice.DeviceId && device.ScopeSupport != ScopeSupport.None),
                Selected = !excluded.Contains(id),
                ActivityDetected = HasActivity(timeline, id),
                DataIncomplete = false,
                ColorHex = null,
            };
        }).ToArray();
    }

    private static VisualizationTimeGrid MapTimeGrid(TimeGridMode grid) => grid switch
    {
        TimeGridMode.None => VisualizationTimeGrid.None,
        TimeGridMode.Authoritative => VisualizationTimeGrid.Authoritative,
        TimeGridMode.Analytical => VisualizationTimeGrid.Analytical,
        _ => VisualizationTimeGrid.Automatic,
    };

    private static EffectsMode MapEffects(VisualEffects effects) => effects switch
    {
        VisualEffects.Off => EffectsMode.None,
        VisualEffects.Cinematic => EffectsMode.Cinematic,
        _ => EffectsMode.Minimal,
    };

    private static Fmp.Core.Visualization.Rendering.NoteColorMode MapNoteColor(
        Fmp.Application.Contracts.NoteColorMode mode) => mode switch
    {
        Fmp.Application.Contracts.NoteColorMode.Channel =>
            Fmp.Core.Visualization.Rendering.NoteColorMode.Channel,
        Fmp.Application.Contracts.NoteColorMode.PitchClass =>
            Fmp.Core.Visualization.Rendering.NoteColorMode.Pitch,
        _ => Fmp.Core.Visualization.Rendering.NoteColorMode.Instrument,
    };

    private static SpcPitchMode MapSpcPitch(SpcPitchInterpretation pitch) => pitch switch
    {
        SpcPitchInterpretation.Estimate => SpcPitchMode.Estimate,
        SpcPitchInterpretation.Relative => SpcPitchMode.Relative,
        _ => throw new ArgumentOutOfRangeException(nameof(pitch)),
    };

    private static IReadOnlyList<PanelRegionInfo> BuildRegions(
        VisualizationTopology topology,
        OverlayLayout layout)
    {
        var regions = new List<PanelRegionInfo>(topology.Panels.Count);
        for (int index = 0; index < topology.Panels.Count; index++)
        {
            VisualizationPanel panel = topology.Panels[index];
            OverlayRect rect = layout.GetPanelRect(index);
            regions.Add(new PanelRegionInfo
            {
                PanelId = panel.Id,
                Kind = PanelKindName(panel.Content),
                DisplayName = panel.Label,
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                TrackIds = new[] { panel.Id }
                    .Concat(panel.VoiceIds)
                    .Concat(panel.OperatorVoiceIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            });
        }
        return regions;
    }

    private static PreviewCapabilities BuildCapabilities(
        VisualizationTimeline timeline,
        VisualizationRequest request)
    {
        bool scope = timeline.WaveformChanges.Length > 0
            || timeline.Devices.Any(device => device.ScopeSupport != ScopeSupport.None);
        return new PreviewCapabilities
        {
            Semantic = timeline.Notes.Count > 0
                || timeline.Rhythm.Count > 0
                || timeline.SamplePlayback.Length > 0
                || timeline.NoiseStates.Length > 0
                || timeline.AggregateHits.Length > 0,
            Scope = scope,
            Analysis = false,
            Waveform = timeline.WaveformChanges.Length > 0,
        };
    }

    private static bool HasActivity(VisualizationTimeline timeline, string voiceId)
    {
        return timeline.Notes.Any(note => note.ChannelId == voiceId)
            || timeline.SamplePlayback.Any(evt => evt.VoiceId == voiceId)
            || timeline.NoiseStates.Any(evt => evt.VoiceId == voiceId)
            || timeline.AggregateHits.Any(evt => evt.VoiceId == voiceId)
            || timeline.WaveformChanges.Any(evt => evt.VoiceId == voiceId);
    }

    private static string SemanticTypeName(VoicePresentationKind presentation) => presentation switch
    {
        VoicePresentationKind.Pitched => "pitched",
        VoicePresentationKind.Fm => "fm",
        VoicePresentationKind.Fm3 => "fm3",
        VoicePresentationKind.Psg => "psg",
        VoicePresentationKind.Noise => "noise",
        VoicePresentationKind.Percussion => "percussion",
        VoicePresentationKind.Pcm => "pcm",
        VoicePresentationKind.Midi => "midi",
        VoicePresentationKind.Wavetable => "wavetable",
        VoicePresentationKind.Aggregate => "aggregate",
        _ => "unknown",
    };

    private static string PanelKindName(PanelContentKind content) => content switch
    {
        PanelContentKind.SingleVoice => "single-voice",
        PanelContentKind.VoiceGroup => "voice-group",
        PanelContentKind.PercussionGroup => "percussion-group",
        PanelContentKind.DeviceAggregate => "device-aggregate",
        _ => "panel",
    };
}
