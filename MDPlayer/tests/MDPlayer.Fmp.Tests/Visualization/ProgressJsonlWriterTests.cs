using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Verifies the CLI's structured progress output: the JSON-lines writer must be
/// able to emit <c>stage-progress</c> events carrying a fractional 0..1 value,
/// which is how the GUI drives a determinate progress bar over the
/// composing-frames stage. Regression guard for "the progress bar doesn't
/// report progress properly" — the runner previously only emitted StageStarted/
/// StageCompleted, so the bar stayed indeterminate for the whole render.
/// </summary>
public sealed class ProgressJsonlWriterTests
{
    [Fact]
    public void StageProgress_SerializesFractionAndType()
    {
        using var buffer = new StringWriter();
        var writer = new ProgressJsonlWriter(buffer);

        writer.StageProgress(ProgressJsonlWriter.StageName(ExportStage.ComposingFrames), 0.5);

        string line = buffer.ToString().Trim();
        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement root = doc.RootElement;

        Assert.Equal("stage-progress", root.GetProperty("type").GetString());
        Assert.Equal("composingFrames", root.GetProperty("stage").GetString());
        Assert.Equal(0.5, root.GetProperty("progress").GetDouble());
        Assert.True(root.TryGetProperty("timestampUtc", out _));
    }

    [Fact]
    public void StageStarted_ThenProgress_ThenCompleted_Flow()
    {
        using var buffer = new StringWriter();
        var writer = new ProgressJsonlWriter(buffer);
        const string stage = "composingFrames";

        writer.StageStarted(stage);
        writer.StageProgress(stage, 0.33);
        writer.StageCompleted(stage, 5.0);

        string[] lines = buffer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("stage-started", JsonRoot(lines[0]).GetProperty("type").GetString());
        Assert.Equal(0.33, JsonRoot(lines[1]).GetProperty("progress").GetDouble());
        Assert.Equal("stage-completed", JsonRoot(lines[2]).GetProperty("type").GetString());
    }

    private static JsonElement JsonRoot(string line)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }
}