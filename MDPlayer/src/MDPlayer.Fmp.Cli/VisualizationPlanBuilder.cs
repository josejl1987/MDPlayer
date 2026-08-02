using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Validation;
using Fmp.Application.Preview;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Pure plan projection: translates already-prepared data (timeline, layout)
/// into the application-facing <see cref="VisualizationPlanResult"/> contract.
/// It performs no capture, backend resolution, scope or layout construction.
/// </summary>
internal static class VisualizationPlanBuilder
{
    public static VisualizationPlanResult Build(
        VisualizationRequest request,
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        string? timelinePath = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(layout);

        IReadOnlyList<ValidationIssue> issues =
            VisualizationRequestValidator.Validate(request);

        return new VisualizationPlanResult
        {
            ResolvedLayout =
                VisualizationLayoutNames.ToCliName(layout.Mode),

            RequestedLayout =
                VisualizationCommandFormatter.CompositionName(
                    request.Composition),

            InputPath = Path.GetFullPath(request.InputPath),

            Tracks =
                BuildTracks(
                    timeline,
                    layout,
                    request),

            ExcludedTrackIds = request.Tracks.ExcludedIds.ToArray(),
            IncludedTrackIds = request.Tracks.IncludedIds.ToArray(),

            Regions =
                BuildRegions(
                    layout.Topology,
                    layout.Geometry),

            ToolRequirements =
                ToolRequirementResolver.Resolve(request),

            ValidationIssues = issues,

            RepresentativePoints =
                RepresentativePointAnalyzer.Compute(
                    timeline,
                    0.75),

            EstimatedDurationSeconds =
                DurationSeconds(timeline),

            EstimatedFrameCount =
                FrameCount(
                    timeline,
                    request.Output),

            Capabilities =
                BuildCapabilities(
                    timeline,
                    layout),

            TimelinePath = timelinePath,
        };
    }

    private static double? DurationSeconds(VisualizationTimeline timeline)
        => timeline.SampleRate > 0
            ? (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate
            : 0.0;

    private static long? FrameCount(
        VisualizationTimeline timeline,
        OutputSettings output)
    {
        double duration = timeline.SampleRate > 0
            ? (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate
            : 0.0;
        return duration > 0
            ? (long)Math.Ceiling(
                duration * output.FpsNumerator / (double)output.FpsDenominator)
            : 0;
    }

    /// <summary>
    /// Builds track-selection reporting from the resolved topology — the single
    /// source of truth established by PR2 — rather than reinterpreting the
    /// request. <c>Selected</c> is true when any panel of the resolved layout
    /// draws this voice.
    /// </summary>
    private static IReadOnlyList<TrackSelectionInfo> BuildTracks(
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        VisualizationRequest request)
    {
        var selectedVoiceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (VisualizationPanel panel in layout.Topology.Panels)
        {
            foreach (string id in panel.VoiceIds.Concat(panel.OperatorVoiceIds))
                selectedVoiceIds.Add(id);
        }

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
                Selected = selectedVoiceIds.Contains(id),
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
        ResolvedVisualizationLayout layout)
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