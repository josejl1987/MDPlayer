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
        const int cases = 4096;
        for (int seed = 0; seed < cases; seed++)
        {
            var random = new Random(seed);
            var notes = new List<NoteEvent>();
            long cursor = 0;
            for (int noteIndex = 0; noteIndex < 4; noteIndex++)
            {
                long duration = random.Next(1, SampleRate / 2);
                long start = cursor;
                long end = checked(start + duration);
                double initial = InitialPitch(random, seed, noteIndex);
                var changes = new List<PitchChange>();
                int pointCount = random.Next(0, 13);
                for (int pointIndex = 1; pointIndex <= pointCount; pointIndex++)
                {
                    long sample = start + duration * pointIndex / (pointCount + 1);
                    double pitch = PitchAt(random, seed, noteIndex, pointIndex, initial);
                    changes.Add(new PitchChange(sample, 0, pitch));
                    if (pointIndex is 2 or 5)
                        changes.Add(new PitchChange(sample, 0, pitch));
                }

                var note = new NoteEvent(
                    "property-voice",
                    start,
                    end,
                    0,
                    initial,
                    $"property-test-{(seed + noteIndex) % 5}",
                    VisualizationNoteMode.Fm,
                    noteIndex > 0,
                    changes);
                notes.Add(note with
                {
                    Domain = new SourceDomainKey(
                        new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0),
                });
                // Keep very short adjacent source notes separated by at least
                // one transport tick. Two distinct attacks that quantize to the
                // same tick cannot both satisfy the global NoteOff-before-NoteOn
                // ordering invariant; same-tick attack behavior is covered by
                // the dedicated collision tests.
                cursor = end + (duration < 24 ? 24 : 0);
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
                IndependentMidiPitchValidator.Validate(timeline, export);
            }
            catch (Exception error)
            {
                throw new Xunit.Sdk.XunitException($"Property case seed {seed} failed: {error.Message}");
            }
        }
    }

    private static double InitialPitch(Random random, int seed, int noteIndex)
    {
        if (seed % 23 == 0 && noteIndex == 0)
            return 0.0;
        if (seed % 29 == 0 && noteIndex == 1)
            return 127.0;
        if (seed % 31 == 0 && noteIndex == 2)
            return 0.5;
        if (seed % 37 == 0 && noteIndex == 3)
            return 126.5;
        return 24.0 + random.NextDouble() * 72.0;
    }

    private static double PitchAt(
        Random random,
        int seed,
        int noteIndex,
        int pointIndex,
        double initial)
    {
        if (seed % 41 == 0 && noteIndex == 0)
            return pointIndex % 2 == 0 ? 0.0 : 127.0;
        if (seed % 43 == 0 && noteIndex == 1)
            return Math.Clamp(initial + (pointIndex - 1) * 0.5, 0.0, 127.0);
        if (seed % 47 == 0 && noteIndex == 2)
            return Math.Clamp(64.0 + Math.Sin(pointIndex * 0.7) * 12.0, 0.0, 127.0);
        return Math.Clamp(initial + random.NextDouble() * 8.0 - 4.0, 0.0, 127.0);
    }
}
