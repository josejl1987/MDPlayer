using Fmp.Core.Visualization.Rendering;

namespace Fmp.Core.Visualization;

/// <summary>
/// Compatibility data for captures produced before typed voice descriptors.
/// Chip-specific legacy IDs stay at the data boundary; generic topology and
/// renderers consume only the resulting panels.
/// </summary>
internal static class VisualizationTopologyCompatibility
{
    private const string LegacyAdpcmPanelId = "ym2608.0.adpcm-b";
    private const string LegacyPpz8PanelId = "ppz8.0";

    public static readonly string[] LegacyPanelIds =
    [
        "ym2608.0.fm.1", "ym2608.0.fm.2", "ym2608.0.fm.3",
        "ym2608.0.fm.4", "ym2608.0.fm.5", "ym2608.0.fm.6",
        "ym2608.0.ssg.1", "ym2608.0.ssg.2", "ym2608.0.ssg.3",
        "ym2608.0.rhythm", LegacyAdpcmPanelId, LegacyPpz8PanelId,
    ];

    public static readonly string[] LegacyPanelLabels =
    [
        "FM1", "FM2", "FM3", "FM4", "FM5", "FM6",
        "SSG1", "SSG2", "SSG3", "RHYTHM", "ADPCM-B", "PPZ8",
    ];

    private static readonly IReadOnlyDictionary<string, string> LegacyStemToPanel =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ym2608-fm1"] = LegacyPanelIds[0],
            ["ym2608-fm2"] = LegacyPanelIds[1],
            ["ym2608-fm3"] = LegacyPanelIds[2],
            ["ym2608-fm4"] = LegacyPanelIds[3],
            ["ym2608-fm5"] = LegacyPanelIds[4],
            ["ym2608-fm6"] = LegacyPanelIds[5],
            ["ym2608-ssg1"] = LegacyPanelIds[6],
            ["ym2608-ssg2"] = LegacyPanelIds[7],
            ["ym2608-ssg3"] = LegacyPanelIds[8],
            ["ym2608-rhythm"] = LegacyPanelIds[9],
            ["ym2608-adpcm"] = LegacyAdpcmPanelId,
            ["ppz8-01"] = LegacyPpz8PanelId,
        };

    public static bool TryGetStemPanel(string stemName, out string panelId) =>
        LegacyStemToPanel.TryGetValue(stemName, out panelId);

    /// <summary>
    /// Adapts old captures that have FM3 operator events but no typed operator
    /// voice descriptors. This legacy ID interpretation belongs at the data
    /// boundary; generic topology and renderers consume the returned list.
    /// </summary>
    public static IReadOnlyList<string> FindLegacyOperatorVoiceIds(
        VisualizationTimeline timeline,
        DeviceId device)
    {
        string prefix = $"{device}.fm3.op.";
        return timeline.Notes
            .Where(note => note.Mode == VisualizationNoteMode.Fm3Operator
                && note.ChannelId.StartsWith(prefix, StringComparison.Ordinal))
            .Select(note => note.ChannelId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool HasLegacyActivity(VisualizationTimeline timeline, string panelId) =>
        string.Equals(panelId, LegacyAdpcmPanelId, StringComparison.Ordinal)
            ? timeline.AdpcmB.Any(evt => evt.EndSample > evt.StartSample)
            : string.Equals(panelId, LegacyPpz8PanelId, StringComparison.Ordinal)
                && timeline.Ppz8.Any(evt => evt.EndSample > evt.StartSample);

    public static bool IsFixedEightVoiceTimeline(VisualizationTimeline timeline)
    {
        if (timeline.Voices.Count != 8)
            return false;

        if (timeline.Devices.Count == 1)
        {
            DeviceId device = timeline.Devices[0].Id;
            return device.Type == ChipType.SnesDsp
                && timeline.Voices.All(voice => voice.Id.Device == device);
        }

        return timeline.Devices.Count == 0
            && timeline.Voices.All(voice => voice.Id.Device.Type == ChipType.SnesDsp)
            && timeline.Voices.Select(voice => voice.Id.Device).Distinct().Count() == 1;
    }

    public static VisualizationTopology BuildFixedEightVoiceTopology(
        IReadOnlyList<VoiceDescriptor> voices)
    {
        VoiceDescriptor[] ordered = voices
            .OrderBy(voice => voice.Order)
            .ThenBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
            .ToArray();
        var panels = new VisualizationPanel[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            VoiceDescriptor voice = ordered[index];
            PreparedPanelKind kind = VisualizationTopologyBuilder.MapKind(voice);
            panels[index] = new VisualizationPanel(
                voice.Id.ToString(), voice.DisplayName,
                kind,
                PanelContentKind.SingleVoice, index,
                [voice.Id.ToString()], Array.Empty<string>())
            {
                Schema = VisualizationTopologyBuilder.SchemaFor(kind),
            };
        }
        return new VisualizationTopology(panels);
    }

    public static VisualizationTopology BuildLegacyTopology()
    {
        var panels = new VisualizationPanel[LegacyPanelIds.Length];
        for (int index = 0; index < panels.Length; index++)
        {
            PreparedPanelKind kind = index switch
            {
                2 => PreparedPanelKind.Fm3,
                >= 0 and <= 5 => PreparedPanelKind.Pitched,
                >= 6 and <= 8 => PreparedPanelKind.Ssg,
                9 => PreparedPanelKind.Rhythm,
                10 or 11 => PreparedPanelKind.PcmVoice,
                _ => PreparedPanelKind.Placeholder,
            };
            var operators = kind == PreparedPanelKind.Fm3
                ? Enumerable.Range(1, 4).Select(op => $"ym2608.0.fm3.op.{op}").ToArray()
                : Array.Empty<string>();
            panels[index] = new VisualizationPanel(
                LegacyPanelIds[index], LegacyPanelLabels[index], kind,
                index == 11
                    ? PanelContentKind.DeviceAggregate
                    : kind == PreparedPanelKind.Rhythm
                        ? PanelContentKind.PercussionGroup
                        : PanelContentKind.SingleVoice,
                index, [LegacyPanelIds[index]], operators)
            {
                Schema = VisualizationTopologyBuilder.SchemaFor(kind),
                Rows = kind == PreparedPanelKind.Rhythm
                    ? [
                        new PanelRowDefinition("bd", "BD"),
                        new PanelRowDefinition("sd", "SD"),
                        new PanelRowDefinition("top", "TOP"),
                        new PanelRowDefinition("hh", "HH"),
                        new PanelRowDefinition("tom", "TOM"),
                        new PanelRowDefinition("rim", "RIM"),
                    ]
                    : kind == PreparedPanelKind.Fm3
                        ? Enumerable.Range(1, 4)
                            .Select(op => new PanelRowDefinition($"op{op}", $"OP{op}"))
                            .ToArray()
                        : index == 11
                            ? Enumerable.Range(0, 8)
                                .Select(channel => new PanelRowDefinition($"ch{channel}", channel.ToString()))
                                .ToArray()
                        : Array.Empty<PanelRowDefinition>(),
            };
        }
        return new VisualizationTopology(panels);
    }
}
