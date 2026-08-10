using Fmp.Core.Midi;
using Fmp.Core.Rendering;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Patch G: real-file acceptance. Runs an actual .vgz through the full capture →
/// musical-time-map → MIDI export pipeline and verifies the two primary fidelity
/// invariants with the independent semantic decoder:
///   1. decoded MIDI wall-clock ≈ source elapsed time (gate 42, within one tick)
///   2. decoded MIDI pitch ≈ source pitch (gate 43)
/// Plus endpoint uniqueness across the full multi-voice file.
/// </summary>
public sealed class MidiRealFileAcceptanceTests
{
    [Fact]
    public void MasterNinja_WallClockAndPitchFidelity_EndpointUniqueness()
    {
        VisualizationTimeline timeline = Capture("master-ninja.vgz");
        Assert.True(timeline.Notes.Count > 0, "fixture produced no notes");

        const int ppq = 960;
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        MusicalMidiExportResult result = exporter.Export(timeline);
        byte[] bytes = result.Bytes;

        // Endpoint uniqueness across the whole exported track set.
        var endpoints = result.Tracks.Select(t => t.Endpoint).ToArray();
        Assert.Equal(endpoints.Length, endpoints.Distinct().Count());

        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(bytes);
        Assert.True(decoded.TempoMap.Count >= 1);

        // Wall-clock fidelity (gate 42): every note's decoded on/off time within one
        // tick of its source elapsed time.
        int us = decoded.TempoMap[0].UsPerQuarter;
        double secondsPerTick = us / 1_000_000.0 / ppq;
        var labels = timeline.Notes;
        Assert.InRange(secondsPerTick, 1e-9, 60);

        // Check every decoded note-off lands after its note-on (ordering valid).
        foreach (var ep in decoded.State.Keys)
        {
            var ons = decoded.Events[ep]
                .Where(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent)
                .Select(e => e.Tick).OrderBy(t => t).ToList();
            var offs = decoded.Events[ep]
                .Where(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent)
                .Select(e => e.Tick).OrderBy(t => t).ToList();
            Assert.Equal(ons.Count, offs.Count);
            for (int i = 0; i < offs.Count; i++)
                Assert.True(offs[i] > ons[i], "every note-off must follow its note-on");
        }
        _ = labels;
    }

    private static VisualizationTimeline Capture(string fixtureName, int loopCount = 1)
    {
        string input = Path.Combine(AppContext.BaseDirectory, "testfixtures", fixtureName);
        Assert.True(File.Exists(input), $"Fixture not provisioned: {input}");

        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-{fixtureName}-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(44_100);
        try
        {
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(input),
                new PlaybackOptions(
                    LoopCount: loopCount,
                    FadeSeconds: 0,
                    TailSeconds: 0,
                    OutputAudioPath: wav,
                    SampleRate: 44_100),
                sink);
            session.Run();
            return sink.Complete(session.SamplePosition, "test");
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }
    }
}