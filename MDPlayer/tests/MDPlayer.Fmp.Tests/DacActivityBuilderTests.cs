using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class DacActivityBuilderTests
{
    [Fact]
    public void LongDacPlaybackKeepsOnePlaybackEventAndDerivesBoundedActivitySlices()
    {
        var builder = new TimelineBuilder(48_000);
        builder.AddSample(new SampleDefinition(
            "dac:0",
            "pcm",
            SourceLengthSamples: 4,
            NativeSampleRate: null,
            LoopStart: null,
            LoopEnd: null,
            SampleLoopMode.None,
            [
                new WaveformEnvelopePoint(0, 0),
                new WaveformEnvelopePoint(-0.50f, 0.25f),
                new WaveformEnvelopePoint(-1, 1),
                new WaveformEnvelopePoint(-0.25f, 0.25f),
            ],
            "DAC S000"));
        builder.AddSamplePlayback(new SamplePlaybackEvent(
            "ym2612.0.pcm.dac",
            100,
            1_100,
            "dac:0",
            MidiPitch: null,
            PlaybackRate: 1,
            Gain: 1,
            Pan: 0,
            Retrigger: false,
            Looping: false));

        VisualizationTimeline timeline = builder.Build(1_200);

        Assert.Single(timeline.SamplePlayback);
        Assert.Equal(3, timeline.DacActivity.Length);
        Assert.All(timeline.DacActivity, activity =>
        {
            Assert.Equal("ym2612.0.pcm.dac", activity.VoiceId);
            Assert.Equal("dac:0", activity.SampleId);
            Assert.InRange(activity.StartSample, 100, 1_099);
            Assert.InRange(activity.EndSample, 101, 1_100);
            Assert.InRange(activity.Level, 0.01f, 1f);
        });
        Assert.Equal((350L, 600L),
            (timeline.DacActivity[0].StartSample, timeline.DacActivity[0].EndSample));
        Assert.Equal((600L, 850L),
            (timeline.DacActivity[1].StartSample, timeline.DacActivity[1].EndSample));
        Assert.Equal((850L, 1_100L),
            (timeline.DacActivity[2].StartSample, timeline.DacActivity[2].EndSample));
        VisualizationTimelineValidator.Validate(timeline);
    }
}
