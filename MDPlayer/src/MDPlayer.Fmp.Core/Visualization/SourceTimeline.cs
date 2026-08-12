namespace Fmp.Core.Visualization;

/// <summary>Read-only, indexed view over a validated timeline. The source timeline
/// remains the compatibility boundary; this type owns no event copies.</summary>
internal sealed class SourceTimeline
{
    private SourceTimeline(VisualizationTimeline timeline)
    {
        Timeline = timeline;
        Notes = timeline.Notes ?? Array.Empty<NoteEvent>();
        Rhythm = timeline.Rhythm ?? Array.Empty<RhythmEvent>();
    }

    public VisualizationTimeline Timeline { get; }
    public IReadOnlyList<NoteEvent> Notes { get; }
    public IReadOnlyList<RhythmEvent> Rhythm { get; }
    public static SourceTimeline Create(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        VisualizationTimelineValidator.Validate(timeline);
        return new SourceTimeline(timeline);
    }
}
