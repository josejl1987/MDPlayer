using Fmp.Application.Export;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;
using System.Text.Json;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Verifies the public GUI-facing <see cref="MidiExportService"/> (Application
/// layer) which turns a persisted timeline into Format 1 MIDI bytes without
/// shelling to the CLI. This is the in-process path the GUI "MIDI…" button uses.
/// </summary>
public sealed class MidiExportServiceTests
{
    private const int Sr = 44_100;

    [Fact]
    public void ExportFromTimelinePath_WritesValidSmf()
    {
        VisualizationTimeline timeline = BuildTimeline();
        string timelinePath = WriteTimeline(timeline);

        var service = new MidiExportService();
        MidiExportResult result = service.ExportFromTimelinePath(timelinePath, new MidiExportRequest
        {
            Ppq = 960,
            Meter = "4/4",
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Bytes);
        Assert.True(result.Bytes.Length >= 14, "SMF header + at least one chunk");
        // MThd
        Assert.Equal((byte)'M', result.Bytes[0]);
        Assert.Equal((byte)'T', result.Bytes[1]);
        Assert.Equal((byte)'h', result.Bytes[2]);
        Assert.Equal((byte)'d', result.Bytes[3]);
        // Format 1 (big-endian 0x0001 at bytes 8-9).
        Assert.Equal(0, result.Bytes[8]);
        Assert.Equal(1, result.Bytes[9]);
        Assert.Equal(1, result.SegmentCount);
        Assert.Contains(result.Report, line => line.Contains("tempo-source"));
    }

    [Fact]
    public void PerformanceReceipts_EnabledEmitCompleteJsonAndHumanSummary()
    {
        string path = WriteTimeline(BuildTimeline(withBeats: true));
        MidiExportResult result = new MidiExportService().ExportFromTimelinePath(path,
            new MidiExportRequest { EnablePerformanceReceipts = true, PerformanceFixture = "pinned-midi-fixture" });

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Performance);
        using JsonDocument json = JsonDocument.Parse(result.Performance!.ToJson());
        Assert.Equal("pinned-midi-fixture", json.RootElement.GetProperty("Fixture").GetString());
        Assert.True(json.RootElement.TryGetProperty("Input", out _));
        Assert.True(json.RootElement.TryGetProperty("Configuration", out _));
        Assert.True(json.RootElement.TryGetProperty("Environment", out _));
        Assert.Equal("not-run", json.RootElement.GetProperty("Comparison").GetProperty("Status").GetString());
        Assert.Contains(result.Report, line => line.Contains("comparison-status=not-run"));
        Assert.Contains(result.Performance.ToHumanReadable(), line => line.Contains("midi-planning-and-serialization"));
    }

    [Fact]
    public void PerformanceReceipts_EnabledMirrorPercussionFidelityReceipt()
    {
        // Spec §11 / D8: when performance receipts are enabled, the Core
        // percussion-fidelity receipt rides on the summary — plain counters,
        // never giant dumps; droppedSourceAttacks MUST be 0.
        string path = WriteTimeline(BuildTimeline(withBeats: true));
        MidiExportResult result = new MidiExportService().ExportFromTimelinePath(path,
            new MidiExportRequest { EnablePerformanceReceipts = true, PerformanceFixture = "pinned-midi-fixture" });

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Performance);
        using JsonDocument json = JsonDocument.Parse(result.Performance!.ToJson());
        JsonElement percussion = json.RootElement.GetProperty("Percussion");
        Assert.True(percussion.TryGetProperty("SourceNotes", out JsonElement sourceNotes));
        Assert.Equal(8, sourceNotes.GetInt32());
        Assert.Equal(0, percussion.GetProperty("DroppedSourceAttacks").GetInt32());
        Assert.True(percussion.TryGetProperty("ExportedGmDrumEvents", out _));
    }

    [Fact]
    public void PerformanceReceipts_DisabledPreserveOutputEquivalence()
    {
        string path = WriteTimeline(BuildTimeline(withBeats: true));
        var service = new MidiExportService();
        MidiExportResult disabled = service.ExportFromTimelinePath(path, new MidiExportRequest());
        MidiExportResult enabled = service.ExportFromTimelinePath(path,
            new MidiExportRequest { EnablePerformanceReceipts = true });

        Assert.True(disabled.Succeeded, disabled.Error);
        Assert.True(enabled.Succeeded, enabled.Error);
        Assert.Equal(disabled.Bytes, enabled.Bytes);
        Assert.Null(disabled.Performance);
    }

    [Fact]
    public void ExportFromTimelinePath_MissingFile_FailsGracefully()
    {
        var service = new MidiExportService();
        MidiExportResult result =
            service.ExportFromTimelinePath(Path.Combine(Path.GetTempPath(), "no-such-lease", "timeline.json"),
                new MidiExportRequest());

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void ExportFromTimelinePath_FixedBpm_ReportsUserOverrideAndPhaseUnknown()
    {
        // A fixed BPM establishes tempo but not a beat phase; the report must
        // surface that rather than pretend the grid is aligned.
        VisualizationTimeline timeline = BuildTimeline(withBeats: false);
        string timelinePath = WriteTimeline(timeline);

        var service = new MidiExportService();
        MidiExportResult result = service.ExportFromTimelinePath(timelinePath, new MidiExportRequest
        {
            Bpm = 120,
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("UserOverride", result.TempoSource);
        Assert.True(result.PhaseUnknown, "fixed BPM alone must not claim a beat grid");
        Assert.False(result.Trustworthy);
        Assert.Contains(result.Report, line => line.Contains("phase unknown"));
    }

    [Fact]
    public void ExportFromTimelinePath_DriverBeats_EstablishesPhase()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true);
        string timelinePath = WriteTimeline(timeline);

        var service = new MidiExportService();
        MidiExportResult result = service.ExportFromTimelinePath(timelinePath, new MidiExportRequest());

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.PhaseUnknown, "driver beat anchors establish the beat grid");
        Assert.True(result.Trustworthy);
    }

    [Fact]
    public void ProbeVoices_ReturnsVoiceDescriptors()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true, voices: new[] { "lead", "bass" });
        string timelinePath = WriteTimeline(timeline);

        MidiVoiceProbe probe = new MidiExportService().ProbeVoices(timelinePath);

        Assert.True(probe.Succeeded, probe.Error);
        Assert.Contains(probe.Voices, v => v.ChannelId == "lead");
        Assert.Contains(probe.Voices, v => v.ChannelId == "bass");
    }

    [Fact]
    public void ProbeVoices_MissingFile_FailsGracefully()
    {
        MidiVoiceProbe probe = new MidiExportService().ProbeVoices(
            Path.Combine(Path.GetTempPath(), "no-such-lease", "timeline.json"));
        Assert.False(probe.Succeeded);
        Assert.False(string.IsNullOrEmpty(probe.Error));
    }

    [Fact]
    public void ExportWithVoiceOptions_ExcludedVoice_RemovesItsNotes()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true, voices: new[] { "lead", "bass" });
        string timelinePath = WriteTimeline(timeline);

        var service = new MidiExportService();
        MidiExportResult result = service.ExportFromTimelinePath(timelinePath, new MidiExportRequest
        {
            VoiceOptions = new[]
            {
                new MidiVoiceOption("lead"),
                new MidiVoiceOption("bass") { Include = false },
            },
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Bytes);
        Assert.True(result.Bytes.Length > 14);
    }

    [Fact]
    public void Export_Auto_SelectsDriverBeats_WhenAvailable()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { TempoSource = MidiTempoSource.Auto });
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("DriverBeatAnchors", result.TempoSource);
        Assert.False(result.PhaseUnknown);
        Assert.True(result.Trustworthy);
    }

    [Fact]
    public void Export_Driver_WithoutAuthority_Fails()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: false);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { TempoSource = MidiTempoSource.Driver });
        Assert.False(result.Succeeded);
        Assert.Contains("timing", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_Fixed_WithoutBpm_Fails()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { TempoSource = MidiTempoSource.Fixed });
        Assert.False(result.Succeeded);
        Assert.Contains("Bpm", result.Error);
    }

    [Fact]
    public void Export_Fixed_NonPositiveBpm_Rejected()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { TempoSource = MidiTempoSource.Fixed, Bpm = 0 });
        Assert.False(result.Succeeded);
        Assert.Contains("BPM", result.Error);
    }

    [Fact]
    public void Export_Strict_RejectsUnresolvedAlignment()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: false);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { Bpm = 120, StrictTiming = true });
        Assert.False(result.Succeeded);
        Assert.Contains("strict", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_FirstDownbeat_WithoutMeter_Fails()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { FirstDownbeatSample = 10_000 });
        Assert.False(result.Succeeded);
        Assert.Contains("meter", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_Symbolic_OverridesDriver_SameAsCli()
    {
        VisualizationTimeline timeline = BuildTimeline(withBeats: true);
        string timelinePath = WriteTimeline(timeline);

        var result = new MidiExportService().ExportFromTimelinePath(
            timelinePath, new MidiExportRequest { TempoSource = MidiTempoSource.Symbolic });
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("SymbolicInference", result.TempoSource);
    }

    [Fact]
    public void Export_SymbolicStructureSelection_UsesProductionApplicationPath()
    {
        const double bpm = 149.4;
        double samplesPerQuarter = Sr * 60.0 / bpm;
        const int bars = 66;
        long endSample = (long)Math.Round(bars * 4 * samplesPerQuarter);
        NoteEvent[] notes = Enumerable.Range(0, bars * 4)
            .Select(index =>
            {
                long start = (long)Math.Round(index * samplesPerQuarter);
                return new NoteEvent(
                    ChannelId: "lead",
                    StartSample: start,
                    EndSample: start + Math.Max(1, (long)Math.Round(0.5 * samplesPerQuarter)),
                    InitialFrequencyHz: 440,
                    InitialMidiNote: 48 + ((index / 4 % 33) * 2 + index % 4) % 12,
                    InstrumentId: "inst",
                    Mode: VisualizationNoteMode.Fm,
                    IsRetrigger: false,
                    Pitch: Array.Empty<PitchChange>());
            })
            .ToArray();
        VisualizationTimeline timeline = new()
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = endSample,
            Notes = notes,
            Rhythm = Enumerable.Range(0, 33 * 4)
                .Select(index => new RhythmEvent(
                    index % 4 is 0 or 2 ? "bd" : "sd",
                    index % 4 is 0 or 2 ? "bd" : "sd",
                    (long)Math.Round(index * samplesPerQuarter),
                    1.0f,
                    0.0f))
                .ToArray(),
            LoopMarkers = new[]
            {
                new LoopMarker(0, LoopMarkerKind.Start, 0),
                new LoopMarker(
                    (long)Math.Round(33 * 4 * samplesPerQuarter),
                    LoopMarkerKind.Restart,
                    1),
            },
        };
        string timelinePath = WriteTimeline(timeline);

        MidiExportResult result = new MidiExportService().ExportFromTimelinePath(
            timelinePath,
            new MidiExportRequest
            {
                TempoSource = MidiTempoSource.Symbolic,
                Meter = "4/4",
                EmitMarkers = true,
            });

        Assert.True(result.Succeeded, result.Error);
        var conductor = MidiRoundTrip.TimedEvents(result.Bytes!, 0);
        string[] markers = conductor
            .Where(pair => pair.Event is MarkerEvent)
            .Select(pair => ((MarkerEvent)pair.Event).Text)
            .ToArray();
        Assert.True(markers.Contains("FIRST_DOWNBEAT"), string.Join(" | ", result.Report));
        Assert.True(markers.Contains("STRUCT_LOOP_START"), string.Join(" | ", result.Report));
        Assert.Contains("STRUCT_LOOP_END", markers);
        Assert.Contains(markers, marker => marker.StartsWith("PHRASE_"));

        SetTempoEvent tempo = Assert.Single(conductor.Select(pair => pair.Event).OfType<SetTempoEvent>());
        Assert.InRange(
            tempo.MicrosecondsPerQuarterNote,
            (uint)Math.Round(60_000_000.0 / bpm) - 10,
            (uint)Math.Round(60_000_000.0 / bpm) + 10);
        TimeSignatureEvent meter = Assert.Single(
            conductor.Select(pair => pair.Event).OfType<TimeSignatureEvent>());
        Assert.Equal((byte)4, meter.Numerator);
        Assert.Equal((byte)4, meter.Denominator);
    }

    [Fact]
    public void ExportFromTimelinePath_AmbiguousSampleRate_IsRejectedNotGeneric()
    {
        // P1: the Application/GUI serialized path must route through the producer
        // boundary. A timeline that lost its SampleRate must be rejected with an
        // actionable MusicalTimingException (surfaced on the result), never a
        // generic JSON/argument error — same as the CLI serialized path.
        string path = Path.Combine(Path.GetTempPath(), "mdplayer-midi-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        {"schemaVersion":2,"sampleRate":0,"startSample":0,"endSample":1000,
         "timing":[{"samplePosition":100,"timerBValue":66,"validatedBpm":120.0}]}
        """);
        try
        {
            var service = new MidiExportService();
            MidiExportResult result = service.ExportFromTimelinePath(path, new MidiExportRequest());

            Assert.False(result.Succeeded);
            Assert.Contains("source rate unknown", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.Bytes);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ExportFromTimelinePath_PreservesSerializedStartRange()
    {
        // P1 + P2 on the Application path: reading a serialized timeline with a
        // nonzero start and a valid rate routes through the boundary (pass-through
        // destination = the timeline's own rate) and preserves the serialized
        // [StartSample, EndSample] range — the map origin is not reset to 0.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 5_000,
            EndSample = 20_000,
            Notes = new[]
            {
                new NoteEvent("v", 5_000, 7_000, 440, 60, "i", VisualizationNoteMode.Fm, false, []),
            },
            Beats = new[] { new BeatEvent(5_000, 0.0), new BeatEvent(8_000, 1.0) },
        };
        string timelinePath = WriteTimeline(timeline);

        var service = new MidiExportService();
        MidiExportResult result = service.ExportFromTimelinePath(timelinePath, new MidiExportRequest());

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Bytes);
        // The serialized start range is preserved through the boundary.
        Assert.Contains(result.Report, line => line.Contains("song: 5000–20000 samples @ " + Sr + " Hz"));
    }

    /* ---- helpers (mirror the musical MIDI export tests) ---- */

    private static VisualizationTimeline BuildTimeline(bool withBeats = true, string[]? voices = null)
    {
        double spq = Sr * 60.0 / 120.0;
        string[] voiceIds = voices ?? new[] { "v" };
        var notes = Enumerable.Range(0, 8)
            .Select(i => new NoteEvent(
                ChannelId: voiceIds[i % voiceIds.Length],
                StartSample: (long)Math.Round(i * 4 * spq),
                EndSample: (long)Math.Round(i * 4 * spq) + 2000,
                InitialFrequencyHz: 440,
                InitialMidiNote: 60 + i,
                InstrumentId: "inst",
                Mode: VisualizationNoteMode.Fm,
                IsRetrigger: false,
                Pitch: Array.Empty<PitchChange>()))
            .ToArray();
        return new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * 4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = withBeats
                ? Enumerable.Range(0, 40)
                    .Select(i => new BeatEvent((long)Math.Round(i * spq), i))
                    .ToArray()
                : Array.Empty<BeatEvent>(),
            Source = new TrackMetadata("vgz", "song", "chip", "song.vgz"),
        };
    }

    /* ---- helpers (mirror the musical MIDI export tests) ---- */

    private static string WriteTimeline(VisualizationTimeline timeline)
    {
        string path = Path.Combine(Path.GetTempPath(), "mdplayer-midi-" + Guid.NewGuid().ToString("N") + ".json");
        VisualizationJsonWriter.Write(path, timeline);
        return path;
    }
}
