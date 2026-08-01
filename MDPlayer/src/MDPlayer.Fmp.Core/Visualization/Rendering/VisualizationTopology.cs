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
    IReadOnlyList<string> OperatorVoiceIds);

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
    /// <summary>Number of voices the SNES S-DSP always exposes (§11.2/§21.1).</summary>
    private const int SnesDspVoiceCount = 8;

    public static VisualizationTopology Build(VisualizationTimeline timeline)
        => Build(timeline, VisualizationLayoutMode.Diagnostic);

    public static VisualizationTopology Build(
        VisualizationTimeline timeline,
        VisualizationLayoutMode layoutMode)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Voices.Count == 0)
        {
            VisualizationTopology legacy = LegacyFmpTopology();
            return layoutMode == VisualizationLayoutMode.Focus
                ? Focus(legacy, timeline)
                : legacy;
        }

        // SNES S-DSP: a dedicated fixed 4-column x 2-row grid ordered
        // VOICE 1..8 (§21.1). The arrangement is authoritative and voices are
        // never reordered by activity. This is a chip-type branch (not a
        // format-name branch), so the generic FMP fallback stays untouched.
        if (IsSnesDspTimeline(timeline))
            return SnesDspTopology(timeline.Voices);

        var source = timeline.Voices
            .OrderBy(voice => DevicePriority(voice.Id.Device.Type))
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
            {
                string prefix = $"{voice.Id.Device}.fm3.op.";
                operatorIds.AddRange(timeline.Notes
                    .Where(note => note.ChannelId.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(note => note.ChannelId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal));
            }

            panels.Add(new VisualizationPanel(
                voice.Id.ToString(),
                voice.DisplayName,
                kind,
                voice.IsPercussion ? PanelContentKind.PercussionGroup : PanelContentKind.SingleVoice,
                panels.Count,
                [voice.Id.ToString()],
                operatorIds));
        }

        if (panels.Count == 0)
            return LegacyFmpTopology();

        VisualizationPanel[] ordered = panels.OrderBy(panel => panel.Order).ToArray();
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

    private static bool HasActivity(VisualizationTimeline timeline, VisualizationPanel panel)
    {
        var ids = new HashSet<string>(panel.VoiceIds.Concat(panel.OperatorVoiceIds), StringComparer.Ordinal);
        if (timeline.Notes.Any(note => ids.Contains(note.ChannelId)))
            return true;

        if (panel.Content == PanelContentKind.PercussionGroup
            && timeline.Rhythm.Any(evt => ids.Contains(evt.ChannelId)
                || panel.VoiceIds.Any(id => evt.ChannelId.StartsWith(id + ".", StringComparison.Ordinal))))
            return true;

        if (panel.Id.EndsWith(".adpcm-b", StringComparison.Ordinal)
            && timeline.AdpcmB.Any(evt => evt.EndSample > evt.StartSample))
            return true;

        return string.Equals(panel.Id, "ppz8.0", StringComparison.Ordinal)
            && timeline.Ppz8.Any(evt => evt.EndSample > evt.StartSample);
    }

    /// <summary>
    /// True when the timeline is a pure SNES S-DSP session: exactly one
    /// S-DSP device with its eight stable voices (§11.2). The voice list may
    /// omit the device descriptor (hand-built timelines), in which case all
    /// eight voices must share a single S-DSP device.
    /// </summary>
    private static bool IsSnesDspTimeline(VisualizationTimeline timeline)
    {
        if (timeline.Voices.Count != SnesDspVoiceCount)
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

    /// <summary>
    /// Builds the fixed 4-column x 2-row SNES S-DSP grid (§21.1): panels 0-3
    /// are the top row, panels 4-7 the bottom row, always in VOICE 1..8
    /// order. Pitch-capable voices get the pitched panel kind.
    /// </summary>
    private static VisualizationTopology SnesDspTopology(IReadOnlyList<VoiceDescriptor> voices)
    {
        VoiceDescriptor[] ordered = voices
            .OrderBy(voice => voice.Order)
            .ThenBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
            .ToArray();

        var panels = new VisualizationPanel[ordered.Length];
        for (int index = 0; index < ordered.Length; index++)
        {
            VoiceDescriptor voice = ordered[index];
            panels[index] = new VisualizationPanel(
                voice.Id.ToString(),
                voice.DisplayName,
                voice.SupportsPitch ? PreparedPanelKind.Pitched : PreparedPanelKind.Placeholder,
                PanelContentKind.SingleVoice,
                index,
                [voice.Id.ToString()],
                Array.Empty<string>());
        }
        return new VisualizationTopology(panels);
    }

    private static PreparedPanelKind MapKind(VoiceDescriptor voice) =>
        voice.Presentation switch
        {
            VoicePresentationKind.Fm3 => PreparedPanelKind.Fm3,
            VoicePresentationKind.Fm or VoicePresentationKind.Pitched => PreparedPanelKind.Pitched,
            VoicePresentationKind.Psg => PreparedPanelKind.Ssg,
            VoicePresentationKind.Midi => PreparedPanelKind.Pitched,
            VoicePresentationKind.Percussion => PreparedPanelKind.Rhythm,
            VoicePresentationKind.Pcm => voice.SupportsPitch
                ? PreparedPanelKind.Pitched
                : voice.IsPercussion
                    ? PreparedPanelKind.Rhythm
                    : PreparedPanelKind.Placeholder,
            _ when voice.Id.Kind is VoiceKind.Noise => PreparedPanelKind.Placeholder,
            _ => PreparedPanelKind.Placeholder,
        };

    private static int DevicePriority(ChipType type) => type switch
    {
        ChipType.Ym2203 or ChipType.Ym2608 or ChipType.Ym2610 or ChipType.Ym2612 or ChipType.Ym2151 or ChipType.Ymf278b or ChipType.Ymz280b => 0,
        ChipType.Sn76489 or ChipType.Ay8910 or ChipType.Dmg or ChipType.NesApu or ChipType.Huc6280 or ChipType.K051649 => 10,
        ChipType.Okim6258 or ChipType.Okim6295 => 30,
        ChipType.MultiPcm => 30,
        ChipType.Midi => 20,
        ChipType.Ppz8 or ChipType.Pcm or ChipType.SnesDsp => 30,
        _ => 100,
    };

    private static VisualizationTopology LegacyFmpTopology()
    {
        string[] ids =
        [
            "ym2608.0.fm.1", "ym2608.0.fm.2", "ym2608.0.fm.3",
            "ym2608.0.fm.4", "ym2608.0.fm.5", "ym2608.0.fm.6",
            "ym2608.0.ssg.1", "ym2608.0.ssg.2", "ym2608.0.ssg.3",
            "ym2608.0.rhythm", "ym2608.0.adpcm-b", "ppz8.0",
        ];
        string[] labels =
        ["FM1", "FM2", "FM3", "FM4", "FM5", "FM6", "SSG1", "SSG2", "SSG3", "RHYTHM", "ADPCM-B", "PPZ8"];
        var panels = new VisualizationPanel[ids.Length];
        for (int index = 0; index < ids.Length; index++)
        {
            PreparedPanelKind kind = index switch
            {
                2 => PreparedPanelKind.Fm3,
                >= 0 and <= 5 => PreparedPanelKind.Pitched,
                >= 6 and <= 8 => PreparedPanelKind.Ssg,
                9 => PreparedPanelKind.Rhythm,
                _ => PreparedPanelKind.Placeholder,
            };
            var operators = kind == PreparedPanelKind.Fm3
                ? Enumerable.Range(1, 4).Select(op => $"ym2608.0.fm3.op.{op}").ToArray()
                : Array.Empty<string>();
            panels[index] = new VisualizationPanel(
                ids[index],
                labels[index],
                kind,
                kind == PreparedPanelKind.Rhythm ? PanelContentKind.PercussionGroup : PanelContentKind.SingleVoice,
                index,
                [ids[index]],
                operators);
        }
        return new VisualizationTopology(panels);
    }
}
