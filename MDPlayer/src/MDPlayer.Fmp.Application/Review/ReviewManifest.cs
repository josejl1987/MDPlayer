using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fmp.Application.Review;

public sealed record ReviewManifest
{
    public IReadOnlyList<ReviewFileEntry> Files { get; init; } = Array.Empty<ReviewFileEntry>();
}

public sealed record ReviewFileEntry
{
    public required string Path { get; init; }
    public string? Label { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ReviewMoment> Moments { get; init; } = Array.Empty<ReviewMoment>();
}

public sealed record ReviewMoment
{
    public required string Name { get; init; }
    public required double TimeSeconds { get; init; }
}

public sealed class ReviewManifestException : Exception
{
    public ReviewManifestException(string message) : base(message) { }
    public ReviewManifestException(string message, Exception inner) : base(message, inner) { }
}

public static class ReviewManifestReader
{
    private const string NumericMomentName = "__numeric_moment__";

    public static ReviewManifest Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ReviewManifestException("manifest path is required");
        if (!File.Exists(path))
            throw new ReviewManifestException($"manifest not found: {path}");

        try
        {
            ReviewManifest? manifest = JsonSerializer.Deserialize<ReviewManifest>(
                File.ReadAllText(path), JsonOptions);
            if (manifest is null)
                throw new ReviewManifestException("manifest is empty");
            Validate(manifest);
            return Normalize(manifest);
        }
        catch (ReviewManifestException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ReviewManifestException($"manifest JSON is malformed: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ReviewManifestException($"could not read manifest: {ex.Message}", ex);
        }
    }

    public static void Validate(ReviewManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        for (int fileIndex = 0; fileIndex < manifest.Files.Count; fileIndex++)
        {
            ReviewFileEntry file = manifest.Files[fileIndex];
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                throw new ReviewManifestException($"files[{fileIndex}].path is required");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int momentIndex = 0; momentIndex < file.Moments.Count; momentIndex++)
            {
                ReviewMoment moment = file.Moments[momentIndex];
                if (moment is null || !double.IsFinite(moment.TimeSeconds) || moment.TimeSeconds < 0)
                    throw new ReviewManifestException(
                        $"files[{fileIndex}].moments[{momentIndex}].time must be a finite non-negative number");
                if (moment.Name != NumericMomentName && string.IsNullOrWhiteSpace(moment.Name))
                    throw new ReviewManifestException(
                        $"files[{fileIndex}].moments[{momentIndex}].name is required");
                if (moment.Name != NumericMomentName && !names.Add(moment.Name))
                    throw new ReviewManifestException(
                        $"files[{fileIndex}] contains duplicate moment name '{moment.Name}'");
            }
        }
    }

    private static ReviewManifest Normalize(ReviewManifest manifest)
    {
        return manifest with
        {
            Files = manifest.Files.Select(file => file with
            {
                Moments = file.Moments.Select((moment, index) =>
                    moment.Name == NumericMomentName
                        ? moment with { Name = $"moment-{index + 1}" }
                        : moment).ToArray(),
            }).ToArray(),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new ReviewMomentConverter());
        return options;
    }

    private sealed class ReviewMomentConverter : JsonConverter<ReviewMoment>
    {
        public override ReviewMoment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double seconds))
            {
                return new ReviewMoment { Name = NumericMomentName, TimeSeconds = seconds };
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("a review moment must be a number or object");

            string? name = null;
            double? time = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("invalid review moment object");
                string property = reader.GetString() ?? "";
                reader.Read();
                switch (property.ToLowerInvariant())
                {
                    case "name": name = reader.GetString(); break;
                    case "time":
                    case "timeseconds":
                        if (!reader.TryGetDouble(out double value))
                            throw new JsonException("moment time must be a number");
                        time = value;
                        break;
                    default: reader.Skip(); break;
                }
            }

            return new ReviewMoment
            {
                Name = name ?? "",
                TimeSeconds = time ?? double.NaN,
            };
        }

        public override void Write(Utf8JsonWriter writer, ReviewMoment value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteNumber("time", value.TimeSeconds);
            writer.WriteEndObject();
        }
    }
}
