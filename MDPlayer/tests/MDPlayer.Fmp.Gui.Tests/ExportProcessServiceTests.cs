using Fmp.Application.Contracts;
using Fmp.Gui.Services;
using Xunit;

namespace Fmp.Gui.Tests;

/// <summary>
/// Export process tests with a fake CLI (spec §33.7): normal completion,
/// nonzero exit with a failed event, and JSONL event parsing.
/// </summary>
public class ExportProcessServiceTests
{
    private static readonly string? PreviousRenderPath = Environment.GetEnvironmentVariable("MDPLAYER_RENDER_PATH");

    [Fact]
    public async Task FakeCli_EmitsProgressEvents_InOrder_AndCompletes()
    {
        using FakeCli fake = FakeCli.Create(
            """
            {"type":"started"}
            {"type":"stage-started","stage":"capturingSemanticTimeline"}
            {"type":"encoder-selected","encoder":"libx264"}
            {"type":"completed","outputPath":"/tmp/out.mp4","elapsedSeconds":4.2}
            """,
            exitCode: 0);

        var events = new List<ExportProgressEvent>();
        string workspace = await RunExportAsync(fake.Path, events);

        Assert.Equal(4, events.Count);
        Assert.Equal(ExportEventTypes.Started, events[0].Type);
        Assert.Equal("capturingSemanticTimeline", events[1].Stage);
        Assert.Equal("libx264", events[2].Encoder);
        Assert.Equal(ExportEventTypes.Completed, events[3].Type);
        Assert.Equal("/tmp/out.mp4", events[3].OutputPath);
        Assert.Equal(4.2, events[3].ElapsedSeconds);
        Assert.True(Directory.Exists(workspace), "export workspace should be retained for logs");
    }

    [Fact]
    public async Task FakeCli_NonZeroExit_EmitsFailedEvent()
    {
        using FakeCli fake = FakeCli.Create(
            """
            {"type":"started"}
            {"type":"failed","code":"CAPTURE_FAILED","exitCode":5,"message":"capture broke"}
            """,
            exitCode: 5);

        var events = new List<ExportProgressEvent>();
        await RunExportAsync(fake.Path, events);

        // The CLI emits the structured failure; the service also appends a
        // fallback failed event for the nonzero exit code.
        ExportProgressEvent failed = Assert.Single(
            events.Where(evt => evt.Type == ExportEventTypes.Failed && evt.Code is not null));
        Assert.Equal(5, failed.ExitCode);
        Assert.Equal(ValidationCodes.CaptureFailed, failed.Code);
    }

    [Fact]
    public async Task FakeCli_MalformedLine_IsIgnored()
    {
        using FakeCli fake = FakeCli.Create(
            """
            {"type":"started"}
            this is not json
            {"type":"completed","outputPath":"/tmp/ok.mp4","elapsedSeconds":1}
            """,
            exitCode: 0);

        var events = new List<ExportProgressEvent>();
        await RunExportAsync(fake.Path, events);

        Assert.Equal(2, events.Count);
        Assert.Equal(ExportEventTypes.Completed, events[^1].Type);
    }

    private static async Task<string> RunExportAsync(string cliPath, List<ExportProgressEvent> events)
    {
        // Ensure MDPLAYER_RENDER_PATH cannot override the fake CLI.
        Environment.SetEnvironmentVariable("MDPLAYER_RENDER_PATH", null);
        try
        {
            var service = new ExportProcessService(cliPath);
            return await service.StartAsync(
                new VisualizationRequest
                {
                    InputPath = "/tmp/fake-input.vgz",
                    OutputPath = "/tmp/fake-out.mp4",
                },
                new Progress<ExportProgressEvent>(events.Add),
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MDPLAYER_RENDER_PATH", PreviousRenderPath);
        }
    }

    /// <summary>Writes an executable fake CLI that echoes fixed JSONL then exits.</summary>
    private sealed class FakeCli : IDisposable
    {
        private FakeCli(string path) => Path = path;

        public string Path { get; }

        public static FakeCli Create(string jsonl, int exitCode)
        {
            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"fake-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "mdplayer-render");
            string script = "#!/bin/bash\ncat <<'EOF'\n" + jsonl + "\nEOF\nexit " + exitCode + "\n";
            File.WriteAllText(path, script);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new FakeCli(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
