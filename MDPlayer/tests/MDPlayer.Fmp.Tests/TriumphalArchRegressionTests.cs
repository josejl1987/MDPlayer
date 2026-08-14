using Fmp.Core.Midi;
using Fmp.Core.Rendering;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Regression pin for the Gradius II - 53 Triumphal Arch user report (INV1/2/4/5):
/// the AY8910 noise "loop" double-triggered the drum channel every frame, the SCC
/// pitch register byte pairs produced clusters of tiny glitch notes, and the AY
/// period-2 ultrasonic init states became sustained audible MIDI garbage. The
/// structural invariants below hold on the real captured file, without per-song
/// event counts.
/// </summary>
public sealed class TriumphalArchRegressionTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    [Fact]
    public void TriumphalArch_NoOutOfDomainPitch_NoDuplicateTriggers_NoGlitchNotes()
    {
        VisualizationTimeline timeline = Capture("testfixtures/53-triumphal-arch.vgz");
        Assert.True(timeline.Notes.Count > 0, "captured music must actually emit some notes");

        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(exporter.Export(timeline).Bytes);

        var noteOns = decoded.Events
            .SelectMany(ep => ep.Value)
            .Where(e => e.Event is NoteOnEvent on && on.Velocity != 0)
            .ToList();
        Assert.NotEmpty(noteOns);

        // INV5: the AY period-2 ultrasonic init state (MIDI ~164.88) is suppressed
        // rather than clamped or wrapped — every decoded note's EFFECTIVE pitch
        // (base note + bend) stays inside [0, 127].
        foreach (var ep in decoded.Events)
        {
            var state = decoded.State[ep.Key];
            foreach (var e in ep.Value)
            {
                if (e.Event is NoteOnEvent on && on.Velocity != 0)
                {
                    double effective = MidiSemanticDecoder.EffectivePitch(
                        on.NoteNumber, state.ActiveBend, state.BendRange);
                    Assert.InRange(effective, 0.0, 127.0);
                }
            }
        }

        // INV2/INV3: at most one NoteOn per (channel, tick, note) — the noise
        // "loop" that re-armed the drum every frame is gone, and unchanged melodic
        // state leaves no Off+On boundary at the same tick.
        var duplicateTriggers = noteOns
            .GroupBy(n => (n.Channel, n.Tick, ((NoteOnEvent)n.Event).NoteNumber))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.Empty(duplicateTriggers);

        // INV4: the SCC multi-byte frequency settle means no cluster of tiny
        // glitch notes — every note lives for more than one MIDI tick.
        foreach (var ep in decoded.Events)
        {
            var offs = ep.Value
                .Where(e => e.Event is NoteOffEvent)
                .Select(e => e.Tick)
                .ToHashSet();
            foreach (var e in ep.Value)
            {
                if (e.Event is not NoteOnEvent on || on.Velocity == 0)
                    continue;
                long duration = offs.Where(o => o > e.Tick).DefaultIfEmpty(long.MaxValue).Min() - e.Tick;
                Assert.True(duration > 1,
                    $"glitch note: channel {ep.Key.Channel} note {on.NoteNumber} at tick {e.Tick} lasts {duration} tick(s)");
            }
        }
    }

    [Fact]
    public void TriumphalArch_Export_SmfParses()
    {
        VisualizationTimeline timeline = Capture("testfixtures/53-triumphal-arch.vgz");
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        MidiFile midi = MidiFile.Read(new MemoryStream(exporter.Export(timeline).Bytes));
        Assert.True(midi.GetTrackChunks().Count() >= 2, "conductor + at least one musical track");
    }

    [Fact]
    public void TriumphalArch_Export_EmitsStructuralMarkers()
    {
        VisualizationTimeline timeline = Capture("testfixtures/53-triumphal-arch.vgz");
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(build.Map, timeline);
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
            Structure = structure,
        };
        MidiFile midi = MidiFile.Read(new MemoryStream(exporter.Export(timeline).Bytes));
        string[] markers = midi.GetTrackChunks()
            .SelectMany(c => c.Events)
            .OfType<MarkerEvent>()
            .Select(m => m.Text)
            .ToArray();

        // Every labeled section is emitted in analyzer order; loop markers appear
        // exactly when the analyzer found a fundamental loop.
        string[] sectionMarkers = markers.Where(m => m.StartsWith("SECTION_")).ToArray();
        Assert.Equal(structure.Sections.Select(s => "SECTION_" + s.Label), sectionMarkers);
        Assert.Equal(structure.HasLoop, markers.Contains("STRUCT_LOOP_START"));
        Assert.Equal(structure.HasLoop, markers.Contains("STRUCT_LOOP_END"));
    }

    private static VisualizationTimeline Capture(string fixture)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fixture);
        Assert.True(File.Exists(path), $"missing fixture: {path}");

        var fileInfo = new FileInfo(path);
        var registry = PlaybackBackendRegistry.CreateDefault();
        Assert.True(registry.TrySelect(fileInfo, new PlaybackEnvironment([]), out IPlaybackBackend backend, out _),
            $"no backend for {path}");

        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-triumphal-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(Sr);
        try
        {
            using IPlaybackCaptureSession session = backend.Open(
                fileInfo,
                new PlaybackOptions(
                    LoopCount: 1,
                    FadeSeconds: 0,
                    TailSeconds: 0,
                    OutputAudioPath: wav,
                    SampleRate: Sr),
                sink);
            session.Run();
            return sink.Complete(session.SamplePosition, "triumphal-arch");
        }
        finally
        {
            if (File.Exists(wav))
                File.Delete(wav);
        }
    }
}
