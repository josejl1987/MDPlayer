namespace Fmp.Cli;

/// <summary>
/// Maps an export/visualization failure to a stable machine-readable code.
/// Lives in the application assembly so both the CLI progress writer and the
/// GUI preview session can share a single failure-code vocabulary.
/// </summary>
internal static class FailureCodeMapper
{
    public static string MapFailureCode(string message, int exitCode)
    {
        if (exitCode == 9)
            return Fmp.Application.Contracts.ValidationCodes.OutputExists;
        if (exitCode == 10)
            return Fmp.Application.Contracts.ValidationCodes.NoRenderableContent;
        if (message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return Fmp.Application.Contracts.ValidationCodes.FfmpegNotFound;
        if (message.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return Fmp.Application.Contracts.ValidationCodes.EncoderUnavailable;
        if (message.Contains("corrscope", StringComparison.OrdinalIgnoreCase))
            return Fmp.Application.Contracts.ValidationCodes.CorrscopeNotFound;
        if (message.Contains("stems", StringComparison.OrdinalIgnoreCase))
            return Fmp.Application.Contracts.ValidationCodes.MissingStems;
        if (message.Contains("FMP.COM", StringComparison.Ordinal))
            return Fmp.Application.Contracts.ValidationCodes.ToolNotFound;
        return Fmp.Application.Contracts.ValidationCodes.CaptureFailed;
    }
}
