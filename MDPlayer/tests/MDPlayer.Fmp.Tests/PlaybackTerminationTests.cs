using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class PlaybackTerminationTests
{
    [Fact]
    public void NaturalEndUsesTailOnly()
    {
        var termination = new PlaybackTermination(loopCount: 2, fadeSamples: 500, tailSamples: 50);

        termination.Observe(playbackEnded: true, currentLoop: 0, samplePosition: 1_000);

        Assert.True(termination.Started);
        Assert.False(termination.FadeActive);
        Assert.Equal("natural_stop", termination.StopReason);
        Assert.Equal(1_050, termination.StopAtSample);
        Assert.False(termination.IsComplete(1_049));
        Assert.True(termination.IsComplete(1_050));
    }

    [Fact]
    public void LoopLimitUsesFadeThenTail()
    {
        var termination = new PlaybackTermination(loopCount: 2, fadeSamples: 500, tailSamples: 50);

        // The first loop boundary is not the requested end.
        termination.Observe(playbackEnded: true, currentLoop: 1, samplePosition: 1_000);
        Assert.False(termination.Started);

        // Reaching the requested loop count starts the fade while playback is
        // still active, and the explicit tail follows it.
        termination.Observe(playbackEnded: false, currentLoop: 2, samplePosition: 2_000);

        Assert.True(termination.Started);
        Assert.True(termination.FadeActive);
        Assert.Equal("loop_limit", termination.StopReason);
        Assert.Equal(2_550, termination.StopAtSample);
        Assert.Equal(2_000, termination.FadeStartSample);
    }

    [Fact]
    public void RepeatedSignalsDoNotExtendEffectiveEnd()
    {
        var termination = new PlaybackTermination(loopCount: 1, fadeSamples: 100, tailSamples: 25);
        termination.Observe(playbackEnded: false, currentLoop: 1, samplePosition: 500);
        termination.Observe(playbackEnded: true, currentLoop: 1, samplePosition: 1_000);

        Assert.Equal(625, termination.StopAtSample);
    }
}
