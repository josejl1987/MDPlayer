using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class VisualizationCaptureSinkTests
{
    [Fact]
    public void WriteYm2608_ForwardsAndDecodesTheSameWrite()
    {
        var downstream = new RecordingSink();
        var decoder = new Ym2608TimelineDecoder();
        var capture = new VisualizationCaptureSink(decoder, downstream);

        capture.WriteYm2608(0, 0, 0xA0, 0x35, 10);
        capture.WriteYm2608(0, 0, 0xA4, 0x21, 10);
        capture.WriteYm2608(0, 0, 0x28, 0xF0, 20);
        capture.WriteYm2608(0, 0, 0x28, 0x00, 30);

        Assert.Equal(4, downstream.YmWrites.Count);
        Assert.Single(decoder.Complete(40, 44_100, "test").Notes);
    }

    private sealed class RecordingSink : IFmpChipSink
    {
        public List<(int Chip, int Port, int Address, int Value, long Sample)> YmWrites { get; } = [];

        public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
        {
            YmWrites.Add((chipId, port, address, value, samplePosition));
        }

        public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
        {
        }

        public void WritePpz8(int port, int address, int value, long samplePosition)
        {
        }
    }
}
