using Fmp.Application.Contracts;
using Fmp.Core.Metadata;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Semantic (non-console) presentation/timeline helpers shared by the CLI and
/// the GUI preview pipeline. Lives in the application assembly so
/// <c>VisualizationPrepareCoordinator</c> does not depend on the CLI-only
/// <c>VisualizationSupport</c> (which also hosts console/progress helpers).
/// </summary>
internal static class VisualizationPresentationSupport
{
    public static VisualizationPresentation ResolvePresentation(VisualizationRequest request, FileInfo input)
    {
        string title = request.Presentation.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            try
            {
                title = FmpMetadata.FromFmpFile(input.FullName, request.Playback.SampleRate, request.Playback.LoopCount, request.Playback.FadeSeconds).Title;
            }
            catch { title = null; }
        }
        title = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(input.Name)
            : title.Trim();
        return new VisualizationPresentation(
            title,
            string.IsNullOrWhiteSpace(request.Presentation.Subtitle) ? "" : request.Presentation.Subtitle.Trim(),
            string.IsNullOrWhiteSpace(request.Presentation.Credits) ? "" : request.Presentation.Credits.Trim());
    }

    public static VisualizationTimeline AlignTimelineToAudio(
        VisualizationTimeline timeline,
        long audioEndSample)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (audioEndSample < timeline.StartSample)
            throw new ArgumentOutOfRangeException(nameof(audioEndSample));

        NoteEvent[] notes = timeline.Notes
            .Where(note => note.StartSample < audioEndSample)
            .Select(note => note with
            {
                EndSample = Math.Min(note.EndSample, audioEndSample),
                Pitch = note.Pitch.Where(point => point.SamplePosition < audioEndSample).ToArray(),
            })
            .Where(note => note.EndSample > note.StartSample)
            .ToArray();
        RhythmEvent[] rhythm = timeline.Rhythm
            .Where(evt => evt.SamplePosition < audioEndSample).ToArray();
        Ppz8Event[] ppz8 = timeline.Ppz8
            .Where(evt => evt.StartSample < audioEndSample)
            .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
            .Where(evt => evt.EndSample > evt.StartSample).ToArray();
        AdpcmBEvent[] adpcm = timeline.AdpcmB
            .Where(evt => evt.StartSample < audioEndSample)
            .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
            .Where(evt => evt.EndSample > evt.StartSample).ToArray();
        SamplePlaybackEvent[] samplePlayback = timeline.SamplePlayback
            .Where(evt => evt.StartSample < audioEndSample)
            .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
            .Where(evt => evt.EndSample > evt.StartSample).ToArray();

        return new VisualizationTimeline
        {
            SampleRate = timeline.SampleRate,
            StartSample = timeline.StartSample,
            EndSample = audioEndSample,
            SchemaVersion = timeline.SchemaVersion,
            Source = timeline.Source,
            StopReason = timeline.StopReason,
            Devices = timeline.Devices,
            Voices = timeline.Voices,
            Notes = notes,
            Rhythm = rhythm,
            Ppz8 = ppz8,
            AdpcmB = adpcm,
            Waveforms = timeline.Waveforms,
            WaveformChanges = timeline.WaveformChanges
                .Where(evt => evt.SamplePosition < audioEndSample).ToArray(),
            Samples = timeline.Samples,
            SamplePlayback = samplePlayback,
            DacActivity = timeline.DacActivity
                .Where(evt => evt.StartSample < audioEndSample)
                .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
                .Where(evt => evt.EndSample > evt.StartSample).ToArray(),
            DacHits = timeline.DacHits
                .Where(evt => evt.StartSample < audioEndSample)
                .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
                .Where(evt => evt.EndSample > evt.StartSample).ToArray(),
            SpcVoiceStates = timeline.SpcVoiceStates
                .Where(evt => evt.SamplePosition < audioEndSample).ToArray(),
            NoiseStates = timeline.NoiseStates
                .Where(evt => evt.StartSample < audioEndSample)
                .Select(evt => evt with { EndSample = Math.Min(evt.EndSample, audioEndSample) })
                .Where(evt => evt.EndSample > evt.StartSample).ToArray(),
            AggregateHits = timeline.AggregateHits
                .Where(evt => evt.SamplePosition < audioEndSample).ToArray(),
            Timing = timeline.Timing.Where(evt => evt.SamplePosition < audioEndSample).ToArray(),
            Beats = timeline.Beats.Where(evt => evt.SamplePosition < audioEndSample).ToArray(),
            LoopMarkers = timeline.LoopMarkers.Where(marker => marker.SamplePosition < audioEndSample).ToArray(),
            Instruments = timeline.Instruments,
            Capabilities = timeline.Capabilities,
            Warnings = timeline.Warnings,
        };
    }
}
