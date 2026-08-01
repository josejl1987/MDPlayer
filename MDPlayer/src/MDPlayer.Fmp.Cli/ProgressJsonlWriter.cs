using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Emits structured export-progress events as one compact JSON line per event
/// (JSON-lines, camelCase names, string enums). Active only when the visualize
/// command runs with <c>--progress jsonl</c>.
/// </summary>
internal sealed class ProgressJsonlWriter
{
    private readonly TextWriter _output;
    private readonly JsonSerializerOptions _options;

    public ProgressJsonlWriter(TextWriter output)
    {
        _output = output ?? Console.Out;
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public void Emit(ExportProgressEvent evt)
    {
        _output.WriteLine(JsonSerializer.Serialize(evt, _options));
        _output.Flush();
    }

    public void Started()
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.Started, TimestampUtc = Now() });

    public void StageStarted(string stage)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.StageStarted, TimestampUtc = Now(), Stage = stage });

    public void StageCompleted(string stage, double? elapsedSeconds)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.StageCompleted, TimestampUtc = Now(), Stage = stage, ElapsedSeconds = elapsedSeconds });

    public void StageProgress(string stage, double? fraction)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.StageProgress, TimestampUtc = Now(), Stage = stage, Progress = fraction });

    public void Warning(string message)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.Warning, TimestampUtc = Now(), Message = message });

    public void EncoderSelected(string encoder)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.EncoderSelected, TimestampUtc = Now(), Encoder = encoder });

    public void OutputCreated(string path)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.OutputCreated, TimestampUtc = Now(), OutputPath = path });

    public void Completed(string outputPath, double elapsedSeconds)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.Completed, TimestampUtc = Now(), OutputPath = outputPath, ElapsedSeconds = elapsedSeconds });

    public void Failed(string message, string code, int exitCode)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.Failed, TimestampUtc = Now(), Message = message, Code = code, ExitCode = exitCode });

    public void Cancelled(string message)
        => Emit(new ExportProgressEvent { Type = ExportEventTypes.Cancelled, TimestampUtc = Now(), Message = message });

    /// <summary>Creates the writer when the options request structured progress.</summary>
    internal static ProgressJsonlWriter? CreateIfRequested(VisualizeOptions options)
        => string.Equals(options.ProgressMode, "jsonl", StringComparison.Ordinal)
            ? new ProgressJsonlWriter(Console.Out)
            : null;

    /// <summary>
    /// Human output target: stdout normally, stderr while JSON-lines progress
    /// owns stdout.
    /// </summary>
    internal static TextWriter HumanOutput(ProgressJsonlWriter? progress)
        => progress == null ? Console.Out : Console.Error;

    /// <summary>Stable camelCase stage name for a pipeline stage.</summary>
    internal static string StageName(ExportStage stage)
    {
        string name = stage.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Maps an error message + exit code to a stable machine-readable code
    /// (see ValidationCodes). Exit codes are checked first, then the message.
    /// </summary>
    internal static string MapFailureCode(string message, int exitCode)
    {
        if (exitCode == 9)
            return ValidationCodes.OutputExists;
        if (exitCode == 10)
            return ValidationCodes.NoRenderableContent;
        if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return ValidationCodes.FfmpegNotFound;
        if (message.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return ValidationCodes.EncoderUnavailable;
        if (message.Contains("corrscope", StringComparison.OrdinalIgnoreCase))
            return ValidationCodes.CorrscopeNotFound;
        if (message.Contains("stems", StringComparison.OrdinalIgnoreCase))
            return ValidationCodes.MissingStems;
        if (message.Contains("FMP.COM", StringComparison.Ordinal))
            return ValidationCodes.ToolNotFound;
        return ValidationCodes.CaptureFailed;
    }

    private static double? Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
