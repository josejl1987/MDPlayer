using Fmp.Core.Midi;
using Fmp.Core.Timing;
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

    [Fact]
    public void FromTimeMap_EmitsTempoChangesAndKnownMeterAtTickZero()
    {
        var map = new MusicalTimeMap(
            sampleRate: 1_000,
            startSample: 0,
            segments: new[]
            {
                new TempoSegment(0, 1_000, 0, 500, 120, TimingSource.UserOverride, 1),
                new TempoSegment(1_000, 2_000, 2, 1_000, 60, TimingSource.UserOverride, 1),
            },
            meter: new Meter(4, 4));

        IReadOnlyList<MidiEventBase> events = MidiConductor.FromTimeMap(map, 960);

        Assert.Equal(
            new[] { (0L, 500_000), (1_920L, 1_000_000) },
            events.OfType<MidiTempoEvent>().Select(eventValue =>
                (eventValue.Tick, eventValue.MicrosecondsPerQuarter)));
        Assert.Equal(0, events.OfType<MidiTimeSignatureEvent>().Single().Tick);
        MidiConductor.Validate(events);
    }
}
