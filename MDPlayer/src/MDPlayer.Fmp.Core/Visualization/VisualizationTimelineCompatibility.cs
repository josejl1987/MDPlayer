namespace Fmp.Core.Visualization;

/// <summary>
/// Upgrades pre-generic timelines at the preparation boundary. This keeps old
/// serialized captures readable while ensuring renderers see one event model.
/// </summary>
internal static class VisualizationTimelineCompatibility
{
    public static bool HasLegacyRenderableContent(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return timeline.Ppz8.Length > 0 || timeline.AdpcmB.Length > 0;
    }

    public static VisualizationTimeline Upgrade(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Ppz8.Length == 0 && timeline.AdpcmB.Length == 0)
            return timeline;

        var builder = new TimelineBuilder(timeline.SampleRate);
        builder.Merge(timeline);
        return builder.Build(timeline.EndSample, timeline.StopReason, timeline.Source);
    }

    /// <summary>
    /// Resolves rhythm ownership at the semantic compatibility boundary. New
    /// decoders populate <see cref="RhythmEvent.ParentVoiceId"/>; old captures
    /// may only have encoded the parent in the child channel ID.
    /// </summary>
    public static bool RhythmBelongsToVoice(
        VisualizationTimeline timeline,
        RhythmEvent rhythm,
        string voiceId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(rhythm);
        ArgumentNullException.ThrowIfNull(voiceId);

        if (string.Equals(rhythm.ChannelId, voiceId, StringComparison.Ordinal)
            || string.Equals(rhythm.ParentVoiceId, voiceId, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(rhythm.ParentVoiceId))
            return false;

        // Compatibility for pre-descriptor captures. This interpretation is
        // intentionally kept outside topology and renderer code.
        bool knownPercussionVoice = timeline.Voices.Count == 0
            ? string.Equals(voiceId, "ym2608.0.rhythm", StringComparison.Ordinal)
            : timeline.Voices.Any(voice => voice.IsPercussion
                && string.Equals(voice.Id.ToString(), voiceId, StringComparison.Ordinal));
        return knownPercussionVoice
            && rhythm.ChannelId.StartsWith(voiceId + ".", StringComparison.Ordinal);
    }
}
