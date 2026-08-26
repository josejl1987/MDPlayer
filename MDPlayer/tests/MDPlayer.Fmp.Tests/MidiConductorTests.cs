using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiConductorTests
{
    [Fact]
    public void FixedTransport_StartsTempoAtTickZero()
    {
        IReadOnlyList<MidiEventBase> events = MidiConductor.FixedTransport(endTick: 960);

        Assert.Equal(0, events.OfType<MidiTempoEvent>().Single().Tick);
        Assert.Equal(500_000, events.OfType<MidiTempoEvent>().Single().MicrosecondsPerQuarter);
        Assert.Equal(960, events.OfType<MidiMetaTextEvent>().Single().Tick);
    }

    [Fact]
    public void FixedTransport_WithoutEndTickHasTempoOnly()
    {
        IReadOnlyList<MidiEventBase> events = MidiConductor.FixedTransport();

        Assert.Single(events);
        Assert.IsType<MidiTempoEvent>(events[0]);
    }
}