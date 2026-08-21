#nullable enable

using Fmp.Core.Visualization.Rendering;

namespace Fmp.Core.Midi;

/// <summary>Immutable semantic binding between one source voice and one SMF track.</summary>
internal sealed record MidiTrackBinding(string SourceVoiceId, MidiTrack Track);

/// <summary>
/// Projects the existing source-faithful MIDI tracks onto presentation panels.
/// Matching is by semantic source identity only; labels and display names are not
/// part of the binding contract. Track input order cannot change panel identity.
/// </summary>
internal static class MidiTrailPanelProjector
{
    public static IReadOnlyList<IReadOnlyList<MidiTrackBinding>> Project(
        VisualizationTopology topology,
        IReadOnlyList<MidiTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(tracks);

        Dictionary<string, MidiTrack> bySource = tracks
            .Where(track => !string.IsNullOrEmpty(track.SourceVoiceId))
            .GroupBy(track => track.SourceVoiceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(track => track.Name, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);

        var result = new IReadOnlyList<MidiTrackBinding>[topology.Panels.Count];
        for (int panelIndex = 0; panelIndex < topology.Panels.Count; panelIndex++)
        {
            VisualizationPanel panel = topology.Panels[panelIndex];
            string[] sourceIds = panel.VoiceIds
                .Concat(panel.OperatorVoiceIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            result[panelIndex] = sourceIds
                .Where(bySource.ContainsKey)
                .Select(id => new MidiTrackBinding(id, bySource[id]))
                .ToArray();
        }
        return result;
    }
}
