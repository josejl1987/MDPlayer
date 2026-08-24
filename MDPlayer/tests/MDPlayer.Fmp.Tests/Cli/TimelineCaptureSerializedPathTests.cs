using Fmp.Cli;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Cli;

/// <summary>
/// Locks the WP01 P1 fix: the serialized-timeline (<c>--timeline</c>) path must
/// route through the SAME producer-clock normalization boundary as a decoder
/// capture. A serialized timeline whose declared sample clock differs from the
/// destination playback clock is converted exactly once (spec §4.1); an
/// ambiguous/unknown sample clock is rejected with an actionable
/// <see cref="MusicalTimingException"/> — never a generic JSON validator error,
/// and never a silent clock-equality assumption.
/// </summary>
public sealed class TimelineCaptureSerializedPathTests
{
    [Fact]
    public void SerializedTimeline_DifferentExplicitClock_IsNormalizedToDestination()
    {
        // Producer source clock 22 050 Hz; destination playback clock 44 100 Hz.
        // Every timed event must be converted exactly once so the returned
        // timeline lands on the destination clock (the clock the MIDI export
        // path will consume through MusicalTimeMapBuilder).
        var source = new VisualizationTimeline
        {
            SampleRate = 22_050,
            StartSample = 500,
            EndSample = 44_101,
            Timing = [new DriverTimingEvent(1_000, 0x42, 120.0)],
            Beats = [new BeatEvent(1_000, 1.0)],
            Notes =
            [
                new NoteEvent("v", 1_000, 2_020, 440, 69, "i", VisualizationNoteMode.Fm, false, []),
            ],
        };
        string path = WriteTimeline(source);
        try
        {
            var options = new MidiOptions { SampleRate = 44_100 };
            VisualizationTimeline captured = TimelineCaptureService.Capture(
                inputPath: "unused.vgm", timelinePath: path, options);

            // Destination clock on the result, and every sample position converted
            // 22050 -> 44100 exactly once (x2).
            Assert.Equal(44_100, captured.SampleRate);
            // The serialized [StartSample, EndSample] range is preserved on the
            // destination clock (P2): origin 500 -> 1000, end 44 101 -> 88 202.
            Assert.Equal(1_000, captured.StartSample);
            Assert.Equal(2_000, Assert.Single(captured.Timing).SamplePosition);
            Assert.Equal(2_000, Assert.Single(captured.Beats).SamplePosition);
            Assert.Equal(2_000, Assert.Single(captured.Notes).StartSample);
            Assert.Equal(4_040, Assert.Single(captured.Notes).EndSample);
            // EndSample is normalized onto the destination clock too.
            Assert.Equal(88_202, captured.EndSample);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SerializedTimeline_MatchingClock_PassesThroughUnchanged()
    {
        var source = new VisualizationTimeline
        {
            SampleRate = 44_100,
            StartSample = 1_000,
            EndSample = 20_000,
            Timing = [new DriverTimingEvent(1_000, 0x42, 120.0)],
            Beats = [new BeatEvent(1_000, 1.0)],
        };
        string path = WriteTimeline(source);
        try
        {
            var options = new MidiOptions { SampleRate = 44_100 };
            VisualizationTimeline captured = TimelineCaptureService.Capture(
                inputPath: "unused.vgm", timelinePath: path, options);

            Assert.Equal(44_100, captured.SampleRate);
            // Matching clock: the serialized start range passes through unchanged
            // (P2) — origin 1 000 is NOT silently reset to 0 by the rebuild.
            Assert.Equal(1_000, captured.StartSample);
            Assert.Equal(20_000, captured.EndSample);
            Assert.Equal(1_000, Assert.Single(captured.Timing).SamplePosition);
            Assert.Equal(1_000, Assert.Single(captured.Beats).SamplePosition);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void AlignTimelineToAudio_PreservesPcmSamplesAndPlaybackEvents()
    {
        var sample = new SampleDefinition(
            "dac:0",
            "pcm",
            4,
            null,
            null,
            null,
            SampleLoopMode.None,
            [new WaveformEnvelopePoint(-1, 1)],
            "DAC S000");
        var source = new VisualizationTimeline
        {
            SampleRate = 44_100,
            StartSample = 0,
            EndSample = 1_000,
            Voices =
            [
                new VoiceDescriptor(
                    new VoiceId(new DeviceId(ChipType.Ym2612, 0), VoiceKind.Pcm, 0, Name: "dac"),
                    "DAC",
                    VoicePresentationKind.Pcm,
                    10,
                    false,
                    false,
                    false),
            ],
            Samples = [sample],
            SamplePlayback =
            [
                new SamplePlaybackEvent(
                    "ym2612.0.pcm.dac",
                    100,
                    900,
                    sample.Id,
                    null,
                    1.0,
                    1f,
                    0f,
                    false,
                    false),
            ],
        };

        VisualizationTimeline aligned = VisualizationPresentationSupport.AlignTimelineToAudio(
            source,
            audioEndSample: 500);

        Assert.Same(sample, Assert.Single(aligned.Samples));
        SamplePlaybackEvent playback = Assert.Single(aligned.SamplePlayback);
        Assert.Equal(100, playback.StartSample);
        Assert.Equal(500, playback.EndSample);
    }

    [Fact]
    public void SerializedTimeline_AmbiguousClock_IsRejectedWithMusicalTimingException()
    {
        // A serialized timeline that lost (or never carried) its SampleRate
        // metadata is ambiguous: the producer boundary must reject it with an
        // actionable MusicalTimingException rather than assume it matches the
        // destination playback clock (and rather than fail with a generic JSON
        // validator error).
        string path = WriteRawTimeline("""
        {"schemaVersion":2,"sampleRate":0,"startSample":0,"endSample":1000,
         "timing":[{"samplePosition":100,"timerBValue":66,"validatedBpm":120.0}]}
        """);
        try
        {
            var options = new MidiOptions { SampleRate = 44_100 };
            var ex = Assert.Throws<MusicalTimingException>(() =>
                TimelineCaptureService.Capture("unused.vgm", path, options));
            Assert.Contains("source rate unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string WriteTimeline(VisualizationTimeline timeline)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "mdplayer-timeline-capture-" + Guid.NewGuid().ToString("N") + ".json");
        VisualizationJsonWriter.Write(path, timeline);
        return path;
    }

    private static string WriteRawTimeline(string json)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "mdplayer-timeline-capture-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
