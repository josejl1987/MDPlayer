using Fmp.Core.Midi;
using Fmp.Core.Timing;
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

    [Fact]
    public void MusicalTimeMap_TempoChangesPreserveSourceSecondsAfterSerializedReadback()
    {
        const int sampleRate = 1_000;
        var map = new MusicalTimeMap(
            sampleRate,
            0,
            new[]
            {
                new TempoSegment(0, 1_000, 0, 500, 120, TimingSource.UserOverride, 1),
                new TempoSegment(1_000, 2_000, 2, 1_000, 60, TimingSource.UserOverride, 1),
            });
        var timeline = new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = 2_000,
            Notes = new[]
            {
                Note(0, 400, 60),
                Note(1_000, 1_400, 62),
                Note(1_500, 1_900, 64),
            },
        };

        MidiTranscriptionResult export = new MidiTranscriber(Ppq, map).Transcribe(timeline);
        long[] noteOnTicks = MidiRoundTrip.TimedEvents(export.Bytes, 1)
            .Where(item => item.Event is DryWetNoteOn noteOn && noteOn.Velocity != 0)
            .Select(item => item.Tick)
            .ToArray();
        Assert.Equal(new[] { 0L, 1_920L, 2_400L }, noteOnTicks);

        SetTempoEvent[] tempos = MidiRoundTrip.TimedEvents(export.Bytes, 0)
            .Select(item => item.Event)
            .OfType<SetTempoEvent>()
            .ToArray();
        Assert.Equal(new long[] { 500_000, 1_000_000 },
            tempos.Select(value => value.MicrosecondsPerQuarterNote).ToArray());
        Assert.All(MidiRoundTrip.TimedEvents(export.Bytes, 0)
            .Where(item => item.Event is SetTempoEvent), item =>
            Assert.True(item.Tick is 0 or 1_920));

        Assert.Equal(0.0, SecondsAtTick(0, tempos), precision: 9);
        Assert.Equal(1.0, SecondsAtTick(1_920, tempos), precision: 9);
        Assert.Equal(1.5, SecondsAtTick(2_400, tempos), precision: 9);
    }

    private static double SecondsAtTick(long tick, IReadOnlyList<SetTempoEvent> tempos)
    {
        const int ppq = Ppq;
        long previousTick = 0;
        double seconds = 0;
        for (int index = 0; index < tempos.Count; index++)
        {
            long tempoTick = index == 0 ? 0 : (index == 1 ? 1_920 : long.MaxValue);
            long end = Math.Min(tick, tempoTick);
            if (end > previousTick)
                seconds += (end - previousTick) * tempos[index - 1].MicrosecondsPerQuarterNote
                    / 1_000_000.0 / ppq;
            if (tick <= tempoTick)
                return seconds;
            previousTick = tempoTick;
        }
        return seconds + (tick - previousTick) * tempos[^1].MicrosecondsPerQuarterNote
            / 1_000_000.0 / ppq;
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
