using System.Buffers.Binary;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class VgmDacStreamTests
{
    [Fact]
    public void MultipleTypeZeroBlocksAccumulateAndLegacySeekUsesCumulativeBank()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgm(
            [
                (0x00, new byte[] { 0x01, 0x02, 0x03 }),
                (0x00, new byte[] { 0x04, 0x05 }),
            ],
            [0xE0, 0x03, 0x00, 0x00, 0x00, 0x8F, 0x66]));

        Assert.Equal([0x04], document.Writes
            .Where(write => write.Address == 0x2A)
            .Select(write => write.Data));
        Assert.Equal(15, document.EndSample);
    }

    [Fact]
    public void Legacy8FWaitsFifteenSourceSamples()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgm(
            (0x00, new byte[] { 0x55 }),
            commands: [0x8F, 0x66]));

        VgmRegisterWrite write = Assert.Single(document.Writes);
        Assert.Equal(0, write.SourceSample);
        Assert.Equal(15, document.EndSample);
    }

    [Fact]
    public void StreamAt11025HzEmitsFourBytesWithFourSampleSpacing()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, _) = CreateController(
            [0x10, 0x20, 0x30, 0x40]);
        controller.SetFrequency(0, 11_025);
        controller.Start(0, 0, 0x01, 4);
        controller.AdvanceTime(0, 20);

        Assert.Equal([0x10, 0x20, 0x30, 0x40], writes.Select(write => write.Data));
        Assert.Equal([1L, 5L, 9L, 13L], writes.Select(write => write.SourceSample));
    }

    [Fact]
    public void RationalSchedulingDoesNotAccumulateFloatingPointDrift()
    {
        const long scale = 1L << 32;
        long increment = (1_000L * scale + 22_050) / 44_100;
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, _) = CreateController(
            new byte[1_000]);
        controller.SetFrequency(0, 1_000);
        controller.Start(0, 0, 0x01, 1_000);
        controller.AdvanceTime(0, 50_000);

        Assert.Equal(1_000, writes.Count);
        for (int index = 0; index < writes.Count; index++)
        {
            long expected = 1 + CeilingDivide((long)index * scale, increment);
            Assert.Equal(expected, writes[index].SourceSample);
        }
    }

    [Fact]
    public void Ym2612StreamTargetingNonDacRegisterDoesNotEmitDacControls()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, List<Control> controls) =
            CreateController([0x10, 0x20]);
        controller.TryHandleSetup(0, 0x02, 0, 0x28);
        controller.SetFrequency(0, 44_100);
        controller.Start(0, 0, 0x01, 2);
        controller.AdvanceTime(0, 10);

        Assert.NotEmpty(writes);
        Assert.All(writes, write => Assert.Equal(0x28, write.Address));
        Assert.Empty(controls);

        var builder = new TimelineBuilder(44_100);
        var decoder = new Ym2612TimelineDecoder();
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        decoder.Initialize(device, builder);
        decoder.Process(new TimedChipWrite(0, device.Id, 0, 0x2B, 0x80));
        foreach (VgmRegisterWrite write in writes)
            decoder.Process(new TimedChipWrite(write.SourceSample, write.Device, write.Port, write.Address, write.Data));
        decoder.Complete(20);

        Assert.Empty(builder.Build(20, "test").SamplePlayback);
    }

    [Fact]
    public void SelectingBankBeforeItsDataBlockStillAllowsLaterStart()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgm(
            [],
            [
                0x90, 0x00, 0x02, 0x00, 0x2A,
                0x91, 0x00, 0x00, 0x01, 0x00,
                0x67, 0x66, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10, 0x20,
                0x92, 0x00, 0x44, 0xAC, 0x00, 0x00,
                0x93, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00,
                0x61, 0x10, 0x00,
                0x66,
            ]));

        Assert.Equal([0x10, 0x20], document.Writes
            .Where(write => write.Address == 0x2A)
            .Select(write => write.Data));
    }

    [Fact]
    public void StopAllStopsTwoSimultaneouslyActiveStreams()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, List<Control> controls) =
            CreateController(new byte[64]);
        controller.TryHandleSetup(1, 0x02, 0, 0x2A);
        controller.SetData(1, 0, 1, 0);
        controller.SetFrequency(0, 4_410);
        controller.SetFrequency(1, 4_410);
        controller.Start(0, 0, 0x01, 64);
        controller.Start(1, 0, 0x01, 64);
        controller.AdvanceTime(0, 100);
        int writesBeforeStop = writes.Count;
        controller.Stop(0xFF);
        controller.AdvanceTime(100, 100);

        Assert.Equal(writesBeforeStop, writes.Count);
        Assert.Equal(2, controls.Count(control => control.Kind == Ym2612DacStreamControlKind.Stop));
    }

    [Fact]
    public void UntilEndModePlaysExactlyToBankEnd()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, List<Control> controls) =
            CreateController([0x10, 0x20, 0x30]);
        controller.SetFrequency(0, 44_100);
        controller.Start(0, 0, 0x03, 0);
        controller.AdvanceTime(0, 10);

        Assert.Equal([0x10, 0x20, 0x30], writes.Select(write => write.Data));
        Assert.Single(controls, control => control.Kind == Ym2612DacStreamControlKind.NaturalEnd);
    }

    [Fact]
    public void RawByteLengthModeDividesLengthByStepSize()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, _) =
            CreateController([0x10, 0xA0, 0x20, 0xB0, 0x30]);
        controller.SetData(0, 0, 2, 0);
        controller.SetFrequency(0, 44_100);
        controller.Start(0, 0, 0x0F, 5);
        controller.AdvanceTime(0, 10);

        Assert.Equal([0x10, 0x20], writes.Select(write => write.Data));
    }

    [Fact]
    public void FrequencyChangePreservesBytePositionAndProducesRateMarker()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, List<Control> controls) =
            CreateController(Enumerable.Range(0, 64).Select(index => (byte)index).ToArray());
        controller.SetFrequency(0, 4_410);
        controller.Start(0, 0, 0x01, 32);
        controller.AdvanceTime(0, 25);
        int before = writes.Count;
        controller.SetFrequency(0, 8_820);
        controller.AdvanceTime(25, 25);

        Assert.True(before > 0);
        Assert.True(writes.Count > before);
        Assert.Equal(Enumerable.Range(0, writes.Count), writes.Select(write => write.Data));
        Assert.Contains(controls, control => control.Kind == Ym2612DacStreamControlKind.RateChanged);
        Assert.DoesNotContain(controls, control => control.Kind == Ym2612DacStreamControlKind.Start
            && control.Sample > 0);
    }

    [Fact]
    public void StopPreventsSubsequentWrites()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, List<Control> controls) =
            CreateController(new byte[64]);
        controller.SetFrequency(0, 4_410);
        controller.Start(0, 0, 0x01, 64);
        controller.AdvanceTime(0, 100);
        int count = writes.Count;
        controller.Stop(0);
        controller.AdvanceTime(100, 100);

        Assert.Equal(count, writes.Count);
        Assert.Contains(controls, control => control.Kind == Ym2612DacStreamControlKind.Stop
            && control.Sample == 100);
    }

    [Fact]
    public void FastStartResolvesBlockNumberWithinSelectedBank()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, _) = CreateController(
            [0xAA, 0xBB], [0x11, 0x22, 0x33]);
        controller.SetFrequency(0, 11_025);
        controller.Start(0, 0, 0, 0, fastStart: true, blockId: 1);
        controller.AdvanceTime(0, 20);

        Assert.Equal([0x11, 0x22, 0x33], writes.Select(write => write.Data));
    }

    [Fact]
    public void StepBaseAndReverseStayWithinBankBounds()
    {
        (VgmDacStreamController stepped, List<VgmRegisterWrite> writes, _) = CreateController(
            [0x10, 0xA0, 0x20, 0xB0, 0x30, 0xC0]);
        stepped.SetFrequency(0, 44_100);
        stepped.SetData(0, 0, 2, 1);
        stepped.Start(0, 0, 0x01, 3);
        stepped.AdvanceTime(0, 10);
        Assert.Equal([0xA0, 0xB0, 0xC0], writes.Select(write => write.Data));

        (VgmDacStreamController reversed, List<VgmRegisterWrite> reverseWrites, _) = CreateController(
            [0x10, 0x20, 0x30, 0x40]);
        reversed.SetFrequency(0, 44_100);
        reversed.Start(0, 0, 0x11, 4);
        reversed.AdvanceTime(0, 10);
        Assert.Equal([0x40, 0x30, 0x20, 0x10], reverseWrites.Select(write => write.Data));
    }

    [Fact]
    public void LoopRetriggersWithoutDuplicatingPayloadIdentity()
    {
        (VgmDacStreamController controller, List<VgmRegisterWrite> writes, List<Control> controls) =
            CreateController([0x10, 0x20]);
        controller.SetFrequency(0, 22_050);
        controller.Start(0, 0, 0x81, 2);
        controller.AdvanceTime(0, 30);

        Assert.Equal([0x10, 0x20, 0x10, 0x20], writes.Take(4).Select(write => write.Data));
        Assert.Contains(controls, control => control.Kind == Ym2612DacStreamControlKind.Retrigger);
    }

    [Fact]
    public void DacWaveformPreviewNormalizesUnsignedPcmAndBoundsSize()
    {
        WaveformEnvelopePoint[] shortPreview = DacWaveformPreviewBuilder.Build(
            [0x00, 0x40, 0x80, 0xC0, 0xFF]);
        Assert.Equal(5, shortPreview.Length);
        Assert.True(shortPreview[0].Minimum < 0);
        Assert.InRange(shortPreview[2].Minimum, -0.01f, 0.01f);
        Assert.True(shortPreview[4].Maximum > 0);

        Assert.True(DacWaveformPreviewBuilder.Build(new byte[10_000]).Length <= 256);
    }

    [Fact]
    public void MinimalVgmReachesDacTimelineWithPreviewAndOneDeduplicatedAsset()
    {
        byte[] vgm = CreateVgm(
            (0x00, new byte[] { 0x10, 0x20, 0x30, 0x40 }),
            commands:
            [
                0x52, 0x2B, 0x80,
                0x90, 0x00, 0x02, 0x00, 0x2A,
                0x91, 0x00, 0x00, 0x01, 0x00,
                0x92, 0x00, 0x11, 0x2B, 0x00, 0x00,
                0x93, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04, 0x00, 0x00, 0x00,
                0x61, 0x10, 0x00,
                0x66,
            ]);

        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-dac-{Guid.NewGuid():N}.vgm");
        try
        {
            File.WriteAllBytes(path, vgm);
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, OutputAudioPath: null, SampleRate: 44_100),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2612);
            Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "ym2612.0.pcm.dac");
            SampleDefinition sample = Assert.Single(timeline.Samples);
            SamplePlaybackEvent playback = Assert.Single(timeline.SamplePlayback);
            Assert.Equal(sample.Id, playback.SampleId);
            Assert.NotEmpty(sample.Preview);
            Assert.Equal(1.0, playback.PlaybackRate);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static (VgmDacStreamController Controller, List<VgmRegisterWrite> Writes, List<Control> Controls)
        CreateController(params byte[][] blocks)
    {
        var writes = new List<VgmRegisterWrite>();
        var controls = new List<Control>();
        var controller = new VgmDacStreamController(
            _ => { },
            writes.Add,
            (kind, sample, stream, device, rate) => controls.Add(new Control(kind, sample, stream, device, rate)));
        foreach (byte[] block in blocks)
            controller.AppendDataBlock(0, block);
        controller.TryHandleSetup(0, 0x02, 0, 0x2A);
        controller.SetData(0, 0, 1, 0);
        return (controller, writes, controls);
    }

    private static byte[] CreateVgm(
        (byte Type, byte[] Payload) block,
        params byte[] commands)
        => CreateVgm([block], commands);

    private static byte[] CreateVgm(
        (byte Type, byte[] Payload)[] blocks,
        byte[] commands)
    {
        int size = 0x40 + commands.Length;
        foreach ((byte _, byte[] payload) in blocks)
            size += 7 + payload.Length;
        byte[] data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), 3_579_545);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C, 4), 7_670_454);
        int cursor = 0x40;
        foreach ((byte type, byte[] payload) in blocks)
        {
            data[cursor++] = 0x67;
            data[cursor++] = 0x66;
            data[cursor++] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), (uint)payload.Length);
            cursor += 4;
            payload.CopyTo(data, cursor);
            cursor += payload.Length;
        }
        commands.CopyTo(data, cursor);
        return data;
    }

    private static long CeilingDivide(long numerator, long denominator) =>
        numerator / denominator + (numerator % denominator == 0 ? 0 : 1);

    private sealed record Control(
        Ym2612DacStreamControlKind Kind,
        long Sample,
        byte Stream,
        DeviceId Device,
        double? Rate);
}
