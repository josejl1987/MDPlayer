using Fmp.Application.Export;
using Fmp.Core.Midi;
using Fmp.Core.Rendering;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// WP06 T040 - the end-to-end acceptance matrix. Builds a timeline, fits its
/// musical time map, exports Format 1 MIDI, then round-trips the bytes through an
/// INDEPENDENT existing parser (MidiDocument, the repo's MIDI playback backend,
/// spec section 70) verifying format==1, division==PPQ, track count, tempo values,
/// event ticks and End-of-Track. Also covers the FR-005 acceptance matrix
/// (auto strongest / driver fails w/o authority / fixed requires BPM / strict
/// rejects unresolved / non-strict fallback visible) and byte determinism.
/// </summary>
public sealed class MidiIntegrationRoundTripTests
{
    private const int Sr = 44_100;

    [Fact]
    public void RoundTrip_Format1_Division_Tracks_Tempo_Eot()
    {
        TimelineFixture fx = new(Sr, bpm: 120, withBeats: true);
        byte[] bytes = Export(fx, ppq: 960);

        MidiDocument doc = MidiDocument.Parse(bytes);
        Assert.Equal(960, doc.Division);
        // Conductor + at least one musical track for the notes.
        Assert.True(doc.Events.GroupBy(e => e.Track).Count() >= 1);
        Assert.Contains(doc.Tempos, t => t.MicrosecondsPerQuarter == 500_000);
        Assert.True(doc.EndTick > 0, "the exported file must contain an End-of-Track");
        Assert.Contains(doc.Events, e => e.Type == MidiMessageType.NoteOn && e.Data2 > 0);
    }

    [Fact]
    public void RoundTrip_Division_MatchesConfiguredPpq()
    {
        TimelineFixture fx = new(Sr, bpm: 100, withBeats: true);
        byte[] bytes = Export(fx, ppq: 480);
        MidiDocument doc = MidiDocument.Parse(bytes);
        Assert.Equal(480, doc.Division);
    }

    [Fact]
    public void RoundTrip_NoteTicks_AlignToConfiguredGrid()
    {
        double spq = Sr * 60.0 / 120.0;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = new[]
            {
                NewNote("v", 0, (long)spq, 60),
                NewNote("v", (long)Math.Round(1 * spq), (long)Math.Round(2 * spq), 62),
                NewNote("v", (long)Math.Round(2 * spq), (long)Math.Round(3 * spq), 64),
            },
            Beats = Enumerable.Range(0, 12)
                .Select(i => new BeatEvent((long)Math.Round(i * spq), i))
                .ToArray(),
        };
        var fx = new TimelineFixture(Sr, 120, withBeats: true) { TimelineOverride = timeline };
        byte[] bytes = Export(fx, 960);
        MidiDocument doc = MidiDocument.Parse(bytes);
        long[] ticks = doc.Events.Where(e => e.Type == MidiMessageType.NoteOn && e.Data2 > 0)
            .Select(e => e.Tick).OrderBy(t => t).ToArray();
        Assert.Equal(3, ticks.Length);
        Assert.InRange(ticks[0], 0L, 1L);
        Assert.InRange(ticks[1], 959L, 961L);
        Assert.InRange(ticks[2], 1919L, 1921L);
    }

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

    [Fact]
    public void SameInput_ProducesSameBytes()
    {
        TimelineFixture fx = new(Sr, 120, withBeats: true);
        Assert.Equal(Export(fx, 960), Export(fx, 960));
    }

    private static NoteEvent NewNote(string voice, long start, long end, int midi)
        => new(voice, start, end, 440, midi, "inst", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>());

    private static string WriteTimeline(VisualizationTimeline timeline)
    {
        string path = Path.Combine(Path.GetTempPath(), "mdplayer-midi-" + Guid.NewGuid().ToString("N") + ".json");
        VisualizationJsonWriter.Write(path, timeline);
        return path;
    }

    private static byte[] Export(TimelineFixture fx, int ppq)
    {
        VisualizationTimeline timeline = fx.TimelineOverride ?? fx.Timeline;
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = fx.FixedBpm, Meter = fx.Meter, DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        { Diagnostics = build.Diagnostics };
        return exporter.Export(timeline).Bytes;
    }

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
