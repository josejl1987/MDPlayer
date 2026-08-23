using Fmp.Core.Timing;

namespace Fmp.Core.Midi;

/// <summary>Builds the absolute transport conductor without musical inference.</summary>
internal static class MidiConductor
{
    public static IReadOnlyList<MidiEventBase> FromTimeMap(MusicalTimeMap map, int ppq)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (ppq <= 0 || ppq > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(ppq));

        var events = new List<MidiEventBase>();
        int sourceOrder = 0;
        int? previousTempo = null;
        foreach (TempoSegment segment in map.Segments)
        {
            long tick = map.SampleToElapsedTick(segment.StartSample, ppq);
            int tempo = segment.MicrosecondsPerQuarter;
            if (previousTempo == tempo && tick != 0)
                continue;
            events.Add(new MidiTempoEvent(tick, tempo) { SourceOrder = sourceOrder++ });
            previousTempo = tempo;
        }

        if (map.Meter is Meter meter)
        {
            events.Add(new MidiTimeSignatureEvent(0, meter.Numerator, meter.Denominator)
            {
                SourceOrder = sourceOrder,
            });
        }

        Validate(events);
        return events;
    }

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

    public static void Validate(IReadOnlyList<MidiEventBase> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        MidiTempoEvent[] tempos = events.OfType<MidiTempoEvent>().ToArray();
        if (tempos.Length == 0 || tempos.Min(tempo => tempo.Tick) != 0)
            throw new InvalidOperationException("The MIDI conductor must set tempo at tick 0.");

        foreach (MidiTimeSignatureEvent signature in events.OfType<MidiTimeSignatureEvent>())
            if (signature.Tick != 0)
                throw new InvalidOperationException(
                    "A known MIDI time signature must be emitted at tick 0.");
    }
}
