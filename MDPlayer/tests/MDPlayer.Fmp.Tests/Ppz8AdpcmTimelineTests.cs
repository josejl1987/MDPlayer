using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Ppz8AdpcmTimelineTests
{
    [Fact]
    public void Ppz8_CapturesIndependentChannelsAndClosesAtTrackEnd()
    {
        var sink = new TimelineDecoderEventSink(44_100);
        DeviceId device = new(ChipType.Ppz8, 0);
        sink.OnDevice(VisualizationDeviceCatalog.Ppz8());
        sink.OnSampleAsset(new TimedSampleAssetEvent(0, device, "ppz8:3:0", AssetKind.SampleBank, 100));

        sink.OnChipWrite(new TimedChipWrite(100, device, 1, 0, 12));
        sink.OnChipWrite(new TimedChipWrite(110, device, 11, 0, 22_050));
        sink.OnChipWrite(new TimedChipWrite(120, device, 1, 1, 13));
        sink.OnChipWrite(new TimedChipWrite(200, device, 2, 0, 0));

        VisualizationTimeline timeline = sink.Complete(300, "test");

        Assert.Equal(2, timeline.Ppz8.Length);
        Assert.Equal(8, timeline.Voices.Count(voice => voice.Id.Device.Type == ChipType.Ppz8));
        Ppz8Event first = Assert.Single(timeline.Ppz8, value => value.Channel == 0);
        Ppz8Event second = Assert.Single(timeline.Ppz8, value => value.Channel == 1);
        Assert.Equal(100, first.StartSample);
        Assert.Equal(200, first.EndSample);
        Assert.Equal(300, second.EndSample);
        Assert.Equal(3, first.Bank);
        Assert.Equal(12, first.SampleNumber);
        Assert.Equal(22_050, first.FrequencyHz);
        Assert.NotNull(first.MidiNote);
        Assert.Contains(timeline.SamplePlayback, value => value.VoiceId == "ppz8.0.pcm.1");
        Assert.Contains(timeline.SamplePlayback, value => value.VoiceId == "ppz8.0.pcm.2");
    }

    [Fact]
    public void Ppz8_StopAndRetriggerAreRecordedWithoutFabricatingPitch()
    {
        var sink = new TimelineDecoderEventSink(44_100);
        DeviceId device = new(ChipType.Ppz8, 0);
        sink.OnDevice(VisualizationDeviceCatalog.Ppz8());
        sink.OnChipWrite(new TimedChipWrite(10, device, 1, 2, 4));
        sink.OnChipWrite(new TimedChipWrite(20, device, 1, 2, 5));
        sink.OnChipWrite(new TimedChipWrite(30, device, 2, 2, 0));

        Ppz8Event[] events = sink.Complete(40, "test").Ppz8;

        Assert.Equal(2, events.Length);
        Assert.Equal(20, events[0].EndSample);
        Assert.True(events[1].IsRetrigger);
        Assert.Null(events[0].MidiNote);
        Assert.Null(events[1].MidiNote);
    }

    [Fact]
    public void AdpcmB_CapturesRegistersStopAndTrackEndClosure()
    {
        var decoder = new Ym2608TimelineDecoder();
        decoder.ApplyYm2608(0, 1, 0x02, 0x34, 10);
        decoder.ApplyYm2608(0, 1, 0x03, 0x12, 10);
        decoder.ApplyYm2608(0, 1, 0x04, 0x78, 10);
        decoder.ApplyYm2608(0, 1, 0x05, 0x56, 10);
        decoder.ApplyYm2608(0, 1, 0x09, 0x22, 10);
        decoder.ApplyYm2608(0, 1, 0x0A, 0x01, 10);
        decoder.ApplyYm2608(0, 1, 0x01, 0x81, 10);
        decoder.ApplyYm2608(0, 1, 0x00, 0x80, 100);
        decoder.ApplyYm2608(0, 1, 0x00, 0x00, 300);

        AdpcmBEvent value = Assert.Single(decoder.Complete(400, 44_100, "test").AdpcmB);

        Assert.Equal(100, value.StartSample);
        Assert.Equal(300, value.EndSample);
        Assert.Equal(0x1234, value.StartAddress);
        Assert.Equal(0x5678, value.EndAddress);
        Assert.Equal(0x0122, value.DeltaN);
        Assert.Equal(1f - 1f / 63f, value.Level, 3);
        Assert.Equal(-1f, value.Pan);
        Assert.Null(value.FrequencyHz);
    }
}
