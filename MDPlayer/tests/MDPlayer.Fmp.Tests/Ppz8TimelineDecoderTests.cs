using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Ppz8TimelineDecoderTests
{
    private static (Ppz8TimelineDecoder Decoder, TimelineBuilder Builder) NewDecoder()
    {
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Ppz8TimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.Ppz8(), timeline);
        decoder.ObserveBank("bank:0", [new ReadOnlyMemory<byte>(new byte[] { 0x80, 0x00 })]);
        return (decoder, timeline);
    }

    private static TimedChipWrite W(long sample, int port, int address, int data) =>
        new(sample, new DeviceId(ChipType.Ppz8, 0), port, address, data);

    [Fact]
    public void Fnum8000_UsesSourceRateAndDoesNotInventMidiRootPitch()
    {
        var (decoder, builder) = NewDecoder();
        decoder.Process(W(0, 21, 0, 16_000)); // source sample rate
        decoder.Process(W(0, 11, 0, 0x8000)); // playback ratio = 1.0
        decoder.Process(W(0, 1, 0, 0)); // key on sample 0
        decoder.Process(W(1_000, 2, 0, 0)); // key off
        decoder.Complete(2_000);

        VisualizationTimeline timeline = builder.Build(2_000);
        Ppz8Event eventValue = Assert.Single(timeline.Ppz8);
        Assert.Equal(16_000.0, eventValue.FrequencyHz, precision: 6);
        Assert.Null(eventValue.MidiNote);
        Assert.Null(Assert.Single(timeline.SamplePlayback).MidiPitch);
    }

    [Fact]
    public void Fnum4000_HalvesEffectiveSourceRate()
    {
        var (decoder, builder) = NewDecoder();
        decoder.Process(W(0, 21, 0, 16_000));
        decoder.Process(W(0, 11, 0, 0x4000));
        decoder.Process(W(0, 1, 0, 0));
        decoder.Process(W(1_000, 2, 0, 0));
        decoder.Complete(2_000);

        Ppz8Event eventValue = Assert.Single(builder.Build(2_000).Ppz8);
        Assert.Equal(8_000.0, eventValue.FrequencyHz, precision: 6);
        Assert.Null(eventValue.MidiNote);
    }
}
