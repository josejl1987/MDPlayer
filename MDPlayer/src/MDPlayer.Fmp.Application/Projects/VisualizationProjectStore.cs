using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Preview;

namespace Fmp.Application.Projects;

/// <summary>
/// Saves/loads <see cref="VisualizationProject"/> (.mdpviz.json) with
/// relative-path support and crash recovery files.
/// </summary>
public static class VisualizationProjectStore
{
    public const int MaxSchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = RequestJson.Create();

    public static string Serialize(VisualizationProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        VisualizationProject portable = project with { FilePath = null };
        return JsonSerializer.Serialize(portable, Json);
    }

    /// <summary>
    /// Saves a project at <paramref name="path"/>. Paths inside the project
    /// are relativized against the project directory when they share a common
    /// root, otherwise kept absolute.
    /// </summary>
    public static VisualizationProject Save(VisualizationProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);

        string relativeInput = TryRelativize(directory, project.Request.InputPath);
        string relativeOutput = TryRelativize(directory, project.Request.OutputPath);

        VisualizationProject toWrite = project with
        {
            FilePath = null,
            RelativeInputPath = relativeInput,
            RelativeOutputPath = relativeOutput,
            LastSavedUtc = DateTimeOffset.UtcNow,
        };

        string temp = fullPath + ".tmp";
        File.WriteAllText(temp, Serialize(toWrite));
        File.Move(temp, fullPath, overwrite: true);
        return toWrite with { FilePath = fullPath };
    }

    public static VisualizationProject Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Project file not found.", fullPath);

        VisualizationProject? project;
        try
        {
            project = JsonSerializer.Deserialize<VisualizationProject>(File.ReadAllText(fullPath), Json);
        }
        catch (JsonException ex)
        {
            throw new VisualizationProjectException($"Project JSON is malformed: {ex.Message}", ex);
        }
        if (project is null)
            throw new VisualizationProjectException("Project file is empty.");

        if (project.SchemaVersion > MaxSchemaVersion)
        {
            throw new VisualizationProjectException(
                $"Project schema {project.SchemaVersion} is newer than this build supports ({MaxSchemaVersion}). Open read-only.");
        }

        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        string inputPath = ResolvePath(directory, project.Request.InputPath, project.RelativeInputPath);
        string outputPath = ResolvePath(directory, project.Request.OutputPath, project.RelativeOutputPath);

        return project with
        {
            FilePath = fullPath,
            Request = project.Request with
            {
                InputPath = inputPath,
                OutputPath = outputPath,
            },
        };
    }

    // ---- Recovery ----

    public static void SaveRecovery(VisualizationProject project, string recoveryDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        string directory = recoveryDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "recovery.mdpviz.json");
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(project with { FilePath = null }));
        File.Move(temp, path, overwrite: true);
    }

    public static VisualizationProject? TryLoadRecovery(string recoveryDirectory)
    {
        string path = Path.Combine(recoveryDirectory, "recovery.mdpviz.json");
        if (!File.Exists(path))
            return null;
        try
        {
            return Load(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void ClearRecovery(string recoveryDirectory)
    {
        string path = Path.Combine(recoveryDirectory, "recovery.mdpviz.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    // ---- Helpers ----

    private static string? TryRelativize(string directory, string absolutePath)
    {
        try
        {
            string full = Path.GetFullPath(absolutePath);
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string dirWithSep = dir + Path.DirectorySeparatorChar;
            if (full.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase))
                return full[dirWithSep.Length..];
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePath(string directory, string storedAbsolute, string? relative)
    {
        if (!string.IsNullOrWhiteSpace(relative))
            return Path.GetFullPath(Path.Combine(directory, relative));
        if (!string.IsNullOrWhiteSpace(storedAbsolute))
            return Path.GetFullPath(storedAbsolute);
        return string.Empty;
    }
}

/// <summary>Thrown for project file/schema problems.</summary>
public sealed class VisualizationProjectException : Exception
{
    public VisualizationProjectException(string message) : base(message) { }
    public VisualizationProjectException(string message, Exception inner) : base(message, inner) { }
}
