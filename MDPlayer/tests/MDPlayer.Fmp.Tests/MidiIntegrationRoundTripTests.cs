using Fmp.Application.Export;
using Fmp.Core.Midi;
using Fmp.Core.Rendering;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// WP06 T040 - timing-source acceptance matrix. Builds timelines and verifies
/// automatic, driver, fixed, strict, and symbolic timing-source selection plus
/// diagnostics and deterministic inference behavior.
/// </summary>
public sealed class MidiIntegrationRoundTripTests
{
    private const int Sr = 44_100;

    /* FR-005 */
    [Fact]
    public void Auto_SelectsStrongestSource()
    {
        // Driver beats present: auto must prefer driver evidence (phase
        // authoritative, not inferred) over symbolic heuristics, and report the
        // selected source rather than hiding it.
        TimelineFixture fx = new(Sr, 120, withBeats: true);
        var build = MusicalTimeMapBuilder.Build(fx.Timeline, new MusicalTimeMapOptions { DetectTempoChanges = true });
        Assert.False(build.Diagnostics.TempoInferred, "auto must not fall back to symbolic inference when driver evidence exists");
        Assert.False(build.Diagnostics.PhaseUnknown);
        Assert.True(build.Diagnostics.PhaseAuthoritative);
        Assert.Equal(TimingSource.DriverValidatedTempo, build.Diagnostics.TempoSource);
    }

    [Fact]
    public void Driver_Fails_WithoutAuthority()
    {
        TimelineFixture fx = new(Sr, 120, withBeats: false, withValidatedBpm: false);
        Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(fx.Timeline, new MusicalTimeMapOptions
            { Source = TimingSource.DriverBeatAnchors }));
    }

    [Fact]
    public void Fixed_WithoutPhase_NotClaimedBeatAligned()
    {
        TimelineFixture fx = new(Sr, 120, withBeats: false);
        var build = MusicalTimeMapBuilder.Build(fx.Timeline, new MusicalTimeMapOptions { FixedBpm = 120 });
        Assert.Equal(TimingSource.UserOverride, build.Diagnostics.TempoSource);
        Assert.True(build.Diagnostics.PhaseUnknown);
    }

    [Fact]
    public void Strict_RejectsUnresolvedAlignment()
    {
        TimelineFixture fx = new(Sr, 120, withBeats: false);
        Assert.Throws<MusicalTimingException>(() =>
            MusicalTimeMapBuilder.Build(fx.Timeline, new MusicalTimeMapOptions
            { FixedBpm = 120, StrictTiming = true }));
    }

    [Fact]
    public void NonStrict_Fallback_VisibleInWarnings()
    {
        TimelineFixture fx = new(Sr, 120, withBeats: false);
        var build = MusicalTimeMapBuilder.Build(fx.Timeline, new MusicalTimeMapOptions { FixedBpm = 120 });
        Assert.True(build.Diagnostics.PhaseUnknown);
        Assert.Contains(build.Diagnostics.Warnings, w => w.Contains("phase is unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Symbolic_Forcing_IsInferred_AndReported_NeverBeatAligned()
    {
        // --tempo-source symbolic forces the symbolic inference path. The result
        // must be labeled inferred (never beat-aligned-authoritative), and any
        // half/double-tempo ambiguity it surfaces must appear in warnings rather
        // than being hidden behind an arbitrary confidence (section 42).
        double spq70 = Sr * 60.0 / 70.0;
        long step = (long)Math.Round(spq70 / 2);
        var notes = Enumerable.Range(0, 16)
            .Select(i => NewNote("v", i * step, i * step + 500, 60)).ToArray();
        var timeline = new VisualizationTimeline
        {
            StartSample = 0, EndSample = 16 * step + 10_000, SampleRate = Sr, Notes = notes,
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        { Source = TimingSource.SymbolicInference });
        Assert.True(build.Diagnostics.TempoInferred, "symbolic forcing must be inferred, not authoritative");
        Assert.True(build.Diagnostics.PhaseInferred);
        // The resolved source is reported.
        Assert.Equal(TimingSource.SymbolicInference, build.Diagnostics.TempoSource);
        // When ambiguity is flagged, it must be visible in the warnings.
        if (build.Diagnostics.TempoAmbiguous)
            Assert.Contains(build.Diagnostics.Warnings, w => w.Contains("ambiguity", StringComparison.OrdinalIgnoreCase));
    }


    private static NoteEvent NewNote(string voice, long start, long end, int midi)
        => new(voice, start, end, 440, midi, "inst", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>());


    private sealed class TimelineFixture
    {
        public TimelineFixture(int sr, double bpm, bool withBeats, bool withValidatedBpm = true)
        {
            double spq = sr * 60.0 / bpm;
            var notes = Enumerable.Range(0, 8)
                .Select(i => NewNote("v", (long)Math.Round(i * 4 * spq), (long)Math.Round(i * 4 * spq) + 2000, 60 + i))
                .ToArray();
            var timing = withValidatedBpm
                ? new[] { new DriverTimingEvent((long)Math.Round(1 * spq), 0x42, bpm) }
                : Array.Empty<DriverTimingEvent>();
            Timeline = new VisualizationTimeline
            {
                StartSample = 0,
                EndSample = (long)Math.Round(8 * 4 * spq) + 20_000,
                SampleRate = sr,
                Notes = notes,
                Beats = withBeats
                    ? Enumerable.Range(0, 40).Select(i => new BeatEvent((long)Math.Round(i * spq), i)).ToArray()
                    : Array.Empty<BeatEvent>(),
                Timing = timing,
                Source = new TrackMetadata("vgz", "song", "chip", "song.vgz"),
            };
            FixedBpm = null;
            Meter = new Meter(4, 4);
        }

        public VisualizationTimeline Timeline { get; }
        public VisualizationTimeline? TimelineOverride { get; init; }
        public double? FixedBpm { get; init; }
        public Meter? Meter { get; init; }
    }
}
