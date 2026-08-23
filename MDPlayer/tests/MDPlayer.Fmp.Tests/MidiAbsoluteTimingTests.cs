using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Xunit;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;
using DryWetNoteOn = Melanchall.DryWetMidi.Core.NoteOnEvent;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiAbsoluteTimingTests
{
    private const int SampleRate = 44_100;
    private const int Ppq = 960;

    [Fact]
    public void SerializedEvents_PreserveSourceSecondsWithinHalfTick()
    {
        var notes = new[]
        {
            Note(100_000, 110_000, 60),
            Note(110_123, 120_123, 62),
            Note(120_987, 130_987, 64),
            Note(131_555, 141_555, 65),
        };
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 100_000,
            EndSample = 150_000,
            Notes = notes,
        };

        MidiTranscriptionResult export = new MidiTranscriber(Ppq).Transcribe(timeline);
        SetTempoEvent tempo = MidiRoundTrip.TimedEvents(export.Bytes, 0)
            .Select(item => item.Event)
            .OfType<SetTempoEvent>()
            .Single();
        DryWetNoteOn[] noteOns = MidiRoundTrip.TimedEvents(export.Bytes, 1)
            .Where(item => item.Event is DryWetNoteOn noteOn && noteOn.Velocity != 0)
            .Select(item => (DryWetNoteOn)item.Event)
            .ToArray();

        Assert.Equal(notes.Length, noteOns.Length);
        double halfTickSeconds = tempo.MicrosecondsPerQuarterNote / 1_000_000.0 / Ppq / 2.0;
        for (int index = 0; index < notes.Length; index++)
        {
            double sourceSeconds = (notes[index].StartSample - timeline.StartSample) / (double)SampleRate;
            long tick = MidiRoundTrip.TimedEvents(export.Bytes, 1)
                .Where(item => item.Event is DryWetNoteOn noteOn && noteOn.Velocity != 0)
                .ElementAt(index)
                .Tick;
            double midiSeconds = tick * tempo.MicrosecondsPerQuarterNote / 1_000_000.0 / Ppq;
            Assert.InRange(Math.Abs(sourceSeconds - midiSeconds), 0, halfTickSeconds + 1e-12);
        }
    }

    private static NoteEvent Note(long start, long end, double pitch) => new NoteEvent(
        "absolute-time-voice",
        start,
        end,
        0,
        pitch,
        "timing-test",
        VisualizationNoteMode.Fm,
        false,
        Array.Empty<PitchChange>()) with
    {
        Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0),
    };
}
