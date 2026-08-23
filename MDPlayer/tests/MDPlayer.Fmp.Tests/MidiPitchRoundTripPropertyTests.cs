using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiPitchRoundTripPropertyTests
{
    private const int SampleRate = 44_100;
    private const int Ppq = 960;

    [Fact]
    public void GeneratedPitchTrajectories_RoundTripThroughSerializedSmf()
    {
        const int cases = 512;
        for (int seed = 0; seed < cases; seed++)
        {
            var random = new Random(seed);
            var notes = new List<NoteEvent>();
            long cursor = 0;
            for (int noteIndex = 0; noteIndex < 4; noteIndex++)
            {
                long duration = random.Next(24, SampleRate / 2);
                long start = cursor;
                long end = checked(start + duration);
                double initial = InitialPitch(random, seed, noteIndex);
                var changes = new List<PitchChange>();
                int pointCount = random.Next(0, 7);
                for (int pointIndex = 1; pointIndex <= pointCount; pointIndex++)
                {
                    long sample = start + duration * pointIndex / (pointCount + 1);
                    double pitch = Math.Clamp(initial + random.NextDouble() * 8.0 - 4.0, 0.0, 127.0);
                    changes.Add(new PitchChange(sample, 0, pitch));
                    if (pointIndex == 2)
                        changes.Add(new PitchChange(sample, 0, pitch));
                }

                var note = new NoteEvent(
                    "property-voice",
                    start,
                    end,
                    0,
                    initial,
                    "property-test",
                    VisualizationNoteMode.Fm,
                    noteIndex > 0,
                    changes);
                notes.Add(note with
                {
                    Domain = new SourceDomainKey(
                        new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0),
                });
                cursor = end;
            }

            var timeline = new VisualizationTimeline
            {
                SampleRate = SampleRate,
                StartSample = 0,
                EndSample = cursor,
                Notes = notes,
            };
            MidiTranscriptionResult export = new MidiTranscriber(Ppq).Transcribe(timeline);

            try
            {
                IndependentMidiPitchValidator.Validate(timeline, export, Ppq);
            }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException($"Property case seed {seed} failed: {error.Message}");
            }
        }
    }

    private static double InitialPitch(Random random, int seed, int noteIndex)
    {
        if (seed % 17 == 0 && noteIndex == 0)
            return 0.25 + random.NextDouble();
        if (seed % 19 == 0 && noteIndex == 1)
            return 125.75 + random.NextDouble();
        return 24.0 + random.NextDouble() * 72.0;
    }
}
