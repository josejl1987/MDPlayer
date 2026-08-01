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
        try
        {
            request = JsonSerializer.Deserialize<VisualizationRequest>(json, RequestJson.Options);
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

/// <summary>Thrown for request JSON/schema problems.</summary>
public sealed class VisualizationRequestException : Exception
{
    public VisualizationRequestException(string message) : base(message) { }
    public VisualizationRequestException(string message, Exception inner) : base(message, inner) { }
}
