namespace Fmp.Core.Midi;

/// <summary>Builds the absolute fixed transport conductor without musical inference.</summary>
internal static class MidiConductor
{
    /// <summary>
    /// The single fixed transport: 120 BPM tempo at tick 0, optionally pinned to
    /// a nominal end by a trailing End marker. No time signatures are emitted.
    /// </summary>
    public static IReadOnlyList<MidiEventBase> FixedTransport(long? endTick = null)
    {
        var events = new List<MidiEventBase>
        {
            new MidiTempoEvent(0, MidiTranscriber.TransportMicrosecondsPerQuarter)
            {
                SourceOrder = 0,
            },
        };
        if (endTick is long markerTick)
        {
            if (markerTick < 0)
                throw new ArgumentOutOfRangeException(nameof(endTick));
            events.Add(new MidiMetaTextEvent(markerTick, 0x06, "End")
            {
                SourceOrder = 1,
            });
        }
        return events;
    }
}