using Fmp.Core.Visualization;

namespace MDPlayer.Fmp.Tests.Fixtures;

internal static class VisualizationTimelineFixture
{
    public static VisualizationTimeline Create()
    {
        const int sampleRate = 1_000;
        return new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = 5_000,
            Instruments =
            [
                FmInstrument("fm:001", 4),
                FmInstrument("fm:002", 2),
                new InstrumentDefinition("ssg:tone-noise", "ssg", null, null, null, null, Array.Empty<FmOperatorDefinition>()),
                new InstrumentDefinition("ssg:noise", "ssg", null, null, null, null, Array.Empty<FmOperatorDefinition>()),
            ],
            Notes =
            [
                new NoteEvent(
                    "ym2608.0.fm.1", 500, 3_200, 261.63, 60,
                    "fm:001", VisualizationNoteMode.Fm, false,
                    [new PitchChange(1_700, 277.18, 61), new PitchChange(2_300, 293.66, 62)]),
                new NoteEvent(
                    "ym2608.0.fm.1", 3_200, 4_000, 329.63, 64,
                    "fm:002", VisualizationNoteMode.Fm, true,
                    Array.Empty<PitchChange>()),
                new NoteEvent(
                    "ym2608.0.fm.3", 1_200, 3_800, 392.00, 67,
                    "fm:001", VisualizationNoteMode.Fm, false,
                    Array.Empty<PitchChange>()),
                new NoteEvent(
                    "ym2608.0.fm3.op.1", 1_300, 3_000, 196.00, 55,
                    "fm:001", VisualizationNoteMode.Fm3Operator, false,
                    Array.Empty<PitchChange>()),
                new NoteEvent(
                    "ym2608.0.fm3.op.2", 1_500, 3_400, 246.94, 59,
                    "fm:002", VisualizationNoteMode.Fm3Operator, false,
                    Array.Empty<PitchChange>()),
                new NoteEvent(
                    "ym2608.0.ssg.1", 1_000, 3_600, 440.00, 69,
                    "ssg:tone-noise", VisualizationNoteMode.SsgToneNoise, false,
                    [new PitchChange(2_000, 466.16, 70)]),
                new NoteEvent(
                    "ym2608.0.ssg.2", 1_100, 3_700, 0, -1,
                    "ssg:noise", VisualizationNoteMode.SsgNoise, false,
                    Array.Empty<PitchChange>()),
            ],
            Rhythm =
            [
                new RhythmEvent("bd", "ym2608.0.rhythm.bd", 1_300, 1.0f, 0, InstrumentId: "rhythm:bd"),
                new RhythmEvent("sd", "ym2608.0.rhythm.sd", 1_550, 0.8f, 0, InstrumentId: "rhythm:sd"),
                new RhythmEvent("hh", "ym2608.0.rhythm.hh", 1_800, 0.5f, 0, InstrumentId: "rhythm:hh"),
                new RhythmEvent("tom", "ym2608.0.rhythm.tom", 2_200, 0.7f, 0, InstrumentId: "rhythm:tom"),
                new RhythmEvent("rim", "ym2608.0.rhythm.rim", 2_500, 0.6f, 0, InstrumentId: "rhythm:rim"),
            ],
        };
    }

    private static InstrumentDefinition FmInstrument(string id, int algorithm)
    {
        var operators = Enumerable.Range(0, 4)
            .Select(_ => new FmOperatorDefinition(31, 10, 5, 4, 8, 20, 1, 2, 0, false, 0))
            .ToArray();
        return new InstrumentDefinition(id, "fm", algorithm, 3, 0, 2, operators);
    }
}
