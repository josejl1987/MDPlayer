using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiConductorTests
{
    [Fact]
    public void FixedTransport_StartsTempoAtTickZero()
    {
        IReadOnlyList<MidiEventBase> events = MidiConductor.FixedTransport(endTick: 960);

        MidiConductor.Validate(events);
        Assert.Equal(0, events.OfType<MidiTempoEvent>().Single().Tick);
        Assert.Equal(960, events.OfType<MidiMetaTextEvent>().Single().Tick);
    }

    [Fact]
    public void Validate_RejectsDelayedTempo()
    {
        var events = new MidiEventBase[] { new MidiTempoEvent(1, 500_000) };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => MidiConductor.Validate(events));

        Assert.Contains("tick 0", error.Message);
    }

    [Fact]
    public void Validate_RejectsDelayedKnownMeter()
    {
        var events = new MidiEventBase[]
        {
            new MidiTempoEvent(0, 500_000),
            new MidiTimeSignatureEvent(10, 4, 4),
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => MidiConductor.Validate(events));

        Assert.Contains("tick 0", error.Message);
    }
}
