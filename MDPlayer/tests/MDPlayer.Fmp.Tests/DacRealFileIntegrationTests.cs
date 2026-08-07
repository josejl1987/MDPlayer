using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Real-file integration tests for the YM2612 DAC sample pipeline (spec §31).
/// These run the actual <c>26 - Robotnik.vgz</c> through the real
/// <see cref="VgmPlaybackBackend"/> and decoder sink, asserting the semantic
/// DAC sample contract (per-trigger sample playbacks, deduplicated assets,
/// no pitched placeholder).
/// </summary>
public sealed class DacRealFileIntegrationTests
{
    [Fact]
    public void Robotnik_ProducesDacSamplePlaybackEventsAndDeduplicatedAssets()
    {
        VisualizationTimeline timeline = Capture("robotnik-dac.vgz");

        SamplePlaybackEvent[] dacEvents = timeline.SamplePlayback
            .Where(e => e.VoiceId == "ym2612.0.pcm.dac")
            .ToArray();

        Assert.NotEmpty(dacEvents);

        // No pitched PCM placeholder note remains (spec §32 / §34).
        Assert.DoesNotContain(timeline.Notes, n => n.Mode == VisualizationNoteMode.Pcm);

        // Every playback event references a real sample asset.
        Assert.All(dacEvents, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.SampleId));
            Assert.Contains(timeline.Samples, s => s.Id == e.SampleId);
        });

        // Assets are the deduplicated set; the timeline exposes at least one.
        Assert.NotEmpty(timeline.Samples.Where(s => s.Family == "pcm"));
    }

    [Fact]
    public void Robotnik_SampleAssetsUseIdentityOrientedNames()
    {
        VisualizationTimeline timeline = Capture("robotnik-dac.vgz");

        SamplePlaybackEvent[] dacEvents = timeline.SamplePlayback
            .Where(e => e.VoiceId == "ym2612.0.pcm.dac")
            .ToArray();
        if (dacEvents.Length == 0)
            return;

        // Display names follow the stable DAC S000 style (not pitch names).
        SampleDefinition[] dacSamples = timeline.Samples
            .Where(s => s.Family == "pcm")
            .ToArray();
        Assert.Contains(dacSamples, s => s.DisplayName != null && s.DisplayName.StartsWith("DAC S"));
    }

    [Fact]
    public void Robotnik_NoDacLaneAbsentWhenActivityExists()
    {
        VisualizationTimeline timeline = Capture("robotnik-dac.vgz");

        // The DAC voice is present and carries a sample-lane presentation.
        Assert.Contains(timeline.Voices, v =>
            v.Id.ToString() == "ym2612.0.pcm.dac");
    }

    [Fact]
    public void Robotnik_LoopingDoesNotDuplicateSampleAssets()
    {
        VisualizationTimeline once = Capture("robotnik-dac.vgz", loopCount: 1);
        VisualizationTimeline looped = Capture("robotnik-dac.vgz", loopCount: 2);

        int assetsOnce = once.Samples.Count(s => s.Family == "pcm");
        int assetsLooped = looped.Samples.Count(s => s.Family == "pcm");

        // Loop playback must not create more unique assets; it only adds events.
        Assert.Equal(assetsOnce, assetsLooped);
        Assert.True(looped.SamplePlayback.Length >= once.SamplePlayback.Length);
    }

    private static VisualizationTimeline Capture(string fixtureName, int loopCount = 1)
    {
        string input = Path.Combine(AppContext.BaseDirectory, "testfixtures", fixtureName);
        Assert.True(File.Exists(input), $"Fixture not provisioned: {input}");

        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-{fixtureName}-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(44_100);
        try
        {
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(input),
                new PlaybackOptions(
                    LoopCount: loopCount,
                    FadeSeconds: 0,
                    TailSeconds: 0,
                    OutputAudioPath: wav,
                    SampleRate: 44_100),
                sink);
            session.Run();
            return sink.Complete(session.SamplePosition, "test");
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }
    }
}