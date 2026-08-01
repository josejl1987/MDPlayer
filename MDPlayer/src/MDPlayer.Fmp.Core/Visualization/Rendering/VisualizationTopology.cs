using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

internal enum PanelContentKind
{
    SingleVoice,
    VoiceGroup,
    PercussionGroup,
    DeviceAggregate,
}

internal sealed record VisualizationPanel(
    string Id,
    string Label,
    PreparedPanelKind Kind,
    PanelContentKind Content,
    int Order,
    IReadOnlyList<string> VoiceIds,
    IReadOnlyList<string> OperatorVoiceIds)
{
    public PanelPresentationSchema Schema { get; init; }
        = PanelPresentationSchema.Unknown;
    public IReadOnlyList<PanelRowDefinition> Rows { get; init; }
        = Array.Empty<PanelRowDefinition>();
    public IReadOnlyList<string> AggregateSubVoiceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> AggregateSubVoiceLabels { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

internal sealed class VisualizationTopology
{
    public VisualizationTopology(IReadOnlyList<VisualizationPanel> panels)
    {
        Panels = panels ?? throw new ArgumentNullException(nameof(panels));
        if (panels.Count == 0)
            throw new ArgumentException("A visualization topology must contain at least one panel.", nameof(panels));
    }

    public IReadOnlyList<VisualizationPanel> Panels { get; }
}

/// <summary>
/// Converts stable voice descriptors into a presentation topology. The only
/// legacy knowledge here is the fallback for old FMP timelines that predate
/// descriptors; renderers consume the resulting topology and do not know FMP.
/// </summary>
internal static class VisualizationTopologyBuilder
{
    public static VisualizationTopology Build(VisualizationTimeline timeline)
        => Build(timeline, VisualizationLayoutMode.Diagnostic);

    public static VisualizationTopology Build(
        VisualizationTimeline timeline,
        VisualizationLayoutMode layoutMode)
        => Build(
            timeline,
            layoutMode,
            layoutMode is VisualizationLayoutMode.Diagnostic or VisualizationLayoutMode.DiagnosticV2
                ? VisualizationChannelFilter.All
                : VisualizationChannelFilter.Active);

    public static VisualizationTopology Build(
        VisualizationTimeline timeline,
        VisualizationLayoutMode layoutMode,
        VisualizationChannelFilter channelFilter)
        => Build(timeline, layoutMode, channelFilter, VisualizationGroupBy.None);

    public static VisualizationTopology Build(
        VisualizationTimeline timeline,
        VisualizationLayoutMode layoutMode,
        VisualizationChannelFilter channelFilter,
        VisualizationGroupBy groupBy)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        layoutMode = VisualizationLayoutModeResolver.Resolve(timeline, layoutMode);
        if (timeline.Voices.Count == 0)
        {
            VisualizationTopology legacy = VisualizationTopologyCompatibility.BuildLegacyTopology();
            if (layoutMode is VisualizationLayoutMode.UnifiedRoll
                or VisualizationLayoutMode.Hybrid
                or VisualizationLayoutMode.Performance)
                return BuildUnified(legacy.Panels, timeline, channelFilter);
            VisualizationTopology filteredLegacy = FilterTopology(legacy, timeline, layoutMode, channelFilter);
            return layoutMode == VisualizationLayoutMode.Focus
                ? Focus(filteredLegacy, timeline)
                : filteredLegacy;
        }

        if (VisualizationTopologyCompatibility.IsFixedEightVoiceTimeline(timeline))
        {
            VisualizationTopology fixedTopology = VisualizationTopologyCompatibility.BuildFixedEightVoiceTopology(timeline.Voices);
            if (layoutMode is VisualizationLayoutMode.UnifiedRoll
                or VisualizationLayoutMode.Hybrid
                or VisualizationLayoutMode.Performance)
                return BuildUnified(fixedTopology.Panels, timeline, channelFilter);
            fixedTopology = FilterTopology(fixedTopology, timeline, layoutMode, channelFilter);
            return layoutMode == VisualizationLayoutMode.Focus
                ? Focus(fixedTopology, timeline)
                : fixedTopology;
        }

        var source = timeline.Voices
            .OrderBy(voice => DeviceOrdering.Priority(voice.Id.Device.Type))
            .ThenBy(voice => voice.Id.Device.Instance)
            .ThenBy(voice => voice.Id.Device.ToString(), StringComparer.Ordinal)
            .ThenBy(voice => voice.Order)
            .ThenBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var panels = new List<VisualizationPanel>();

        foreach (VoiceDescriptor voice in source)
        {
            if (voice.Id.Kind == VoiceKind.Fm3Operator)
                continue;

            PreparedPanelKind kind = MapKind(voice);
            var operatorIds = source
                .Where(candidate => candidate.Id.Kind == VoiceKind.Fm3Operator
                    && candidate.Id.Device == voice.Id.Device)
                .OrderBy(candidate => candidate.Order)
                .Select(candidate => candidate.Id.ToString())
                .ToList();

            // Older FMP descriptors contain the FM3 voice but not explicit
            // operator descriptors. Discover the IDs from actual timeline data.
            if (kind == PreparedPanelKind.Fm3 && operatorIds.Count == 0)
                operatorIds.AddRange(
                    VisualizationTopologyCompatibility.FindLegacyOperatorVoiceIds(
                        timeline, voice.Id.Device));

            if (kind == PreparedPanelKind.Aggregate)
            {
                var subVoiceLabels = timeline.AggregateHits
                    .Where(value => value.VoiceId == voice.Id.ToString())
                    .GroupBy(value => value.SubVoiceId, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(value => value.Label)
                            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? group.Key,
                        StringComparer.Ordinal);
                string[] subVoices = subVoiceLabels.Keys
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                int groupCount = Math.Max(1, (subVoices.Length + 15) / 16);
                for (int group = 0; group < groupCount; group++)
                {
                    string[] groupSubVoices = subVoices.Skip(group * 16).Take(16).ToArray();
                    string id = groupCount == 1
                        ? voice.Id.ToString()
                        : $"{voice.Id}.group.{group + 1}";
                    var aggregatePanel = new VisualizationPanel(
                        id,
                        groupCount == 1 ? voice.DisplayName : $"{voice.DisplayName} {group + 1}",
                        kind,
                        PanelContentKind.DeviceAggregate,
                        panels.Count,
                        [voice.Id.ToString()],
                        operatorIds)
                    {
                        AggregateSubVoiceIds = groupSubVoices,
                        AggregateSubVoiceLabels = groupSubVoices.ToDictionary(
                            subVoice => subVoice,
                            subVoice => subVoiceLabels[subVoice],
                            StringComparer.Ordinal),
                        Schema = SchemaFor(kind),
                        Rows = RowsFor(timeline, kind, voice.Id.ToString(), groupSubVoices, voice.Rows),
                    };
                    panels.Add(aggregatePanel);
                }
                continue;
            }

            panels.Add(new VisualizationPanel(
                voice.Id.ToString(),
                voice.DisplayName,
                kind,
                voice.IsPercussion ? PanelContentKind.PercussionGroup : PanelContentKind.SingleVoice,
                panels.Count,
                [voice.Id.ToString()],
                operatorIds)
            {
                Schema = SchemaFor(kind),
                Rows = RowsFor(timeline, kind, voice.Id.ToString(), operatorIds, voice.Rows),
            });
        }

        if (panels.Count == 0)
            return VisualizationTopologyCompatibility.BuildLegacyTopology();

        VisualizationPanel[] ordered = panels
            .Where(panel => ShouldInclude(timeline, panel, layoutMode, channelFilter))
            .OrderBy(panel => panel.Order)
            .ToArray();
        if (ordered.Length == 0)
        {
            if ((layoutMode is VisualizationLayoutMode.Scope or VisualizationLayoutMode.ScopeStage)
                && HasScopeSource(timeline))
                return BuildScopeFallback(timeline);
            ordered = panels.OrderBy(panel => panel.Order).ToArray();
        }
        if (groupBy != VisualizationGroupBy.None
            && layoutMode is not (VisualizationLayoutMode.Diagnostic or VisualizationLayoutMode.DiagnosticV2))
            ordered = GroupPanels(ordered, timeline, groupBy);
        if (layoutMode is VisualizationLayoutMode.UnifiedRoll
            or VisualizationLayoutMode.Hybrid
            or VisualizationLayoutMode.Performance)
            return BuildUnified(ordered, timeline, channelFilter);

        if (layoutMode == VisualizationLayoutMode.Focus)
            return Focus(new VisualizationTopology(ordered), timeline);

        return new VisualizationTopology(ordered);
    }

    private static VisualizationTopology Focus(
        VisualizationTopology diagnostic,
        VisualizationTimeline timeline)
    {
        VisualizationPanel[] focused = diagnostic.Panels
            .Where(panel => HasActivity(timeline, panel))
            .Select((panel, index) => panel with { Order = index })
            .ToArray();
        return focused.Length > 0
            ? new VisualizationTopology(focused)
            : diagnostic;
    }

    private static VisualizationTopology FilterTopology(
        VisualizationTopology topology,
        VisualizationTimeline timeline,
        VisualizationLayoutMode layoutMode,
        VisualizationChannelFilter filter)
    {
        if (filter == VisualizationChannelFilter.All)
            return topology;

        VisualizationPanel[] panels = topology.Panels
            .Where(panel => ShouldInclude(timeline, panel, layoutMode, filter))
            .ToArray();
        if (panels.Length > 0)
            return new VisualizationTopology(panels);
        return (layoutMode is VisualizationLayoutMode.Scope or VisualizationLayoutMode.ScopeStage)
            && HasScopeSource(timeline)
            ? BuildScopeFallback(timeline)
            : topology;
    }

    private static VisualizationTopology BuildScopeFallback(VisualizationTimeline timeline)
    {
        string[] sourceVoiceIds = timeline.Voices.Count > 0
            ? timeline.Voices.Select(voice => voice.Id.ToString()).ToArray()
            : timeline.WaveformChanges.Select(value => value.VoiceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        return new VisualizationTopology(
        [
            new VisualizationPanel(
                "visualization.master-scope",
                "MASTER SCOPE",
                PreparedPanelKind.Placeholder,
                PanelContentKind.DeviceAggregate,
                0,
                sourceVoiceIds,
                Array.Empty<string>())
            {
                Schema = PanelPresentationSchema.AggregateActivity,
            },
        ]);
    }

    private static bool HasScopeSource(VisualizationTimeline timeline)
        => timeline.WaveformChanges.Length > 0
            || timeline.Devices.Any(device => device.ScopeSupport != ScopeSupport.None);

    private static VisualizationPanel[] GroupPanels(
        IReadOnlyList<VisualizationPanel> panels,
        VisualizationTimeline timeline,
        VisualizationGroupBy groupBy)
    {
        var voiceById = timeline.Voices.ToDictionary(
            voice => voice.Id.ToString(),
            StringComparer.Ordinal);
        return panels
            .GroupBy(panel => GroupKey(panel, voiceById, groupBy), StringComparer.Ordinal)
            .OrderBy(group => group.Min(panel => panel.Order))
            .Select((group, index) =>
            {
                VisualizationPanel first = group.First();
                VisualizationPanel[] members = group.ToArray();
                string[] voiceIds = members
                    .SelectMany(panel => panel.VoiceIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                string[] operatorIds = members
                    .SelectMany(panel => panel.OperatorVoiceIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                PanelRowDefinition[] rows = members
                    .SelectMany(panel => panel.Rows)
                    .GroupBy(row => row.Id, StringComparer.Ordinal)
                    .Select(row => row.First())
                    .OrderBy(row => row.StableOrder)
                    .ThenBy(row => row.Id, StringComparer.Ordinal)
                    .ToArray();
                string groupLabel = groupBy == VisualizationGroupBy.Device
                    ? DeviceLabel(first, voiceById, timeline)
                    : first.Schema switch
                    {
                        PanelPresentationSchema.PitchedLane => "PITCHED",
                        PanelPresentationSchema.PercussionRows => "PERCUSSION",
                        PanelPresentationSchema.SampleLane => "SAMPLES",
                        PanelPresentationSchema.NoiseLane => "NOISE",
                        _ => first.Label,
                    };
                return first with
                {
                    Id = $"group.{groupBy.ToString().ToLowerInvariant()}.{index + 1}",
                    Label = groupLabel,
                    Content = PanelContentKind.VoiceGroup,
                    Order = index,
                    VoiceIds = voiceIds,
                    OperatorVoiceIds = operatorIds,
                    Rows = rows,
                    AggregateSubVoiceIds = members
                        .SelectMany(panel => panel.AggregateSubVoiceIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    AggregateSubVoiceLabels = members
                        .SelectMany(panel => panel.AggregateSubVoiceLabels)
                        .GroupBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal),
                };
            })
            .ToArray();
    }

    private static string DeviceLabel(
        VisualizationPanel panel,
        IReadOnlyDictionary<string, VoiceDescriptor> voiceById,
        VisualizationTimeline timeline)
    {
        VoiceDescriptor voice = panel.VoiceIds
            .Select(id => voiceById.TryGetValue(id, out VoiceDescriptor value) ? value : null)
            .FirstOrDefault(value => value != null);
        if (voice == null)
            return panel.Label;

        return timeline.Devices
            .FirstOrDefault(device => device.Id == voice.DeviceId)?.DisplayName
            ?? voice.DeviceId.ToString();
    }

    private static string GroupKey(
        VisualizationPanel panel,
        IReadOnlyDictionary<string, VoiceDescriptor> voiceById,
        VisualizationGroupBy groupBy)
    {
        string identity = panel.VoiceIds
            .Select(id => voiceById.TryGetValue(id, out VoiceDescriptor voice)
                ? groupBy == VisualizationGroupBy.Device
                    ? voice.DeviceId.ToString()
                    : voice.Presentation.ToString()
                : panel.Id)
            .FirstOrDefault() ?? panel.Id;
        return $"{identity}|{panel.Schema}";
    }

    private static bool HasActivity(VisualizationTimeline timeline, VisualizationPanel panel)
    {
        var ids = new HashSet<string>(panel.VoiceIds.Concat(panel.OperatorVoiceIds), StringComparer.Ordinal);
        if (VisualizationActivity.HasMeaningfulNoteOnChannel(timeline, ids))
            return true;

        if (timeline.SamplePlayback.Any(value => ids.Contains(value.VoiceId)
            && value.EndSample > value.StartSample))
            return true;
        if (timeline.NoiseStates.Any(value => ids.Contains(value.VoiceId)
            && value.EndSample > value.StartSample))
            return true;
        if (timeline.AggregateHits.Any(value => ids.Contains(value.VoiceId)))
            return true;
        if (timeline.WaveformChanges.Any(value => ids.Contains(value.VoiceId)))
            return true;

        if (panel.Content == PanelContentKind.PercussionGroup
            && timeline.Rhythm.Any(evt => panel.VoiceIds.Any(id =>
                VisualizationTimelineCompatibility.RhythmBelongsToVoice(timeline, evt, id))))
            return true;

        return VisualizationTopologyCompatibility.HasLegacyActivity(timeline, panel.Id);
    }

    private static bool ShouldInclude(
        VisualizationTimeline timeline,
        VisualizationPanel panel,
        VisualizationLayoutMode layoutMode,
        VisualizationChannelFilter filter)
    {
        if (filter == VisualizationChannelFilter.All)
            return true;

        bool semantic = HasSemanticActivity(timeline, panel);
        bool audible = semantic || timeline.WaveformChanges.Any(value =>
            panel.VoiceIds.Contains(value.VoiceId, StringComparer.Ordinal));
        return filter switch
        {
            VisualizationChannelFilter.Semantic => semantic,
            VisualizationChannelFilter.Audible => audible,
            _ => HasActivity(timeline, panel),
        };
    }

    private static bool HasSemanticActivity(VisualizationTimeline timeline, VisualizationPanel panel)
    {
        var ids = new HashSet<string>(panel.VoiceIds.Concat(panel.OperatorVoiceIds), StringComparer.Ordinal);
        return VisualizationActivity.HasMeaningfulNoteOnChannel(timeline, ids)
            || timeline.SamplePlayback.Any(value => ids.Contains(value.VoiceId)
                && value.EndSample > value.StartSample)
            || timeline.NoiseStates.Any(value => ids.Contains(value.VoiceId)
                && value.EndSample > value.StartSample)
            || timeline.AggregateHits.Any(value => ids.Contains(value.VoiceId))
            || (panel.Content == PanelContentKind.PercussionGroup
                && timeline.Rhythm.Any(evt => panel.VoiceIds.Any(id =>
                    VisualizationTimelineCompatibility.RhythmBelongsToVoice(timeline, evt, id))))
            || VisualizationTopologyCompatibility.HasLegacyActivity(timeline, panel.Id);
    }

    private static VisualizationTopology BuildUnified(
        IReadOnlyList<VisualizationPanel> diagnostic,
        VisualizationTimeline timeline,
        VisualizationChannelFilter channelFilter)
    {
        var pitchPanels = diagnostic
            .Where(panel => IsUnifiedPitchPanel(panel, timeline))
            .Where(panel => ShouldInclude(timeline, panel, VisualizationLayoutMode.UnifiedRoll, channelFilter))
            .ToArray();
        var eventPanels = diagnostic
            .Where(panel => !IsUnifiedPitchPanel(panel, timeline))
            .Where(panel => ShouldInclude(timeline, panel, VisualizationLayoutMode.UnifiedRoll, channelFilter))
            .ToArray();

        var panels = new List<VisualizationPanel>(eventPanels.Length + 1);
        if (pitchPanels.Length > 0)
        {
            string[] voiceIds = pitchPanels
                .SelectMany(panel => panel.VoiceIds.Concat(panel.OperatorVoiceIds))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            panels.Add(new VisualizationPanel(
                "visualization.unified-roll",
                "UNIFIED ROLL",
                PreparedPanelKind.Pitched,
                PanelContentKind.VoiceGroup,
                0,
                voiceIds,
                Array.Empty<string>())
            {
                Schema = PanelPresentationSchema.PitchedLane,
                Rows = Array.Empty<PanelRowDefinition>(),
            });
        }

        panels.AddRange(eventPanels.Select((panel, index) => panel with { Order = index + panels.Count }));
        return panels.Count > 0
            ? new VisualizationTopology(panels)
            : new VisualizationTopology(diagnostic.ToArray());
    }

    /// <summary>
    /// A panel participates in the unified roll when it renders on an absolute
    /// pitch lane. Ordinary pitched, FM-operator, and wavetable panels always
    /// qualify; sample lanes qualify only when their voices carry real pitch
    /// (e.g. SPC BRR voices), so a non-pitched PCM percussion channel is never
    /// forced onto the shared roll.
    /// </summary>
    private static bool IsUnifiedPitchPanel(
        VisualizationPanel panel,
        VisualizationTimeline timeline)
    {
        if (panel.Schema is PanelPresentationSchema.PitchedLane
            or PanelPresentationSchema.FmOperatorGroup
            or PanelPresentationSchema.WaveTableLane)
            return true;
        if (panel.Schema != PanelPresentationSchema.SampleLane)
            return false;

        return panel.VoiceIds.Any(voiceId => timeline.Voices.Any(voice =>
            string.Equals(voice.Id.ToString(), voiceId, StringComparison.Ordinal)
            && voice.SupportsPitch
            && !voice.IsNoise
            && !voice.IsPercussion));
    }

    internal static PanelPresentationSchema SchemaFor(PreparedPanelKind kind) => kind switch
    {
        PreparedPanelKind.Fm3 => PanelPresentationSchema.FmOperatorGroup,
        PreparedPanelKind.Pitched or PreparedPanelKind.Ssg => PanelPresentationSchema.PitchedLane,
        PreparedPanelKind.Noise => PanelPresentationSchema.NoiseLane,
        PreparedPanelKind.PcmVoice => PanelPresentationSchema.SampleLane,
        PreparedPanelKind.Rhythm => PanelPresentationSchema.PercussionRows,
        PreparedPanelKind.Wavetable => PanelPresentationSchema.WaveTableLane,
        PreparedPanelKind.Aggregate => PanelPresentationSchema.AggregateActivity,
        _ => PanelPresentationSchema.Unknown,
    };

    private static IReadOnlyList<PanelRowDefinition> RowsFor(
        VisualizationTimeline timeline,
        PreparedPanelKind kind,
        string voiceId,
        IReadOnlyList<string> relatedIds,
        IReadOnlyList<VisualizationRowDescriptor> declaredRows = null)
    {
        if (kind == PreparedPanelKind.Rhythm)
        {
            var rows = new List<PanelRowDefinition>();
            if (declaredRows != null)
            {
                rows.AddRange(declaredRows.Select(row => new PanelRowDefinition(
                    row.Id,
                    row.Label)
                {
                    StableOrder = row.StableOrder,
                    Kind = row.Kind,
                }));
            }

            rows.AddRange(timeline.Rhythm
                .Where(value => VisualizationTimelineCompatibility.RhythmBelongsToVoice(
                    timeline, value, voiceId))
                .GroupBy(value => value.Voice, StringComparer.Ordinal)
                .Select(group => new PanelRowDefinition(group.Key, group.Key)
                {
                    Kind = VisualizationRowKind.Trigger,
                }));
            return rows
                .GroupBy(row => row.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(row => row.StableOrder)
                .ThenBy(row => row.Id, StringComparer.Ordinal)
                .ToArray();
        }

        if (kind == PreparedPanelKind.Fm3)
            return Enumerable.Range(1, 4)
                .Select(index => new PanelRowDefinition($"op{index}", $"OP{index}")
                {
                    StableOrder = index - 1,
                    Kind = VisualizationRowKind.Note,
                })
                .ToArray();

        if (kind == PreparedPanelKind.Aggregate)
            return relatedIds.Select((id, index) => new PanelRowDefinition(id, id)
            {
                StableOrder = index,
                Kind = VisualizationRowKind.Other,
            }).ToArray();

        return Array.Empty<PanelRowDefinition>();
    }

    /// <summary>
    /// Presentation-driven panel mapping with explicit precedence. Every
    /// defined <see cref="VoicePresentationKind"/> maps to a deliberate panel
    /// kind; <see cref="PreparedPanelKind.Placeholder"/> is reserved for
    /// out-of-range (external) presentation values on ordinary voices.
    /// </summary>
    internal static PreparedPanelKind MapKind(VoiceDescriptor voice) =>
        voice.Presentation switch
        {
            VoicePresentationKind.Fm3 => PreparedPanelKind.Fm3,
            VoicePresentationKind.Psg => PreparedPanelKind.Ssg,
            VoicePresentationKind.Percussion => PreparedPanelKind.Rhythm,
            VoicePresentationKind.Aggregate => PreparedPanelKind.Aggregate,
            VoicePresentationKind.Noise => PreparedPanelKind.Noise,
            VoicePresentationKind.Pcm => PreparedPanelKind.PcmVoice,
            VoicePresentationKind.Wavetable => PreparedPanelKind.Wavetable,
            VoicePresentationKind.Fm or VoicePresentationKind.Pitched or VoicePresentationKind.Midi
                when voice.Id.Kind is VoiceKind.Noise => PreparedPanelKind.Noise,
            VoicePresentationKind.Fm or VoicePresentationKind.Pitched or VoicePresentationKind.Midi
                when voice.Id.Kind is VoiceKind.Wavetable => PreparedPanelKind.Wavetable,
            VoicePresentationKind.Fm or VoicePresentationKind.Pitched or VoicePresentationKind.Midi
                => PreparedPanelKind.Pitched,
            _ when voice.Id.Kind is VoiceKind.Noise => PreparedPanelKind.Noise,
            _ when voice.Id.Kind is VoiceKind.Wavetable => PreparedPanelKind.Wavetable,
            _ when voice.Id.Kind is VoiceKind.Adpcm or VoiceKind.Pcm or VoiceKind.PcmVoice or VoiceKind.Dpcm
                => PreparedPanelKind.PcmVoice,
            _ when voice.Id.Kind is VoiceKind.Aggregate => PreparedPanelKind.Aggregate,
            _ => PreparedPanelKind.Placeholder,
        };

}
