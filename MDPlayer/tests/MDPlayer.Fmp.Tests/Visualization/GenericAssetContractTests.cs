using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class GenericAssetContractTests
{
    [Fact]
    public void WaveformIdentityAndPreviewAreDeterministicAndBounded()
    {
        float[] source = Enumerable.Range(0, 32).Select(index => (index - 16) / 16f).ToArray();
        WaveformDefinition first = VisualizationAssetBuilder.CreateWaveform("wavetable", source);
        WaveformDefinition second = VisualizationAssetBuilder.CreateWaveform("wavetable", source);

        Assert.Equal(first.Id, second.Id);
        Assert.StartsWith("wave:", first.Id, StringComparison.Ordinal);
        Assert.Equal(32, first.Preview.Length);
        Assert.InRange(first.Preview.Min(), -1, 1);
        Assert.InRange(first.Preview.Max(), -1, 1);

        float[] large = Enumerable.Range(0, 512).Select(index => MathF.Sin(index / 10f)).ToArray();
        Assert.InRange(VisualizationAssetBuilder.CreateWaveform("custom", large).Preview.Length, 2, 128);
    }

    [Fact]
    public void TimelineBuilderProducesGenericPcmAssetsWithoutRawSamples()
    {
        var builder = new TimelineBuilder(44_100);
        DeviceDescriptor device = VisualizationDeviceCatalog.Ga20();
        VoiceId voice = new(device.Id, VoiceKind.Pcm, 0);
        builder.AddDevice(device);
        builder.AddVoice(new VoiceDescriptor(voice, "PCM 1", VoicePresentationKind.Pcm, 0, false, false, true));
        builder.AddNote(voice, 100, 1_000, 60, 261.63, "sample-slot-7", VisualizationNoteMode.Pcm, false, []);

        VisualizationTimeline timeline = builder.Build(2_000);
        SamplePlaybackEvent playback = Assert.Single(timeline.SamplePlayback);
        Assert.Single(timeline.Samples);
        Assert.Equal(voice.ToString(), playback.VoiceId);
        Assert.DoesNotContain("raw", VisualizationJsonWriter.Serialize(timeline), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnpitchedPcmProducesOnlySamplePlaybackAndNoFakeMidiNote()
    {
        var builder = new TimelineBuilder(44_100);
        DeviceDescriptor device = VisualizationDeviceCatalog.Okim6295();
        VoiceId voice = new(device.Id, VoiceKind.Pcm, 0);
        builder.AddVoice(new VoiceDescriptor(
            voice, "Voice 1", VoicePresentationKind.Pcm, 0, true, false, false));
        builder.AddNote(voice, 100, 900, -1, 0, "sample-3",
            VisualizationNoteMode.Pcm, false, []);

        VisualizationTimeline timeline = builder.Build(1_000);

        Assert.Empty(timeline.Notes);
        Assert.Single(timeline.SamplePlayback);
        Assert.Null(timeline.SamplePlayback[0].MidiPitch);
    }

    [Fact]
    public void HuC6280WaveformWritesEmitChangesWithoutNoteRetriggers()
    {
        var builder = new TimelineBuilder(44_100);
        var decoder = new Huc6280TimelineDecoder();
        DeviceDescriptor device = VisualizationDeviceCatalog.Huc6280();
        decoder.Initialize(device, builder);

        decoder.Process(new TimedChipWrite(0, device.Id, 0, 0, 0));
        for (int index = 0; index < 32; index++)
        {
            decoder.Process(new TimedChipWrite(index, device.Id, 0, 6, index & 0x1F));
            decoder.Process(new TimedChipWrite(index, device.Id, 0, 6, index & 0x1F));
        }
        decoder.Process(new TimedChipWrite(100, device.Id, 0, 2, 0x40));
        decoder.Process(new TimedChipWrite(100, device.Id, 0, 3, 0x01));
        decoder.Process(new TimedChipWrite(100, device.Id, 0, 4, 0x1F));
        decoder.Process(new TimedChipWrite(100, device.Id, 0, 5, 0xFF));
        decoder.Complete(1_000);

        VisualizationTimeline timeline = builder.Build(1_000);
        Assert.NotEmpty(timeline.Waveforms);
        Assert.NotEmpty(timeline.WaveformChanges);
        Assert.Single(timeline.Notes);
        Assert.False(timeline.Notes[0].IsRetrigger);
    }

    [Fact]
    public void GenericAssetJsonIsDeterministicAndValidatesReferences()
    {
        SampleDefinition sample = VisualizationAssetBuilder.CreateSample(
            "pcm", [-1, 0, 1], 32_000, displayName: "SMP 03");
        var voice = new VoiceId(new DeviceId(ChipType.SnesDsp, 0), VoiceKind.PcmVoice, 0);
        var first = new VisualizationTimeline
        {
            SampleRate = 32_000,
            EndSample = 1_000,
            Samples = [sample],
            SamplePlayback = [new SamplePlaybackEvent(
                voice.ToString(), 0, 500, sample.Id, null, 1, 0.8f, 0, false, false)],
        };
        var second = new VisualizationTimeline
        {
            SampleRate = first.SampleRate,
            EndSample = first.EndSample,
            Samples = first.Samples.Reverse().ToArray(),
            SamplePlayback = first.SamplePlayback.Reverse().ToArray(),
        };

        Assert.Equal(VisualizationJsonWriter.Serialize(first), VisualizationJsonWriter.Serialize(second));

        var invalid = new VisualizationTimeline
        {
            SampleRate = 32_000,
            EndSample = 1_000,
            SamplePlayback = [new SamplePlaybackEvent(
                voice.ToString(), 0, 1, "sample:missing", null, 1, 1, 0, false, false)],
        };
        Assert.Throws<System.Text.Json.JsonException>(() => VisualizationJsonWriter.Serialize(invalid));
    }
}
