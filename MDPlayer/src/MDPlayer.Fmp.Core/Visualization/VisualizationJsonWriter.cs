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

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);

        string tempPath = fullPath + ".partial";
        try
        {
            using (var stream = File.Create(tempPath))
                JsonSerializer.Serialize(stream, timeline, Options);

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
        return JsonSerializer.Serialize(timeline, Options);
    }

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
        return JsonSerializer.Deserialize<VisualizationTimeline>(root.ToJsonString(), Options)
            ?? throw new JsonException("Visualization timeline is empty.");
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
}
