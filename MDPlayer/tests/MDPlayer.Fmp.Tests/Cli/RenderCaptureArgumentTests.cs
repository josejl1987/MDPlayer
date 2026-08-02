using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Cli;

/// <summary>
/// Parsing of the internal <c>--capture-dir</c>/<c>--capture-key</c> runtime
/// capture-reuse options in the canonical render command. These are runtime-only:
/// they must never leak into <see cref="VisualizationRequest"/>.
/// </summary>
public sealed class RenderCaptureArgumentTests
{
    [Fact]
    public void CaptureDirectoryPlusKey_ParsesIntoInvocation()
    {
        RenderInvocation invocation = RenderCommandParser.ParseInvocationCore(
            ["song.vgz", "--capture-dir", "/tmp/cap", "--capture-key", "abc123"]);

        Assert.Equal("/tmp/cap", invocation.CaptureDirectory);
        Assert.Equal("abc123", invocation.CaptureKey);
        Assert.Equal("song.vgz", invocation.Request.InputPath);
    }

    [Fact]
    public void CaptureDirectoryAlone_Fails()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseInvocationCore(["song.vgz", "--capture-dir", "/tmp/cap"]));

        Assert.Contains("--capture-dir requires --capture-key", ex.Message);
    }

    [Fact]
    public void CaptureKeyAlone_Fails()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RenderCommandParser.ParseInvocationCore(["song.vgz", "--capture-key", "abc123"]));

        Assert.Contains("--capture-key requires --capture-dir", ex.Message);
    }

    [Fact]
    public void CaptureOptions_DoNotAlterVisualizationRequest()
    {
        var without = RenderCommandParser.ParseInvocationCore(["song.vgz"]);
        var with = RenderCommandParser.ParseInvocationCore(
            ["song.vgz", "--capture-dir", "/tmp/cap", "--capture-key", "abc123"]);

        Assert.False(ReferenceEquals(without.Request, with.Request));
        Assert.Equal(without.Request, with.Request);
    }

    [Fact]
    public void CaptureOptions_PopulateRenderInvocation()
    {
        var invocation = RenderCommandParser.ParseInvocationCore(
            ["song.vgz", "--capture-dir", "/tmp/cap", "--capture-key", "k"]);

        Assert.NotNull(invocation.CaptureDirectory);
        Assert.NotNull(invocation.CaptureKey);
        Assert.Equal("k", invocation.CaptureKey);
    }

    [Fact]
    public void NoCaptureOptions_LeavesInvocationNull()
    {
        RenderInvocation invocation = RenderCommandParser.ParseInvocationCore(["song.vgz"]);
        Assert.Null(invocation.CaptureDirectory);
        Assert.Null(invocation.CaptureKey);
    }

    [Fact]
    public void CaptureOptions_AreRuntimeOnly_NotSerializedIntoRequest()
    {
        // Serialization round-trip must not contain capture paths.
        RenderInvocation invocation = RenderCommandParser.ParseInvocationCore(
            ["song.vgz", "--capture-dir", "/tmp/secret", "--capture-key", "k"]);

        string json = System.Text.Json.JsonSerializer.Serialize(
            invocation.Request,
            RequestJson.Options);

        Assert.DoesNotContain("/tmp/secret", json);
    }
}
