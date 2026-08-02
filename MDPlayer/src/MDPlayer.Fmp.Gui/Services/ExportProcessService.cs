using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Preview;

namespace Fmp.Gui.Services;

/// <summary>
/// Launches the export pipeline: writes the request JSON to a per-run temp
/// workspace, runs <c>mdplayer-render render --request-json &lt;path&gt;
/// --progress jsonl</c>, streams each JSON-lines progress event to the caller.
/// The temporary workspace is deleted after a successful render and retained
/// after a failure for log/scope inspection.
/// </summary>
public sealed class ExportProcessService
{
    private static readonly JsonSerializerOptions Json = RequestJson.Create();

    private readonly string? _renderCliUserPath;

    public ExportProcessService(string? renderCliUserPath)
    {
        _renderCliUserPath = renderCliUserPath;
    }

    /// <summary>
    /// Runs the export. Reports structured events through <paramref name="progress"/>.
    /// Returns the temp workspace path (retained only on failure for diagnostics).
    /// </summary>
    public async Task<string> StartAsync(
        VisualizationRequest request,
        IProgress<ExportProgressEvent> progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string workspace = Path.Combine(
            Path.GetTempPath(), "MDPlayer", "Visualizer",
            "export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);

        string requestJson = Path.Combine(workspace, "request.json");
        VisualizationRequestSerializer.WriteToFile(request, requestJson);

        string renderCli = DesktopProcessService.ResolveRenderCli(_renderCliUserPath)
            ?? throw new InvalidOperationException(
                "The 'mdplayer-render' executable was not found. Set MDPLAYER_RENDER_PATH, " +
                "place it next to the application, or add it to PATH.");

        var psi = DesktopProcessService.CreateStartInfo(renderCli, new[]
        {
            "render", "--request-json", requestJson, "--progress", "jsonl",
        });
        psi.WorkingDirectory = workspace;

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start '{renderCli}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to start '{renderCli}': {ex.Message}", ex);
        }

        Task readTask = ReadProgressAsync(process, progress, ct);
        var stderrBuilder = new StringBuilder();
        Task stderrTask = ReadStderrAsync(process, stderrBuilder, ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Grace period (3 s) for the CLI to wrap up, then kill the tree.
            try
            {
                await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
            }
            catch
            {
                // Ignore.
            }
            if (!process.HasExited)
                DesktopProcessService.KillTree(process);
            throw;
        }

        await Task.WhenAll(readTask, stderrTask);

        bool succeeded = process.ExitCode == 0;
        if (!succeeded)
        {
            string detail = stderrBuilder.ToString().Trim();
            progress.Report(new ExportProgressEvent
            {
                Type = ExportEventTypes.Failed,
                ExitCode = process.ExitCode,
                Message = string.IsNullOrWhiteSpace(detail)
                    ? $"mdplayer-render exited with code {process.ExitCode}."
                    : detail,
            });
        }

        // The temp workspace is a per-run staging area. On success it holds no
        // durable artifacts a user needs, so delete it. On failure (including
        // cancellation) it is retained for log/scope inspection.
        if (succeeded)
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch
            {
                // Best effort; leftover temp files are harmless.
            }
        }

        return workspace;
    }

    private static async Task ReadProgressAsync(Process process, IProgress<ExportProgressEvent> progress, CancellationToken ct)
    {
        using var reader = process.StandardOutput;
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                ExportProgressEvent? evt = JsonSerializer.Deserialize<ExportProgressEvent>(line, Json);
                if (evt is not null)
                    progress.Report(evt);
            }
            catch (JsonException)
            {
                // Malformed progress line; ignore.
            }
        }
    }

    private static async Task ReadStderrAsync(Process process, StringBuilder builder, CancellationToken ct)
    {
        using var reader = process.StandardError;
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (line is null)
                break;
            builder.AppendLine(line);
        }
    }
}
