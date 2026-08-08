using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fmp.Core.Visualization;

internal static class VisualizationJsonWriter
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static void Write(string path, VisualizationTimeline timeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeline);
        VisualizationTimelineValidator.Validate(timeline);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);

        string tempPath = fullPath + ".partial";
        try
        {
            using (var stream = File.Create(tempPath))
                JsonSerializer.Serialize(stream, Ordered(timeline), Options);

            if (File.Exists(fullPath))
                File.Delete(fullPath);
            File.Move(tempPath, fullPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static string Serialize(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        VisualizationTimelineValidator.Validate(timeline);
        return JsonSerializer.Serialize(Ordered(timeline), Options);
    }

    /// <summary>
    /// Reads a serialized <see cref="VisualizationTimeline"/>. Structural bounds
    /// are validated here, but the sample clock is deliberately NOT required to be
    /// positive: an ambiguous/missing clock must reach
    /// <see cref="TimelineBuilder.Merge"/> and be rejected there by
    /// <see cref="ProducerClockNormalization"/> with an actionable
    /// <see cref="Fmp.Core.Timing.MusicalTimingException"/> (the producer
    /// boundary), not a generic JSON error. Consumers that read a timeline without
    /// routing it through the producer boundary must pass a positive-rate timeline
    /// (or reject it themselves).
    /// </summary>
    public static VisualizationTimeline Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string json = File.ReadAllText(path);
        // Timelines written by early generic backends used channelId while the
        // stable v2 contract uses voiceId. Accept both on input; serialization
        // remains intentionally unchanged for existing consumers and tests.
        JsonNode root = JsonNode.Parse(json)
            ?? throw new JsonException("Visualization timeline is empty.");
        if (root["notes"] is JsonArray notes)
        {
            foreach (JsonNode note in notes)
            {
                if (note is JsonObject obj && obj["voiceId"] is null && obj["channelId"] is not null)
                    obj["voiceId"] = obj["channelId"]?.DeepClone();
            }
        }
        VisualizationTimeline timeline = JsonSerializer.Deserialize<VisualizationTimeline>(root.ToJsonString(), Options)
            ?? throw new JsonException("Visualization timeline is empty.");
        VisualizationTimelineValidator.Validate(timeline, requirePositiveSampleRate: false);
        return timeline;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DeviceIdJsonConverter());
        options.Converters.Add(new VoiceIdJsonConverter());
        return options;
    }

    private static VisualizationTimeline Ordered(VisualizationTimeline timeline) => new()
    {
        SchemaVersion = timeline.SchemaVersion,
        SampleRate = timeline.SampleRate,
        StartSample = timeline.StartSample,
        EndSample = timeline.EndSample,
        Source = timeline.Source,
        StopReason = timeline.StopReason,
        Devices = (timeline.Devices ?? [])
            .OrderBy(value => DeviceOrdering.Priority(value.Id.Type))
            .ThenBy(value => value.Id.Instance)
            .ThenBy(value => value.Id.ToString(), StringComparer.Ordinal)
            .ToArray(),
        Voices = (timeline.Voices ?? [])
            .OrderBy(value => DeviceOrdering.Priority(value.Id.Device.Type))
            .ThenBy(value => value.Id.Device.Instance)
            .ThenBy(value => value.Id.Device.ToString(), StringComparer.Ordinal)
            .ThenBy(value => value.Order)
            .ThenBy(value => value.Id.ToString(), StringComparer.Ordinal)
            .ToArray(),
        Notes = (timeline.Notes ?? [])
            .OrderBy(value => value.StartSample)
            .ThenBy(value => value.ChannelId, StringComparer.Ordinal)
            .ThenBy(value => value.EndSample)
            .ToArray(),
        Rhythm = (timeline.Rhythm ?? [])
            .OrderBy(value => value.SamplePosition)
            .ThenBy(value => value.ChannelId, StringComparer.Ordinal)
            .ThenBy(value => value.Voice, StringComparer.Ordinal)
            .ToArray(),
        Ppz8 = (timeline.Ppz8 ?? [])
            .OrderBy(value => value.StartSample)
            .ThenBy(value => value.Channel)
            .ThenBy(value => value.EndSample)
            .ToArray(),
        AdpcmB = (timeline.AdpcmB ?? [])
            .OrderBy(value => value.StartSample)
            .ThenBy(value => value.EndSample)
            .ThenBy(value => value.StartAddress)
            .ToArray(),
        Waveforms = (timeline.Waveforms ?? []).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
        Samples = (timeline.Samples ?? []).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
        WaveformChanges = (timeline.WaveformChanges ?? [])
            .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
            .ThenBy(value => value.SamplePosition)
            .ThenBy(value => value.WaveformId, StringComparer.Ordinal)
            .ToArray(),
        SamplePlayback = (timeline.SamplePlayback ?? [])
            .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
            .ThenBy(value => value.StartSample)
            .ThenBy(value => value.SampleId, StringComparer.Ordinal)
            .ThenBy(value => value.EndSample)
            .ThenBy(value => value.PlaybackRate)
            .ThenBy(value => value.Gain)
            .ThenBy(value => value.Pan)
            .ThenBy(value => value.Retrigger)
            .ToArray(),
        SpcVoiceStates = (timeline.SpcVoiceStates ?? [])
            .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
            .ThenBy(value => value.SamplePosition)
            .ThenBy(value => value.State, StringComparer.Ordinal)
            .ThenBy(value => value.Value)
            .ThenBy(value => value.Value2)
            .ToArray(),
        NoiseStates = (timeline.NoiseStates ?? [])
            .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
            .ThenBy(value => value.StartSample)
            .ThenBy(value => value.EndSample)
            .ThenBy(value => value.Mode)
            .ToArray(),
        AggregateHits = (timeline.AggregateHits ?? [])
            .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
            .ThenBy(value => value.SamplePosition)
            .ThenBy(value => value.SubVoiceId, StringComparer.Ordinal)
            .ThenBy(value => value.Label, StringComparer.Ordinal)
            .ThenBy(value => value.AssetId, StringComparer.Ordinal)
            .ToArray(),
        Timing = (timeline.Timing ?? [])
            .OrderBy(value => value.SamplePosition)
            .ThenBy(value => value.TimerBValue)
            .ToArray(),
        Beats = (timeline.Beats ?? [])
            .OrderBy(value => value.SamplePosition)
            .ThenBy(value => value.BeatIndex)
            .ToArray(),
        LoopMarkers = (timeline.LoopMarkers ?? [])
            .OrderBy(value => value.SamplePosition)
            .ThenBy(value => value.Kind)
            .ThenBy(value => value.Iteration)
            .ToArray(),
        Instruments = (timeline.Instruments ?? [])
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray(),
        Capabilities = (timeline.Capabilities ?? []).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
        Warnings = (timeline.Warnings ?? []).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
    };
}
