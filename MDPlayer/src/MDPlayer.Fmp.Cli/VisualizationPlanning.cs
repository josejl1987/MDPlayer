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
/// concrete layout, applies include/exclude track filtering, and builds the
/// <see cref="VisualizationPlanResult"/> contract plus the renderable pieces
/// (timeline, resolved layout mode, overlay layout) the preview commands need.
/// </summary>
internal static class VisualizationPlanning
{
    internal sealed record PlanOutput(
        VisualizationRequest Request,
        VisualizationPlanResult Plan,
        VisualizationTimeline Timeline,
        VisualizationLayoutMode ResolvedLayout,
        OverlayLayout Layout);

    /// <summary>
    /// Full plan for a request. <paramref name="timelinePath"/> reuses an
    /// existing captured timeline when it exists (capture is skipped);
    /// <paramref name="timelineOutPath"/> writes the used timeline so the
    /// caller can keep it for later previews.
    /// </summary>
    public static PlanOutput Prepare(
        VisualizeOptions options,
        VisualizationRequest request,
        string timelinePath,
        string timelineOutPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        PreparedTrack? track = null;
        VisualizationTimeline timeline;
        if (timelinePath != null && File.Exists(timelinePath))
        {
            timeline = VisualizationJsonWriter.Read(timelinePath);
        }
        else
        {
            VisualizationBackendResolution resolution = VisualizationBackendResolver.Resolve(options);

            if (string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal))
            {
                // Legacy FMP emulation capture path.
                track = TrackPreparation.Prepare(options.Input, options);
                var capture = new VisualizationPipeline(
                    track.Assets,
                    track.FileSystem,
                    options.SampleRate).Capture(
                        track.Data,
                        track.Input.FullName,
                        new VisualizationPipeline.Options
                        {
                            LoopCount = options.Loops,
                            FadeSeconds = options.Fade,
                            TailSeconds = options.Tail,
                            MaxDurationSeconds = options.MaxDuration,
                            TimeoutSeconds = options.Timeout,
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
                    : options.SampleRate;
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
                            options.Loops,
                            options.Fade,
                            options.Tail,
                            options.MaxDuration,
                            audioPath,
                            options.SampleRate,
                            options.SpcStems,
                            options.SpcPitchMode,
                            options.SsgGainDb),
                        eventSink);
                    session.Run();
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

        VisualizationLayoutMode resolvedMode = VisualizationLayoutModeResolver.Resolve(
            timeline, options.LayoutMode);
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline, resolvedMode, options.Channels, options.GroupBy);
        topology = ApplyTrackFiltering(topology, options);

        OverlayLayout layout = new(
            options.Width,
            options.Height,
            options.PastSeconds,
            options.FutureSeconds,
            topology.Panels.Count,
            resolvedMode,
            options.ScopeHeight,
            options.TimelineHeight,
            options.RollZoom,
            options.ScopeRatio,
            options.ScopePosition);

        var issues = new List<ValidationIssue>(VisualizationRequestValidator.Validate(request));
        try
        {
            VisualizationLayoutValidator.Validate(layout, topology);
        }
        catch (Exception)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.LayoutTooSmall,
                Severity = ValidationSeverity.Warning,
                Message = "The requested resolution is too small for the selected layout and panels.",
                SettingPath = nameof(request.Output.Width),
                SuggestedAction = "Increase the output resolution or reduce the number of channels.",
            });
        }

        IReadOnlyList<ToolRequirement> toolRequirements = ToolRequirementResolver.Resolve(request);
        IReadOnlyList<RepresentativePoint> points = RepresentativePointAnalyzer.Compute(timeline, 0.75);

        double duration = timeline.SampleRate > 0
            ? (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate
            : 0.0;
        long frameCount = duration > 0
            ? (long)Math.Ceiling(duration * options.Fps / (double)options.FpsDenominator)
            : 0;

        var plan = new VisualizationPlanResult
        {
            ResolvedLayout = VisualizationLayoutNames.ToCliName(resolvedMode),
            RequestedLayout = RequestedLayoutName(request.Composition),
            InputPath = Path.GetFullPath(options.Input),
            Tracks = BuildTracks(timeline, options),
            ExcludedTrackIds = options.ExcludeTracks.ToArray(),
            IncludedTrackIds = options.IncludeTracks.ToArray(),
            Regions = BuildRegions(topology, layout),
            ToolRequirements = toolRequirements,
            ValidationIssues = issues,
            RepresentativePoints = points,
            EstimatedDurationSeconds = duration,
            EstimatedFrameCount = frameCount,
            Capabilities = BuildCapabilities(timeline, request),
            TimelinePath = timelineOutPath,
        };

        return new PlanOutput(request, plan, timeline, resolvedMode, layout);
    }

    /// <summary>
    /// Builds the panel overlay renderer exactly as <see cref="VisualizationRunner"/>
    /// does, so previews match the published composition (minus scope/analysis
    /// layers, which are approximated in the preview output).
    /// </summary>
    internal static PanelOverlayRenderer BuildPanelRenderer(
        VisualizationTimeline timeline,
        VisualizeOptions options,
        VisualizationLayoutMode resolvedMode,
        Fmp.Core.Visualization.Rendering.VisualizationPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(options);
        return new PanelOverlayRenderer(
            timeline,
            new PanelOverlayRenderer.Options
            {
                Width = options.Width,
                Height = options.Height,
                FpsNumerator = options.Fps,
                FpsDenominator = options.FpsDenominator,
                PastSeconds = options.PastSeconds,
                FutureSeconds = options.FutureSeconds,
                RollZoom = options.RollZoom,
                ScopeHeight = options.ScopeHeight,
                TimelineHeight = options.TimelineHeight,
                ScopeRatio = options.ScopeRatio,
                ScopePosition = options.ScopePosition,
                Channels = options.Channels,
                GroupBy = options.GroupBy,
                TimeGrid = options.TimeGrid,
                Presentation = presentation,
                FontPath = options.FontPath,
                PreferAntialiasedText = options.Preset != Fmp.Core.Visualization.Rendering.VisualizationPreset.Diagnostic
                    && resolvedMode != VisualizationLayoutMode.DiagnosticV2,
                Effects = options.Effects,
                NoteColor = options.NoteColor,
                LayoutMode = resolvedMode,
                IntroSeconds = 0.75,
                OutroSeconds = Math.Min(0.45, options.Tail),
                AnalysisOverlay = Fmp.Core.Analysis.AnalysisOverlayScene.Empty,
                Energy = null,
            });
    }

    /// <summary>
    /// Applies --include-track/--exclude-track filtering to the topology
    /// panels. A panel is dropped when its panel id or any of its voice ids
    /// intersects the exclude list; when an include list is present only
    /// panels intersecting it survive, except panels whose id contains
    /// "master" which are always kept. An empty result falls back to the
    /// unfiltered topology.
    /// </summary>
    private static VisualizationTopology ApplyTrackFiltering(
        VisualizationTopology topology,
        VisualizeOptions options)
    {
        if (options.ExcludeTracks.Count == 0 && options.IncludeTracks.Count == 0)
            return topology;

        var exclude = new HashSet<string>(options.ExcludeTracks, StringComparer.Ordinal);
        var include = new HashSet<string>(options.IncludeTracks, StringComparer.Ordinal);

        bool Excluded(VisualizationPanel panel)
            => exclude.Contains(panel.Id)
                || panel.VoiceIds.Any(exclude.Contains)
                || panel.OperatorVoiceIds.Any(exclude.Contains);

        bool Included(VisualizationPanel panel)
            => include.Contains(panel.Id)
                || panel.VoiceIds.Any(include.Contains)
                || panel.OperatorVoiceIds.Any(include.Contains);

        VisualizationPanel[] filtered = topology.Panels
            .Where(panel => !Excluded(panel))
            .Where(panel => options.IncludeTracks.Count == 0
                || Included(panel)
                || panel.Id.Contains("master", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return filtered.Length > 0
            ? new VisualizationTopology(filtered)
            : topology;
    }

    private static IReadOnlyList<TrackSelectionInfo> BuildTracks(
        VisualizationTimeline timeline,
        VisualizeOptions options)
    {
        var excluded = new HashSet<string>(options.ExcludeTracks, StringComparer.Ordinal);
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

    internal static string RequestedLayoutName(CompositionKind composition) => composition switch
    {
        CompositionKind.ScopeStage => "scope-stage",
        CompositionKind.Diagnostic => "diagnostic",
        _ => "performance",
    };

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
