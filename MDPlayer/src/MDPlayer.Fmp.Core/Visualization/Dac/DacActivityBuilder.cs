namespace Fmp.Core.Visualization;

/// <summary>
/// Derives bounded visual activity slices from a DAC playback event without
/// changing the event's playback semantics. A long explicit VGM stream can be
/// one valid sample playback while still needing multiple visual activity
/// cells so the renderer can show its contour.
/// </summary>
internal static class DacActivityBuilder
{
    public static IReadOnlyList<DacActivityEvent> Build(
        IReadOnlyList<SamplePlaybackEvent> playback,
        IReadOnlyDictionary<string, SampleDefinition> samples)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(samples);

        var result = new List<DacActivityEvent>();
        foreach (SamplePlaybackEvent value in playback)
        {
            if (!value.VoiceId.Equals("ym2612.0.pcm.dac", StringComparison.Ordinal)
                || value.EndSample <= value.StartSample
                || !samples.TryGetValue(value.SampleId, out SampleDefinition sample)
                || sample.Preview is not { Length: > 0 } preview)
            {
                continue;
            }

            long duration = value.EndSample - value.StartSample;
            for (int index = 0; index < preview.Length; index++)
            {
                WaveformEnvelopePoint point = preview[index];
                float level = Math.Clamp(
                    Math.Max(Math.Abs(point.Minimum), Math.Abs(point.Maximum)),
                    0,
                    1);
                if (level <= 0.01f)
                    continue;

                long start = value.StartSample + (long)Math.Round(
                    duration * (index / (double)preview.Length));
                long end = value.StartSample + (long)Math.Round(
                    duration * ((index + 1) / (double)preview.Length));
                end = Math.Max(start + 1, end);
                result.Add(new DacActivityEvent(
                    value.VoiceId,
                    start,
                    Math.Min(value.EndSample, end),
                    value.SampleId,
                    level));
            }
        }

        return result;
    }
}
