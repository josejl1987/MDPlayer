namespace Fmp.Cli;

internal sealed class VisualizationExecutionException : Exception
{
    public VisualizationExecutionException(string message, int exitCode, string? code = null)
        : base(message)
    {
        ExitCode = exitCode;
        Code = code ?? ProgressJsonlWriter.MapFailureCode(message, exitCode);
    }

    public int ExitCode { get; }
    public string Code { get; }
}
