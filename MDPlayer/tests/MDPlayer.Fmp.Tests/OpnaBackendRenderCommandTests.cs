using Fmp.Application.Contracts;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Request-driven `--opna-backend` routing for the visualization <c>render</c>
/// command (Prompt 10R). Complements OpnaBackendCliTests (which covers the WAV
/// batch path): asserts the one authoritative backend value lands in the parsed
/// request, the MDSound default holds when the option is absent, and the retired
/// `native-lle` value is rejected rather than silently aliased to native audio.
/// </summary>
public class OpnaBackendRenderCommandTests
{
    private static VisualizationRequest Parse(params string[] args)
        => VisualizationRenderCommand.ParseRequestStrict(args);

    [Fact]
    public void Default_WhenOptionAbsent_IsMdsound() =>
        Assert.Equal(FmpOpnaBackend.Mdsound,
            Parse("song.ovi", "-o", "out.mp4").Playback.OpnaBackend);

    [Fact]
    public void NativeAudio_IsParsed() =>
        Assert.Equal(FmpOpnaBackend.NativeAudio,
            Parse("song.ovi", "-o", "out.mp4", "--opna-backend", "native-audio").Playback.OpnaBackend);

    [Fact]
    public void Mdsound_IsParsed() =>
        Assert.Equal(FmpOpnaBackend.Mdsound,
            Parse("song.ovi", "-o", "out.mp4", "--opna-backend", "mdsound").Playback.OpnaBackend);

    [Fact]
    public void RetiredNativeLle_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Parse("song.ovi", "-o", "out.mp4", "--opna-backend", "native-lle"));
        Assert.Contains("native-lle", ex.Message);
    }

    [Fact]
    public void UnknownBackend_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Parse("song.ovi", "-o", "out.mp4", "--opna-backend", "auto"));
    }
}
