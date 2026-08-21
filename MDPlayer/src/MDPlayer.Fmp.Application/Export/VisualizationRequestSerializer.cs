using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Preview;

namespace Fmp.Application.Export;

/// <summary>
/// Serializes/deserializes <see cref="VisualizationRequest"/> to the canonical
/// JSON form (camelCase, string enums, schemaVersion). Shared by the CLI
/// (--request-json) and the GUI (project files, preview sessions).
/// </summary>
public static class VisualizationRequestSerializer
{
    public const int MaxSchemaVersion = 1;

    public static string Serialize(VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(request, RequestJson.Options);
    }

    public static VisualizationRequest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        VisualizationRequest? request;
        bool hasComposition;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            hasComposition = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, "composition", StringComparison.OrdinalIgnoreCase));
            request = document.Deserialize<VisualizationRequest>(RequestJson.Options);
        }
        catch (JsonException ex)
        {
            throw new VisualizationRequestException("Request JSON is malformed: " + ex.Message, ex);
        }

        if (request is null)
            throw new VisualizationRequestException("Request JSON is empty.");

        if (request.SchemaVersion > MaxSchemaVersion)
        {
            throw new VisualizationRequestException(
                $"Request schema {request.SchemaVersion} is newer than this build supports ({MaxSchemaVersion}).");
        }

        // Composition was added to the schema-v1 request contract after
        // existing files were written. Preserve their established meaning
        // explicitly rather than relying on the enum's underlying zero value.
        if (!hasComposition)
            request = request with { Composition = CompositionKind.Diagnostic };

        return request;
    }

    /// <summary>Serializes and writes the request file atomically (temp + move).</summary>
    public static string WriteToFile(VisualizationRequest request, string path)
    {
        ArgumentNullException.ThrowIfNull(request);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        string temp = fullPath + ".tmp";
        File.WriteAllText(temp, Serialize(request));
        File.Move(temp, fullPath, overwrite: true);
        return fullPath;
    }

    public static VisualizationRequest ReadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new VisualizationRequestException($"Request file not found: {path}");
        return Deserialize(File.ReadAllText(path));
    }
}

/// <summary>
/// Shared JSON serializer options for request/project payloads. Enums as
/// strings, camelCase property names, indented for readability.
/// </summary>
public static class RequestJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            // Registered before the global string-enum converter so the
            // composition rename keeps legacy payloads readable.
            new CompositionKindJsonConverter(),
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
        },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}

/// <summary>
/// String converter for <see cref="CompositionKind"/> that survives the
/// MidiTrail→Performance rename: serialization always writes the canonical
/// name ("Diagnostic"/"Performance"), while deserialization still accepts the
/// legacy "MidiTrail"/"miditrail" spellings found in existing project files
/// and request JSON.
/// </summary>
public sealed class CompositionKindJsonConverter : System.Text.Json.Serialization.JsonConverter<CompositionKind>
{
    public override CompositionKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // Legacy request files written before the string-enum contract used
        // numeric enum values (0 = Diagnostic, 1 = MidiTrail→Performance).
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out int numeric)
                || numeric is < 0 or > 1)
            {
                throw new JsonException($"unknown numeric composition '{reader.GetDecimal()}'");
            }
            return numeric == 0 ? CompositionKind.Diagnostic : CompositionKind.Performance;
        }

        string? raw = reader.GetString();
        return raw?.Trim().ToLowerInvariant() switch
        {
            "diagnostic" => CompositionKind.Diagnostic,
            "performance" or "miditrail" => CompositionKind.Performance,
            _ => throw new JsonException($"unknown composition '{raw}'"),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        CompositionKind value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            CompositionKind.Diagnostic => "Diagnostic",
            CompositionKind.Performance => "Performance",
            _ => value.ToString(),
        });
}

/// <summary>Thrown for request JSON/schema problems.</summary>
public sealed class VisualizationRequestException : Exception
{
    public VisualizationRequestException(string message) : base(message) { }
    public VisualizationRequestException(string message, Exception inner) : base(message, inner) { }
}
