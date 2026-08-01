namespace Fmp.Core.Visualization;

/// <summary>
/// Shared semantic-activity predicates used during layout selection and track
/// filtering. Preparation applies the same validity rule before rendering, so
/// malformed zero-length or non-finite notes cannot keep an empty track in a
/// publishing composition.
/// </summary>
internal static class VisualizationActivity
{
    public static bool IsMeaningfulNote(NoteEvent note)
        => note != null
            && note.EndSample > note.StartSample
            && double.IsFinite(note.InitialMidiNote);

    public static bool IsMeaningfulNote(
        NoteEvent note,
        PitchCoordinateSystem pitchSystem)
        => note != null
            && note.EndSample > note.StartSample
            && pitchSystem switch
            {
                PitchCoordinateSystem.FrequencyHz => double.IsFinite(note.InitialFrequencyHz)
                    && note.InitialFrequencyHz > 0,
                PitchCoordinateSystem.AbsoluteMidi
                    or PitchCoordinateSystem.AbsoluteSemitone
                    or PitchCoordinateSystem.RelativeSemitone => double.IsFinite(note.InitialMidiNote),
                _ => double.IsFinite(note.InitialMidiNote),
            };

    public static bool IsMeaningfulNote(
        VisualizationTimeline timeline,
        NoteEvent note)
    {
        if (note == null)
            return false;

        PitchCoordinateSystem pitchSystem = timeline.Voices
            .Where(voice => string.Equals(
                voice.Id.ToString(), note.ChannelId, StringComparison.Ordinal))
            .Select(voice => voice.PitchSystem)
            .FirstOrDefault(PitchCoordinateSystem.AbsoluteMidi);
        return IsMeaningfulNote(note, pitchSystem);
    }

    public static bool HasMeaningfulNoteOnChannel(
        VisualizationTimeline timeline,
        IReadOnlySet<string> channelIds)
        => timeline.Notes.Any(note => channelIds.Contains(note.ChannelId)
            && IsMeaningfulNote(timeline, note));
}
