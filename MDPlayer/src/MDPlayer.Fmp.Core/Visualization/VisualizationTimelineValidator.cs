using System.Text.Json;

namespace Fmp.Core.Visualization;

/// <summary>Validates bounded generic visualization data at the JSON boundary.</summary>
internal static class VisualizationTimelineValidator
{
    /// <summary>
    /// Validates bounded generic visualization data at the JSON boundary.
    /// </summary>
    /// <param name="requirePositiveSampleRate">
    /// When true (the default for <see cref="VisualizationJsonWriter.Write"/> and
    /// <see cref="VisualizationJsonWriter.Serialize"/>) a missing/ambiguous sample
    /// clock is rejected here as a malformed payload. When false (the
    /// serialized-timeline read path) the structural bounds are still checked but
    /// sample-clock positivity is deferred to the producer-clock normalization
    /// boundary (<see cref="TimelineBuilder.Merge"/> →
    /// <see cref="ProducerClockNormalization"/>), which rejects an ambiguous clock
    /// with an actionable <see cref="Fmp.Core.Timing.MusicalTimingException"/>.
    /// </param>
    public static void Validate(VisualizationTimeline timeline, bool requirePositiveSampleRate = true)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.StartSample < 0 || timeline.EndSample < timeline.StartSample)
            throw new JsonException("Invalid visualization timeline bounds.");
        if (requirePositiveSampleRate && timeline.SampleRate <= 0)
            throw new JsonException("Invalid visualization timeline bounds.");

        var waveforms = new Dictionary<string, WaveformDefinition>(StringComparer.Ordinal);
        foreach (WaveformDefinition waveform in timeline.Waveforms ?? [])
        {
            if (string.IsNullOrWhiteSpace(waveform.Id) || string.IsNullOrWhiteSpace(waveform.Family)
                || waveform.SourceLength < 2
                || waveform.Preview is null || waveform.Preview.Length < 2
                || waveform.Preview.Length > VisualizationAssetBuilder.MaximumWaveformPreviewLength)
                throw new JsonException($"Invalid waveform definition '{waveform.Id}'.");
            foreach (float value in waveform.Preview)
                EnsureFiniteRange(value, -1, 1, "waveform preview");
            if (waveforms.TryGetValue(waveform.Id, out WaveformDefinition existing)
                && (!waveform.Preview.AsSpan().SequenceEqual(existing.Preview)
                    || waveform.Family != existing.Family
                    || waveform.SourceLength != existing.SourceLength))
                throw new JsonException($"Waveform id '{waveform.Id}' has conflicting content.");
            waveforms[waveform.Id] = waveform;
        }

        var samples = new Dictionary<string, SampleDefinition>(StringComparer.Ordinal);
        foreach (SampleDefinition sample in timeline.Samples ?? [])
        {
            if (string.IsNullOrWhiteSpace(sample.Id) || string.IsNullOrWhiteSpace(sample.Family)
                || sample.SourceLengthSamples < 0
                || sample.Preview is null || sample.Preview.Length > VisualizationAssetBuilder.MaximumSamplePreviewBins)
                throw new JsonException($"Invalid sample definition '{sample.Id}'.");
            if (!Enum.IsDefined(sample.LoopMode) || !Enum.IsDefined(sample.IdentityKind))
                throw new JsonException($"Invalid sample metadata for '{sample.Id}'.");
            if (sample.NativeSampleRate is <= 0)
                throw new JsonException($"Invalid sample rate for '{sample.Id}'.");
            if (sample.LoopStart is int loopStart && (loopStart < 0 || loopStart > sample.SourceLengthSamples))
                throw new JsonException($"Invalid sample loop start for '{sample.Id}'.");
            if (sample.LoopEnd is int loopEnd && (loopEnd < 0 || loopEnd > sample.SourceLengthSamples))
                throw new JsonException($"Invalid sample loop end for '{sample.Id}'.");
            if (sample.LoopStart is int start && sample.LoopEnd is int end && end < start)
                throw new JsonException($"Invalid sample loop range for '{sample.Id}'.");
            foreach (WaveformEnvelopePoint point in sample.Preview)
            {
                EnsureFiniteRange(point.Minimum, -1, 1, "sample preview minimum");
                EnsureFiniteRange(point.Maximum, -1, 1, "sample preview maximum");
                if (point.Minimum > point.Maximum)
                    throw new JsonException($"Invalid sample envelope for '{sample.Id}'.");
            }
            if (samples.TryGetValue(sample.Id, out SampleDefinition existing)
                && !SampleEquals(existing, sample))
                throw new JsonException($"Sample id '{sample.Id}' has conflicting content.");
            samples[sample.Id] = sample;
        }

        foreach (WaveformChangeEvent change in timeline.WaveformChanges ?? [])
        {
            if (string.IsNullOrWhiteSpace(change.VoiceId) || change.SamplePosition < 0
                || !waveforms.ContainsKey(change.WaveformId))
                throw new JsonException("Invalid waveform change event.");
        }
        foreach (SamplePlaybackEvent playback in timeline.SamplePlayback ?? [])
        {
            if (string.IsNullOrWhiteSpace(playback.VoiceId)
                || playback.StartSample < 0
                || playback.EndSample < playback.StartSample
                || !samples.ContainsKey(playback.SampleId)
                || !double.IsFinite(playback.PlaybackRate) || playback.PlaybackRate <= 0
                || !float.IsFinite(playback.Gain) || playback.Gain < 0
                || !float.IsFinite(playback.Pan) || playback.Pan is < -1 or > 1
                || (playback.MidiPitch is double pitch && !double.IsFinite(pitch)))
                throw new JsonException("Invalid sample playback event.");
        }
        foreach (NoiseStateEvent noise in timeline.NoiseStates ?? [])
        {
            if (string.IsNullOrWhiteSpace(noise.VoiceId)
                || noise.StartSample < 0 || noise.EndSample < noise.StartSample
                || !Enum.IsDefined(noise.Mode)
                || (noise.CentreFrequencyHz is double frequency && (!double.IsFinite(frequency) || frequency < 0))
                || (noise.Period is double period && (!double.IsFinite(period) || period < 0))
                || !float.IsFinite(noise.Level) || noise.Level < 0)
                throw new JsonException("Invalid noise state event.");
        }
        foreach (AggregateHitEvent hit in timeline.AggregateHits ?? [])
        {
            if (string.IsNullOrWhiteSpace(hit.VoiceId) || string.IsNullOrWhiteSpace(hit.SubVoiceId)
                || hit.SamplePosition < 0 || !float.IsFinite(hit.Strength) || hit.Strength < 0
                || !float.IsFinite(hit.Pan) || hit.Pan is < -1 or > 1
                || (!string.IsNullOrEmpty(hit.AssetId) && !samples.ContainsKey(hit.AssetId)
                    && !waveforms.ContainsKey(hit.AssetId)))
                throw new JsonException("Invalid aggregate hit event.");
        }
    }

    private static void EnsureFiniteRange(float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new JsonException($"Invalid {name} value.");
    }

    private static bool SampleEquals(SampleDefinition left, SampleDefinition right) =>
        left.Family == right.Family
        && left.SourceLengthSamples == right.SourceLengthSamples
        && left.NativeSampleRate == right.NativeSampleRate
        && left.LoopStart == right.LoopStart
        && left.LoopEnd == right.LoopEnd
        && left.LoopMode == right.LoopMode
        && left.IdentityKind == right.IdentityKind
        && left.Preview.Length == right.Preview.Length
        && left.Preview.Zip(right.Preview).All(pair =>
            pair.First.Minimum == pair.Second.Minimum && pair.First.Maximum == pair.Second.Maximum);
}
