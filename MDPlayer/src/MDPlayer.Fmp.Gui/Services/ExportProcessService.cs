using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Preview;

namespace Fmp.Gui.Services;

/// <summary>Outcome of an export run plus the diagnostics workspace, if retained.</summary>
public sealed record ExportResult
{
    public bool Succeeded { get; init; }
    public bool Cancelled { get; init; }
    public string? WorkspacePath { get; init; }
    public string? LogPath { get; init; }
}

/// <summary>
/// Launches the export pipeline: writes the request JSON to a per-run temp
/// workspace, runs <c>mdplayer-render render --request-json &lt;path&gt;
/// --progress jsonl</c>, streams each JSON-lines progress event to the caller.
/// The caller is notified of the workspace (and its <c>export.log</c>) before
/// the child process starts; the workspace is deleted after a successful
/// render and retained after a failure or cancellation for log/scope
/// inspection.
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
    /// Runs the export. Reports structured events through <paramref name="progress"/>
    /// and invokes <paramref name="workspaceReady"/> with the diagnostics
    /// workspace (containing <c>export.log</c>) before the child process
    /// starts, so the UI can advertise the log path even when the run fails or
    /// is cancelled. Returns an explicit <see cref="ExportResult"/>.
    /// </summary>
    public async Task<ExportResult> StartAsync(
        VisualizationRequest request,
        IProgress<ExportProgressEvent> progress,
        Action<string>? workspaceReady = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string workspace = Path.Combine(
            Path.GetTempPath(), "MDPlayer", "Visualizer",
            "export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string logPath = Path.Combine(workspace, "export.log");

        string requestJson = Path.Combine(workspace, "request.json");
        VisualizationRequestSerializer.WriteToFile(request, requestJson);

        // Resolve the CLI before publishing the workspace: if resolution
        // fails there is nothing to advertise and the empty workspace is
        // removed instead of leaking.
        string renderCli;
        try
        {
            renderCli = DesktopProcessService.ResolveRenderCli(_renderCliUserPath)
                ?? throw new InvalidOperationException(
                    "The 'mdplayer-render' executable was not found. Set MDPLAYER_RENDER_PATH, " +
                    "place it next to the application, or add it to PATH.");
        }
        catch
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
            throw;
        }

        // Publish the diagnostics workspace before the child process starts so
        // a cancellation or failure can still point the user at the log.
        workspaceReady?.Invoke(workspace);

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

        bool receivedTerminalEvent = false;
        Task readTask = ReadProgressAsync(process, progress, onTerminalEvent: () => receivedTerminalEvent = true, ct);
        var stderrBuilder = new StringBuilder();
        Task stderrTask = ReadStderrAsync(process, stderrBuilder, logPath, ct);

        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
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
        }

        await Task.WhenAll(readTask, stderrTask);

        bool succeeded = !cancelled && process.ExitCode == 0;

        // Emit a synthetic terminal event only when the CLI did not already
        // report a structured failure/cancellation — a nonzero exit must not
        // produce a duplicate failure summary.
        if (cancelled && !receivedTerminalEvent)
        {
            progress.Report(new ExportProgressEvent
            {
                Type = ExportEventTypes.Cancelled,
                ExitCode = process.ExitCode,
                Message = "Export was cancelled.",
            });
        }
        else if (!succeeded && !receivedTerminalEvent)
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

        // On success the staging workspace holds no durable artifacts a user
        // needs, so delete it and report no workspace. On failure or
        // cancellation it is retained for log/scope inspection.
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
            return new ExportResult
            {
                Succeeded = true,
                Cancelled = false,
                WorkspacePath = null,
                LogPath = null,
            };
        }

        return new ExportResult
        {
            Succeeded = false,
            Cancelled = cancelled,
            WorkspacePath = workspace,
            LogPath = logPath,
        };
    }

    private static async Task ReadProgressAsync(
        Process process,
        IProgress<ExportProgressEvent> progress,
        Action onTerminalEvent,
        CancellationToken ct)
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
                if (evt is null)
                    continue;
                if (evt.Type is ExportEventTypes.Failed or ExportEventTypes.Cancelled)
                    onTerminalEvent();
                progress.Report(evt);
            }
            catch (JsonException)
            {
                // Malformed progress line; ignore.
            }
        }
    }

    private static async Task ReadStderrAsync(
        Process process,
        StringBuilder builder,
        string logPath,
        CancellationToken ct)
    {
        // Create the log up front so failed/cancelled workspaces always carry
        // export.log even when the CLI produced no stderr.
        using var log = new StreamWriter(logPath, append: false, Encoding.UTF8);
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
            log.WriteLine(line);
        }
        log.Flush();
    }
}
