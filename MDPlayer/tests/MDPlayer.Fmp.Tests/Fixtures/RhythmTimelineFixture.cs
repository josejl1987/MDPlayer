using Fmp.Core.Visualization;

namespace MDPlayer.Fmp.Tests.Fixtures;

/// <summary>
/// Rhythm-focused timeline fixture for PR8 tests. Uses a single voice (bd)
/// with three distinct pan values at the same sample position, so the pan-tick
/// x-offset can be compared without confounding onset x. Strength varies to
/// exercise the decay-trail alpha and pulse width. Sample rate 1_000 means
/// 1 ms = 1 sample, keeping the §15.2 decay constants (90/300 ms) easy to
/// reason about in frame indices.
/// </summary>
internal static class RhythmTimelineFixture
{
    public const int SampleRate = 1_000;
    public const int OnsetSample = 1_000;

    public static VisualizationTimeline Create()
    {
        return new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 5_000,
            Instruments = Array.Empty<InstrumentDefinition>(),
            Notes = Array.Empty<NoteEvent>(),
            Rhythm =
            [
                // Three bd hits at the same onset, different pans and strengths.
                // Same voice+sample means the impact blocks share an x; only the
                // pan tick differs (§15.3).
                new RhythmEvent("bd", "ym2608.0.rhythm.bd", OnsetSample, 1.0f, -1f),
                new RhythmEvent("bd", "ym2608.0.rhythm.bd", OnsetSample + 600, 1.0f, 0f),
                new RhythmEvent("bd", "ym2608.0.rhythm.bd", OnsetSample + 1200, 1.0f, 1f),
            ],
        };
    }
}
